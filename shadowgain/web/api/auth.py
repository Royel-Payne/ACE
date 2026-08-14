"""Shadowgain 124 — login, sessions and brute-force protection for my.shadowgain.com.

THREE RULES, ALL LOAD-BEARING

1. **VERIFY ONLY. NEVER WRITE.** The server's own `AccountExtensions.PasswordMatches` re-hashes
   and saves on two paths — a work-factor migration, and the SHA512-to-bcrypt upgrade. Neither is
   reproduced here. A web login must not be able to change a credential, and a read-only DB user
   means it cannot even if this code tried. A legacy-hash account is REFUSED with an explanation
   rather than migrated; the game client migrates it on the player's next in-game login, which is
   where that belongs.

2. **BRUTE-FORCE PROTECTION IS OURS TO ADD.** ACE never had any: `PasswordMatches` will happily
   answer a thousand guesses a second. In game that is throttled by the login handshake; over
   HTTPS it is not throttled by anything, so the lockout below is the only thing between a
   dictionary and 25 real accounts.

3. **NOTHING SENSITIVE LEAVES.** The session cookie carries an account id and name — never the
   hash, never the email, never an IP. The queries are column-scoped to match.

WHY THE COUNTERS ARE IN MEMORY

A failed-login counter is state, and the only place this service is allowed to write is its own
process. Persisting it would mean either a writable database (forbidden) or a file (a new failure
mode for no benefit at this scale). The cost is honest and worth stating: a service restart
clears the lockouts. With a single-digit player count and a 25-account universe that is an
acceptable trade; if this ever grows, the fix is Redis, not a game-DB table.
"""

from __future__ import annotations

import os
import threading
import time
from dataclasses import dataclass, field

import bcrypt
from itsdangerous import BadSignature, SignatureExpired, URLSafeTimedSerializer

from . import db

# The marker ACE writes into passwordSalt to mean "this hash is bcrypt". Anything else is a
# legacy SHA512 hash and this service refuses it. See AccountExtensions.PasswordMatches.
BCRYPT_SALT_MARKER = "use bcrypt"

SESSION_COOKIE = "sg_session"

# Standard lifetime, and the longer one behind "remember me" (decision 4 in Task.md 123).
SESSION_MAX_AGE = int(os.environ.get("SG_WEB_SESSION_HOURS", "12")) * 3600
SESSION_REMEMBER_AGE = int(os.environ.get("SG_WEB_REMEMBER_DAYS", "30")) * 86400

# Lockout shape. Five tries is generous for someone typing their own password and hopeless for a
# dictionary; fifteen minutes makes an online attack cost more time than it can possibly be worth
# against a 25-account server.
MAX_ATTEMPTS = int(os.environ.get("SG_WEB_MAX_ATTEMPTS", "5"))
LOCKOUT_SECONDS = int(os.environ.get("SG_WEB_LOCKOUT_SECONDS", "900"))
ATTEMPT_WINDOW = int(os.environ.get("SG_WEB_ATTEMPT_WINDOW", "900"))


class AuthError(Exception):
    """Anything that stops a login, carrying the message the player should see."""

    def __init__(self, message: str, status: int = 401):
        super().__init__(message)
        self.message = message
        self.status = status


def _secret() -> str:
    secret = os.environ.get("SG_WEB_SECRET_KEY")

    if not secret:
        # Generating one on the fly would "work" and then log everybody out on every restart,
        # which reads as a session bug rather than as missing configuration.
        raise db.ConfigError("SG_WEB_SECRET_KEY is not set")

    return secret


def _serializer() -> URLSafeTimedSerializer:
    return URLSafeTimedSerializer(_secret(), salt="sg-web-session")


# ---------------------------------------------------------------------------------------------
# rate limiting
# ---------------------------------------------------------------------------------------------


@dataclass
class _Bucket:
    failures: list[float] = field(default_factory=list)
    locked_until: float = 0.0


class RateLimiter:
    """Failed-login tracking, keyed independently on client IP and on account name.

    Both keys matter and neither is sufficient alone: IP-only lets one attacker spray 25 accounts
    from 25 addresses, account-only lets an attacker lock a player out of their own account on
    purpose. Tracking both means a spray trips the IP bucket while a targeted guess trips the
    account bucket, and a real player fat-fingering their password trips neither for long.
    """

    def __init__(self) -> None:
        self._buckets: dict[str, _Bucket] = {}
        self._lock = threading.Lock()

    def _prune(self, bucket: _Bucket, now: float) -> None:
        bucket.failures = [t for t in bucket.failures if now - t < ATTEMPT_WINDOW]

    def check(self, keys: list[str]) -> int:
        """Seconds remaining on a lockout, or 0 if the caller may try."""
        now = time.time()
        worst = 0

        with self._lock:
            for key in keys:
                bucket = self._buckets.get(key)

                if bucket and bucket.locked_until > now:
                    worst = max(worst, int(bucket.locked_until - now))

        return worst

    def record_failure(self, keys: list[str]) -> None:
        now = time.time()

        with self._lock:
            for key in keys:
                bucket = self._buckets.setdefault(key, _Bucket())

                self._prune(bucket, now)
                bucket.failures.append(now)

                if len(bucket.failures) >= MAX_ATTEMPTS:
                    bucket.locked_until = now + LOCKOUT_SECONDS
                    bucket.failures.clear()

    def record_success(self, keys: list[str]) -> None:
        """A correct password clears the counters — a player who finally remembers it should not
        stay one typo away from a lockout."""
        with self._lock:
            for key in keys:
                self._buckets.pop(key, None)

    def sweep(self) -> None:
        """Drop buckets that are neither locked nor holding a recent failure.

        Without this the dict is an unbounded map of every IP that ever guessed wrong — small,
        but it only ever grows, and this service is meant to run for months untouched.
        """
        now = time.time()

        with self._lock:
            dead = [
                key
                for key, bucket in self._buckets.items()
                if bucket.locked_until <= now
                and not [t for t in bucket.failures if now - t < ATTEMPT_WINDOW]
            ]

            for key in dead:
                self._buckets.pop(key, None)


