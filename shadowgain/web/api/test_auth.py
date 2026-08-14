"""Shadowgain 124 — tests for the login path.

    cd shadowgain/web && python -m pytest api/test_auth.py -v
    (or: python api/test_auth.py, which runs them without pytest)

WHY THIS FILE EXISTS

Every OTHER part of this service was verified against production data directly: the rank maths
against Black Breath's real rows, the payload against the live shard, the read-only grant by
trying a write and watching it fail. The login SUCCESS path cannot be verified that way, because
doing so would need a real player's password — and nothing here is allowed to write one.

So the DB row is faked and everything else is real: a genuine `$2y$08$` bcrypt hash in exactly
the format LIVE stores (all 25 accounts are `$2y$08$`), run through the actual
`verify_credentials`, with the actual salt-marker check, ban check and rate limiter.

WHAT THIS DOES NOT PROVE: that the hash of any specific real account verifies. That needs one
human logging in once, and it is the single item left for Chris.
"""

from __future__ import annotations

import time

import bcrypt

from . import auth


def _make_account(password: str, *, bcrypt_salt: bool = True, banned=None, expires=None) -> dict:
    """A row shaped exactly like the one `_fetch_account` selects.

    The hash is minted at work factor 8 with the 2y identifier, because that is what
    BCryptProvider produces (it forces 2y explicitly) and what LIVE actually holds. Testing
    against a 2b hash would pass while leaving the real format untested — the whole point.
    """
    raw = bcrypt.hashpw(password.encode(), bcrypt.gensalt(8, prefix=b"2b"))
    hashed = raw.replace(b"$2b$", b"$2y$").decode()

    return {
        "accountId": 42,
        "accountName": "TestAccount",
        "passwordHash": hashed,
        "passwordSalt": "use bcrypt" if bcrypt_salt else "somelegacysaltvalue",
        "accessLevel": 0,
        "banned_Time": banned,
        "ban_Expire_Time": expires,
    }


class _Patch:
    """Swap `_fetch_account` for a stub, and give each test a clean rate limiter."""

    def __init__(self, account):
        self.account = account

    def __enter__(self):
        self._real = auth._fetch_account
        auth._fetch_account = lambda name: self.account
        # A fresh limiter per test: these tests deliberately fail logins, and a shared limiter
        # would lock out later tests for reasons that have nothing to do with what they check.
        self._limiter = auth.limiter
        auth.limiter = auth.RateLimiter()
        return self

    def __exit__(self, *exc):
        auth._fetch_account = self._real
        auth.limiter = self._limiter
        return False


def test_correct_password_verifies():
    """The path that has never run against a real account."""
    with _Patch(_make_account("correct horse battery staple")):
        result = auth.verify_credentials("TestAccount", "correct horse battery staple", "1.2.3.4")

    assert result["accountId"] == 42
    assert result["accountName"] == "TestAccount"
    # The hash must never travel with the result — it goes into a session cookie.
    assert "passwordHash" not in result
    assert "passwordSalt" not in result


def test_wrong_password_is_rejected():
    with _Patch(_make_account("correct horse battery staple")):
        try:
            auth.verify_credentials("TestAccount", "wrong", "1.2.3.4")
            raise AssertionError("a wrong password was accepted")
        except auth.AuthError as ex:
            assert ex.status == 401
            # The message must not distinguish "no such account" from "wrong password", or it
            # becomes an account-name oracle against a 25-account server.
            assert "do not match" in ex.message


def test_unknown_account_gives_the_same_message():
    with _Patch(None):
        try:
            auth.verify_credentials("NoSuchAccount", "whatever", "1.2.3.4")
            raise AssertionError("an unknown account was accepted")
        except auth.AuthError as ex:
            assert ex.status == 401
            assert "do not match" in ex.message


def test_legacy_hash_is_refused_not_migrated():
    """A SHA512 account must be told what to do, not silently failed.

    The server migrates these on the next in-game login. This service must not, because
    migrating is a WRITE — and a player told only "wrong password" would keep retrying a
    password that is in fact correct.
    """
    with _Patch(_make_account("hunter2", bcrypt_salt=False)):
        try:
            auth.verify_credentials("TestAccount", "hunter2", "1.2.3.4")
            raise AssertionError("a legacy-hash account was accepted")
        except auth.AuthError as ex:
            assert ex.status == 409
            assert "old password format" in ex.message


