"""Shadowgain 124 — the read-only API behind my.shadowgain.com.

    uvicorn api.app:app --host 127.0.0.1 --port 8081

THE ONE RULE

This service NEVER writes to a game database. Not the shard, not auth, not world. There is no
INSERT/UPDATE/DELETE anywhere under api/, and the MySQL user it connects as (setup.sql) has
SELECT and nothing else — so the rule is enforced by the grant, not just by good intentions. It
follows that the whole thing is standalone infrastructure: no game-server code, nothing to
restart, and if this process dies the world does not notice.

WHAT IS PUBLIC AND WHAT IS NOT (Task.md 123)

  public   name, marker, title, gender, heritage, level, total XP, skills + true ranks,
           skill credits, titles
  private  everything above PLUS attributes, vitals, location, inventory, quests, playtime

The split is enforced by two separate builder functions returning two separate literals — see
payload.py. Not a flag, because a flag is one wrong boolean away from publishing where a player
keeps their character.

WHY THE STATIC FILES ARE SERVED BY CADDY AND NOT BY THIS

Caddy already terminates TLS for shadowgain.com and already serves a static root well. Putting
the front-end behind uvicorn would mean a Python process in the path of every icon request for
no benefit. So Caddy serves `/` and `/assets/*` from disk and reverse-proxies only `/api/*` here.
"""

from __future__ import annotations

import calendar
import json
import os
import time
import urllib.error
import urllib.request

from fastapi import Cookie, FastAPI, Request, Response
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from . import auth, cache, curves, db, payload

app = FastAPI(
    title="Shadowgain character portal",
    docs_url=None,      # nothing here is a public API surface worth documenting
    redoc_url=None,
    openapi_url=None,
)

# Where sitedata.sh publishes the live server status. Read over the existing public URL rather
# than re-deriving it: that feed is already generated, already cached at the edge, and already
# the thing the honour roll and the landing page agree with.
STATUS_URL = os.environ.get("SG_WEB_STATUS_URL", "https://shadowgain.com/data/status.json")

# Written by sitedata.sh from `listplayers`, OUTSIDE the web root — see online_names().
ONLINE_NAMES_PATH = os.environ.get("SG_WEB_ONLINE_NAMES", "/opt/ACE/online-names.json")

# sitedata.sh refreshes on a 30-second timer; two and a half minutes of slack absorbs a slow
# console round-trip without ever believing a feed that has actually stopped.
ONLINE_NAMES_MAX_AGE = 150

# Cookies are Secure because Caddy terminates TLS in front of this and the site is HTTPS-only.
# SameSite=Lax rather than Strict so a link into the site from Discord still arrives logged in.
COOKIE_KWARGS = dict(httponly=True, secure=True, samesite="lax", path="/")


# ---------------------------------------------------------------------------------------------
# dials — read from the live DB, never from a compiled default
# ---------------------------------------------------------------------------------------------

_dials_cache = cache.TTLCache(ttl=300)