limiter = RateLimiter()


# ---------------------------------------------------------------------------------------------
# credential verification
# ---------------------------------------------------------------------------------------------


def _fetch_account(account_name: str) -> dict | None:
    """The login row, and nothing beyond it.

    The column list is the security boundary in this function: `email_Address`, the two IP
    columns and the ban text are never selected, so they cannot leak through a logged exception
    or a debug repr. accessLevel comes along because staff accounts are excluded from public
    surfaces elsewhere and the same rule should be available here.
    """
    with db.auth() as cur:
        return db.fetch_one(
            cur,
            """
            SELECT accountId, accountName, passwordHash, passwordSalt, accessLevel,
                   banned_Time, ban_Expire_Time
            FROM account
            WHERE accountName = %s
            """,
            (account_name,),
        )


def verify_credentials(account_name: str, password: str, client_ip: str) -> dict:
    """Check a game password and return `{accountId, accountName, accessLevel}`.

    Raises AuthError for every failure mode, with a message safe to show a stranger.
    """
    account_name = (account_name or "").strip()

    if not account_name or not password:
        raise AuthError("Enter your account name and password.", status=400)

    keys = [f"ip:{client_ip}", f"acct:{account_name.lower()}"]

    remaining = limiter.check(keys)

    if remaining:
        minutes = max(1, remaining // 60)
        raise AuthError(
            f"Too many failed attempts. Try again in about {minutes} minute"
            f"{'s' if minutes != 1 else ''}.",
            status=429,
        )

    account = _fetch_account(account_name)

    # One message for "no such account" and for "wrong password". Distinguishing them would hand
    # an attacker a free account-name oracle against a 25-account server.
    generic = "That account name and password do not match."

    if account is None:
        # Still spend the time a real verify costs. Returning instantly on an unknown name is a
        # timing oracle for the same thing the shared message is hiding.
        bcrypt.checkpw(b"timing", b"$2y$08$" + b"." * 53)
        limiter.record_failure(keys)
        raise AuthError(generic)

    if account.get("passwordSalt") != BCRYPT_SALT_MARKER:
        # A legacy SHA512 account. The server migrates these on the next in-game login; this
        # service must not, because migrating is a WRITE. Say so plainly — a player told only
        # "wrong password" would keep retrying a password that is in fact correct.
        raise AuthError(
            "This account still uses the old password format. Log in to the game once and it "
            "will upgrade automatically, then this will work.",
            status=409,
        )

    password_hash = (account.get("passwordHash") or "").encode("utf-8")

    try:
        ok = bcrypt.checkpw(password.encode("utf-8"), password_hash)
    except ValueError:
        # A malformed stored hash. Not the player's fault and not something they can fix.
        raise AuthError(
            "This account's password could not be checked. Please contact staff.", status=500
        ) from None

    if not ok:
        limiter.record_failure(keys)
        raise AuthError(generic)

    if _is_banned(account):
        raise AuthError("This account is suspended.", status=403)

    limiter.record_success(keys)

    return {
        "accountId": account["accountId"],
        "accountName": account["accountName"],
        "accessLevel": account.get("accessLevel", 0),
    }


def _is_banned(account: dict) -> bool:
    """A ban is live when banned_Time is set and the expiry is absent or in the future.

    ACE stores both as datetimes and treats a null expiry as permanent.
    """
    banned_time = account.get("banned_Time")

    if not banned_time:
        return False

    expires = account.get("ban_Expire_Time")

    if expires is None:
        return True

    import datetime

    return expires > datetime.datetime.now(tz=expires.tzinfo)


# ---------------------------------------------------------------------------------------------
# sessions
# ---------------------------------------------------------------------------------------------


def issue_session(account: dict, remember: bool) -> tuple[str, int]:
    """A signed cookie value and its max-age.

    Stateless on purpose. A server-side session store would need somewhere to write, and the one
    hard rule of this service is that it writes to no database. itsdangerous signs and timestamps
    the payload, so the cookie cannot be forged and expires on its own.
    """
    max_age = SESSION_REMEMBER_AGE if remember else SESSION_MAX_AGE

    token = _serializer().dumps(
        {
            "id": account["accountId"],
            "name": account["accountName"],
            "lvl": account.get("accessLevel", 0),
        }
    )

    return token, max_age


def read_session(token: str | None, max_age: int = SESSION_REMEMBER_AGE) -> dict | None:
    """Decode a session cookie, or None if it is missing, tampered with, or expired.

    `max_age` defaults to the longest lifetime issued, because the cookie itself does not record
    which of the two it was — the browser drops the short one on its own schedule, and a
    "remember me" cookie presented inside 30 days is exactly as valid as it claims to be.
    """
    if not token:
        return None

    try:
        return _serializer().loads(token, max_age=max_age)
    except (BadSignature, SignatureExpired):
        return None