def test_banned_account_cannot_read_its_own_sheet():
    import datetime

    forever = _make_account("pw", banned=datetime.datetime(2026, 1, 1), expires=None)

    with _Patch(forever):
        try:
            auth.verify_credentials("TestAccount", "pw", "1.2.3.4")
            raise AssertionError("a banned account logged in")
        except auth.AuthError as ex:
            assert ex.status == 403


def test_expired_ban_can_log_in_again():
    import datetime

    lapsed = _make_account(
        "pw", banned=datetime.datetime(2026, 1, 1), expires=datetime.datetime(2026, 1, 2)
    )

    with _Patch(lapsed):
        result = auth.verify_credentials("TestAccount", "pw", "1.2.3.4")

    assert result["accountId"] == 42


def test_lockout_after_repeated_failures():
    """ACE has no brute-force protection of its own; this is the only thing there is."""
    with _Patch(_make_account("pw")):
        for _ in range(auth.MAX_ATTEMPTS):
            try:
                auth.verify_credentials("TestAccount", "wrong", "9.9.9.9")
            except auth.AuthError:
                pass

        try:
            # Even the RIGHT password must now be refused — otherwise the lockout is decorative.
            auth.verify_credentials("TestAccount", "pw", "9.9.9.9")
            raise AssertionError("locked-out client was still served")
        except auth.AuthError as ex:
            assert ex.status == 429


def test_lockout_is_per_client_not_global():
    """One person mistyping their password must not lock out everybody else."""
    with _Patch(_make_account("pw")):
        for _ in range(auth.MAX_ATTEMPTS):
            try:
                auth.verify_credentials("TestAccount", "wrong", "9.9.9.9")
            except auth.AuthError:
                pass

        # Same account, different address. The account bucket has tripped, so this is expected
        # to be refused too — which is the DESIGNED behaviour for a targeted guess, and the
        # reason both keys exist. What must NOT happen is an unrelated account being affected.
        other = _make_account("pw2")
        other["accountName"] = "SomeoneElse"

        with _Patch(other):
            result = auth.verify_credentials("SomeoneElse", "pw2", "5.5.5.5")

        assert result["accountName"] == "SomeoneElse"


def test_session_round_trip():
    import os

    os.environ.setdefault("SG_WEB_SECRET_KEY", "test-secret-not-the-real-one")

    token, max_age = auth.issue_session(
        {"accountId": 7, "accountName": "Royel", "accessLevel": 0}, remember=False
    )

    session = auth.read_session(token)

    assert session["id"] == 7
    assert session["name"] == "Royel"
    assert max_age == auth.SESSION_MAX_AGE


def test_tampered_session_is_rejected():
    import os

    os.environ.setdefault("SG_WEB_SECRET_KEY", "test-secret-not-the-real-one")

    token, _ = auth.issue_session(
        {"accountId": 7, "accountName": "Royel", "accessLevel": 0}, remember=False
    )

    # Flip a character in the payload. Without a valid signature this must not decode — a forged
    # cookie is the one way into somebody else's private sheet.
    tampered = ("A" if token[0] != "A" else "B") + token[1:]

    assert auth.read_session(tampered) is None
    assert auth.read_session(None) is None
    assert auth.read_session("not-a-token") is None


def test_expired_session_is_rejected():
    import os

    os.environ.setdefault("SG_WEB_SECRET_KEY", "test-secret-not-the-real-one")

    token, _ = auth.issue_session(
        {"accountId": 7, "accountName": "Royel", "accessLevel": 0}, remember=False
    )

    # itsdangerous stamps whole seconds and expires on `age > max_age`, so a 1.1s sleep against
    # max_age=1 can compute age == 1 and still be considered valid. Sleep past the granularity
    # rather than tuning the assertion to a rounding artefact.
    time.sleep(2.2)

    assert auth.read_session(token, max_age=1) is None


if __name__ == "__main__":
    passed = failed = 0

    for name, fn in sorted(globals().items()):
        if not name.startswith("test_") or not callable(fn):
            continue

        try:
            fn()
            print(f"  PASS  {name}")
            passed += 1
        except Exception as ex:  # noqa: BLE001 - a test runner reports, it does not propagate
            print(f"  FAIL  {name}: {ex}")
            failed += 1

    print(f"\n{passed} passed, {failed} failed")

    raise SystemExit(1 if failed else 0)