def live_dials() -> dict:
    """The server settings the rank maths depends on, read from the SHARD's config tables.

    PropertyManager loads these rows over its compiled defaults, so the value in the C# source is
    only the fallback — reading it instead of the database is how you end up computing ranks on a
    curve the server stopped using. attribute_max_value in particular is a dial that has moved.

    Cached for five minutes: these change by hand, roughly never, and a per-request read of three
    config tables would be three round-trips for a constant.
    """

    def build() -> dict:
        with db.shard() as cur:
            bools = {
                r["key"]: db.as_bool(r["value"])
                for r in db.fetch_all(
                    cur,
                    "SELECT `key`, value FROM config_properties_boolean "
                    "WHERE `key` IN ('skill_uncap_ranks','attributes_start_at_ten',"
                    "                'burden_capacity_floor_enabled')",
                )
            }

            longs = {
                r["key"]: int(r["value"])
                for r in db.fetch_all(
                    cur,
                    "SELECT `key`, value FROM config_properties_long "
                    "WHERE `key` IN ('attribute_max_value','burden_capacity_floor')",
                )
            }

        return {
            # Defaults here match ACE's compiled ones and exist only for a database that has no
            # row at all — which is a real state for a dial nobody has ever changed.
            "skill_uncap_ranks": bools.get("skill_uncap_ranks", True),
            "attributes_start_at_ten": bools.get("attributes_start_at_ten", True),
            "attribute_max_value": longs.get("attribute_max_value", 290),
            # 009's additive capacity floor. Read, not assumed: without it a caster's burden
            # reads far heavier than the game shows them, because capacity would be 150*Str alone.
            "burden_capacity_floor_enabled": bools.get("burden_capacity_floor_enabled", True),
            "burden_capacity_floor": longs.get("burden_capacity_floor", 3000),
        }

    value, _ = _dials_cache.get_or_build("dials", build)

    return value


# ---------------------------------------------------------------------------------------------
# live status
# ---------------------------------------------------------------------------------------------

_status_cache = cache.TTLCache(ttl=25)


def live_status() -> dict:
    """The status feed, with a short TTL so the page's 30-second poll costs one fetch, not many.

    Never raises. A status feed that is briefly unreachable must not take the character sheet
    down with it — the sheet's own data comes from the database and is entirely independent.
    """

    def build() -> dict:
        try:
            with urllib.request.urlopen(STATUS_URL, timeout=4) as response:
                return json.loads(response.read().decode("utf-8"))
        except (urllib.error.URLError, ValueError, TimeoutError, OSError):
            return {"online": False, "playersOnline": None, "stale": True}

    value, _ = _status_cache.get_or_build("status", build)

    return value


_online_cache = cache.TTLCache(ttl=25)


