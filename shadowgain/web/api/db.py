"""Shadowgain 124 — database access for my.shadowgain.com.

READ-ONLY, ONE WAY. This module has no INSERT, UPDATE or DELETE in it, and the account it
connects as cannot execute one if it did (see setup.sql). That is deliberately belt AND braces:
the grant is the guarantee, and the absence of write code is what stops a well-meaning future
edit from ever needing the grant widened.

It follows the bot's pattern for the same reasons the bot chose it (bot/requirements.txt):
PyMySQL because it is pure Python — no compiler, no libmysqlclient-dev on the droplet — and a
DictCursor so queries read as field names rather than tuple indices.

CONNECTIONS ARE PER-REQUEST, NOT POOLED. The traffic here is a handful of players refreshing a
page every five minutes, against a cache that absorbs almost all of it; a pool would be more
moving parts guarding against load that does not exist. What matters far more is that a stale
connection can never serve stale data, which `ping(reconnect=True)` handles.
"""

from __future__ import annotations

import contextlib
import os

import pymysql
import pymysql.cursors

DB_HOST = os.environ.get("SG_WEB_DB_HOST", "127.0.0.1")
DB_PORT = int(os.environ.get("SG_WEB_DB_PORT", "3306"))
DB_USER = os.environ.get("SG_WEB_DB_USER", "sgweb")
DB_PASS = os.environ.get("SG_WEB_DB_PASSWORD") or ""

SHARD_DB = os.environ.get("SG_WEB_DB_SHARD", "ace_shard")
AUTH_DB = os.environ.get("SG_WEB_DB_AUTH", "ace_auth")


class ConfigError(RuntimeError):
    pass


def _connect(database: str):
    if not DB_PASS:
        # Failing loudly at the first query beats connecting as an anonymous user and getting a
        # confusing permission error three layers down.
        raise ConfigError("SG_WEB_DB_PASSWORD is not set")

    return pymysql.connect(
        host=DB_HOST,
        port=DB_PORT,
        user=DB_USER,
        password=DB_PASS,
        database=database,
        charset="utf8mb4",
        cursorclass=pymysql.cursors.DictCursor,
        connect_timeout=5,
        read_timeout=15,
        # The web service must never be the reason a row changes. autocommit on a connection
        # that can only SELECT costs nothing and keeps no transaction open against the shard.
        autocommit=True,
    )


@contextlib.contextmanager
def shard():
    """A cursor on ace_shard.

    Queries here name ace_auth tables fully qualified when they need one, rather than opening a
    second connection — the bot learned the hard way (env.example) that an unqualified name with
    no default database fails as "(1046, 'No database selected')", which surfaces as a feature
    that silently never works rather than as an error anyone sees.
    """
    conn = _connect(SHARD_DB)
    try:
        with conn.cursor() as cur:
            yield cur
    finally:
        conn.close()


@contextlib.contextmanager
def auth():
    """A cursor on ace_auth. Used only by the login path."""
    conn = _connect(AUTH_DB)
    try:
        with conn.cursor() as cur:
            yield cur
    finally:
        conn.close()


def fetch_all(cur, sql: str, args=None) -> list[dict]:
    cur.execute(sql, args or ())
    return list(cur.fetchall())


def fetch_one(cur, sql: str, args=None) -> dict | None:
    cur.execute(sql, args or ())
    return cur.fetchone()


def as_bool(value) -> bool:
    """MySQL BIT(1) comes back from PyMySQL as b'\\x00' / b'\\x01', and `bool(b'\\x00')` is True.

    `character.is_Deleted` is a BIT(1), and getting this wrong means deleted characters appear on
    the site — which is not hypothetical: LIVE has two characters named "Black Breath", one of
    them deleted, and only this distinguishes them.
    """
    if value is None:
        return False

    if isinstance(value, (bytes, bytearray)):
        return value != b"\x00" and value != b""

    return bool(value)


def health() -> dict:
    """A cheap liveness probe for /api/health. Never raises — the endpoint reports the failure."""
    try:
        with shard() as cur:
            row = fetch_one(cur, "SELECT COUNT(*) AS n FROM `character` WHERE is_Deleted = 0")

        return {"ok": True, "characters": row["n"] if row else 0}
    except Exception as ex:  # noqa: BLE001 - the point is to report, not to propagate
        return {"ok": False, "error": type(ex).__name__}