def online_names() -> set[str]:
    """Character names the server currently has online.

    Read from a FILE, not from the public status feed. sitedata.sh writes it every 30 seconds
    from the server's own `listplayers` console command, into /opt/ACE rather than the web root —
    a public who-is-online roster is a disclosure about players, not about the server, and the
    portal only needs it to put a dot beside a character the viewer can already see.

    The `†` marker is stripped on the writing side, because it is cosmetic and is NOT part of
    `character.name` — leaving it on would make every hard-lane character fail the comparison
    and read as permanently offline.

    A missing or stale file yields an empty set, so everyone reads offline. That is the right
    failure mode. The tempting alternative — deriving presence from the shard's login/logoff
    timestamps — is actively WRONG: ACE repurposes LogoffTimestamp as the PK timer and writes
    FUTURE values into it (Player.cs), so a character who fought recently looks logged in
    forever.
    """

    def build() -> set[str]:
        try:
            with open(ONLINE_NAMES_PATH, encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            return set()

        # If the feed has stopped being written, believing it would pin an "Online" dot on
        # whoever happened to be on when it died.
        generated = data.get("generated")

        if generated:
            try:
                stamp = time.strptime(generated, "%Y-%m-%dT%H:%M:%SZ")

                if time.time() - calendar.timegm(stamp) > ONLINE_NAMES_MAX_AGE:
                    return set()
            except ValueError:
                return set()

        names = data.get("names")

        return {str(n) for n in names} if isinstance(names, list) else set()

    value, _ = _online_cache.get_or_build("online", build)

    return value


# ---------------------------------------------------------------------------------------------
# session plumbing
# ---------------------------------------------------------------------------------------------


def client_ip(request: Request) -> str:
    """The real client address, from Caddy's X-Forwarded-For.

    Everything reaches this process through the reverse proxy, so `request.client.host` is always
    127.0.0.1 and would collapse every visitor into one rate-limit bucket — turning the lockout
    into a self-inflicted outage the first time anybody mistyped a password. The LEFTMOST entry
    is the client; Caddy appends, and trusting it is safe only because nothing but Caddy can
    reach this port (uvicorn binds 127.0.0.1).
    """
    forwarded = request.headers.get("x-forwarded-for")

    if forwarded:
        return forwarded.split(",")[0].strip()

    return request.client.host if request.client else "unknown"


def require_session(token: str | None) -> dict:
    session = auth.read_session(token)

    if session is None:
        raise _http(401, "Sign in with your game account to see this.")

    return session


class ApiError(Exception):
    def __init__(self, status: int, message: str):
        self.status = status
        self.message = message


def _http(status: int, message: str) -> ApiError:
    return ApiError(status, message)


@app.exception_handler(ApiError)
async def _api_error(_: Request, exc: ApiError):
    return JSONResponse({"error": exc.message}, status_code=exc.status)


@app.exception_handler(auth.AuthError)
async def _auth_error(_: Request, exc: auth.AuthError):
    return JSONResponse({"error": exc.message}, status_code=exc.status)


@app.exception_handler(db.ConfigError)
async def _config_error(_: Request, exc: db.ConfigError):
    # The message names an environment variable, so it is logged rather than returned.
    print(f"[sg-web] configuration error: {exc}", flush=True)
    return JSONResponse({"error": "The portal is not configured correctly."}, status_code=500)


# ---------------------------------------------------------------------------------------------
# endpoints
# ---------------------------------------------------------------------------------------------


class LoginBody(BaseModel):
    """The login form's body.

    ACCEPTS BOTH `account` AND `accountName`. Contract 1 never named this field — it specified the
    character payload and left the login body to whoever wrote it — so the front-end sent
    `account` and this required `accountName`. Pydantic rejected the body with a 422, and the
    front-end's error handler rendered that as "Incorrect account or password", which is the worst
    possible symptom: a player with the right password is told it is wrong.

    Taking either is the right resolution for a name that was never agreed. Rewriting one side to
    match the other would work today and break again the next time Cowork regenerates the page
    from the contract, which still does not mention it.
    """

    account: str | None = None
    accountName: str | None = None
    password: str
    remember: bool = False

    @property
    def name(self) -> str:
        return (self.accountName or self.account or "").strip()


@app.post("/api/login")
def login(body: LoginBody, request: Request, response: Response):
    """Verify GAME credentials and start a session. Verifies only — never migrates, never writes."""
    account = auth.verify_credentials(body.name, body.password, client_ip(request))

    token, max_age = auth.issue_session(account, body.remember)

    response.set_cookie(auth.SESSION_COOKIE, token, max_age=max_age, **COOKIE_KWARGS)

    return {
        "account": {"id": account["accountId"], "name": account["accountName"]},
        "characters": character_list(account["accountId"]),
    }


@app.post("/api/logout")
def logout(response: Response):
    response.delete_cookie(auth.SESSION_COOKIE, path="/")
    return {"ok": True}


@app.get("/api/me")
def me(sg_session: str | None = Cookie(default=None)):
    """Who the caller is, or `{account: null}`. Never 401s — the front-end asks this on load."""
    session = auth.read_session(sg_session)

    if session is None:
        return {"account": None}

    return {"account": {"id": session["id"], "name": session["name"]}}


def character_list(account_id: int) -> list[dict]:
    """The picker row: id, name, level, marker, online.

    Deleted characters are excluded on BOTH columns. `is_Deleted` is a BIT(1) and `delete_Time` is
    a countdown to the purge, and a character mid-delete has one set without the other — LIVE has
    two rows named "Black Breath", one of them deleted, and only this keeps the ghost off the page.
    """

    def build() -> list[dict]:
        with db.shard() as cur:
            rows = db.fetch_all(
                cur,
                """
                SELECT c.id, c.name,
                       COALESCE(lvl.value, 1) AS level,
                       EXISTS (SELECT 1 FROM biota_properties_bool b
                               WHERE b.object_Id = c.id AND b.type = 9102 AND b.value = 1) AS forfeited
                FROM `character` c
                LEFT JOIN biota_properties_int lvl
                       ON lvl.object_Id = c.id AND lvl.type = 25
                WHERE c.account_Id = %s AND c.is_Deleted = 0 AND c.delete_Time = 0
                ORDER BY COALESCE(lvl.value, 1) DESC, c.name
                """,
                (account_id,),
            )

        return [
            {
                "id": r["id"],
                "name": r["name"],
                "level": int(r["level"] or 1),
                "marker": "fast" if r["forfeited"] else "hard",
            }
            for r in rows
        ]

    characters, _ = cache.character_lists.get_or_build(account_id, build)

    # Online is stamped outside the cache: the list is cached for a minute, the status feed
    # refreshes every 25 seconds, and a stale "online" dot is the most obviously wrong thing on
    # the page.
    online = online_names()

    return [{**c, "online": c["name"] in online} for c in characters]


@app.get("/api/characters")
def characters(sg_session: str | None = Cookie(default=None)):
    session = require_session(sg_session)

    return {
        "account": {"id": session["id"], "name": session["name"]},
        "characters": character_list(session["id"]),
    }


@app.get("/api/character/{character_id}")
def character(character_id: int, sg_session: str | None = Cookie(default=None)):
    """The full private object — for a character the logged-in account OWNS.

    Ownership is re-checked against the shard on every request, not taken from the session. A
    session says who you are; only the database says what is yours, and a character can change
    hands between one request and the next.
    """
    session = require_session(sg_session)

    def build() -> dict:
        with db.shard() as cur:
            raw = payload.load_character(cur, character_id)

            if raw is None:
                raise _http(404, "No such character.")

            if raw["char"]["account_Id"] != session["id"]:
                # Deliberately the same 404 an absent character gets. A distinct 403 would confirm
                # that a given character id exists and belongs to somebody else.
                raise _http(404, "No such character.")

            return payload.build_private(cur, raw, live_dials(), time.time())

    data, age = cache.private_characters.get_or_build(character_id, build)

    data = dict(data)
    data["online"] = data["name"] in online_names()
    # ISO, like every other timestamp in the payload - `new Date()` reads a bare number as
    # milliseconds. See payload.iso().
    data["asOf"] = payload.iso(time.time() - age)
    data["cacheSeconds"] = cache.private_characters.ttl

    return data


@app.get("/api/public/character/{name}")
def public_character(name: str):
    """The no-login subset, by character name. Honour-roll links land here.

    Names are unique among LIVE characters today and the query enforces it anyway — a deleted
    character and a live one can share a name, and only the delete filter tells them apart.
    """

    def build() -> dict:
        with db.shard() as cur:
            row = db.fetch_one(
                cur,
                "SELECT id FROM `character` "
                "WHERE name = %s AND is_Deleted = 0 AND delete_Time = 0 "
                "ORDER BY last_Login_Timestamp DESC LIMIT 1",
                (name,),
            )

            if row is None:
                raise _http(404, "No such character.")

            raw = payload.load_character(cur, row["id"])

            if raw is None:
                raise _http(404, "No such character.")

            return payload.build_public(raw, live_dials())

    data, age = cache.public_characters.get_or_build(name.lower(), build)

    data = dict(data)
    data["online"] = data["name"] in online_names()
    data["asOf"] = payload.iso(time.time() - age)

    return data


@app.get("/api/status")
def status():
    """The live server feed, proxied so the page makes one origin's worth of requests."""
    return live_status()


@app.get("/api/health")
def health():
    auth.limiter.sweep()

    return {
        "db": db.health(),
        "cache": {
            "private": cache.private_characters.stats(),
            "public": cache.public_characters.stats(),
        },
        "tables": {
            "skills": len(curves.skill_table()),
            "levels": len(curves.tables().level),
        },
    }
