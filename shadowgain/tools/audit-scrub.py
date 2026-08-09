#!/usr/bin/env python3
"""
One-off: redact account names already posted to #audit.

WHY. An account name is half of a login - the half that is normally hard to obtain. Character
names are public and identify the actor perfectly well; account names add nothing a reader of
#audit needs. The bot masks them going forward, but everything already posted still carries them
in plain text, and #audit is readable by every staff member.

The bot authored those messages, so they can be EDITED in place. Deleting and reposting would lose
the timestamps that make the trail worth keeping and would flood the channel.

WHY NOT FIND-AND-REPLACE. `admin` is an account name, AND the access-level label `[Admin]`, AND a
character name on this server. A blind substring pass would corrupt all three and silently rewrite
history into something misleading - worse than the exposure it fixes. So this anchors to the two
shapes the bot actually emits:

    **Character (account)** ...        the actor
    ran `@set-accountaccess acct ...`  an account as the first argument

Longest names are masked first, because `oldgreylocke` is a prefix of `oldgreylockeadmin` and
replacing the short one first would leave `acct#9189admin`.

DRY RUN BY DEFAULT. Pass --apply to actually edit.

    python3 audit-scrub.py            # report what would change
    python3 audit-scrub.py --apply    # do it
"""

import asyncio
import hashlib
import os
import re
import sys

import discord
import pymysql


def load_env(path="/opt/ACE/bot.env"):
    env = {}
    with open(path) as fh:
        for line in fh:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                env[k.strip()] = v.strip()
    return env


def mask_account(name: str) -> str:
    """Identical to the bot's mask_account - the tags MUST agree or correlation breaks."""
    if not name:
        return "?"
    return "acct#" + hashlib.sha256(name.strip().lower().encode("utf-8")).hexdigest()[:4]


ACCOUNT_ARG_COMMANDS = ("accountcreate", "accountget", "set-accountaccess", "set-accountpassword")


def redact(text: str, accounts) -> str:
    """Return `text` with account names replaced by their tags, or unchanged if none present."""
    out = text

    # 1. The actor: **Character (account)**. Only rewrite when the parenthesised value is a KNOWN
    #    account, so a character name that happens to be parenthesised is left alone.
    for acct in accounts:
        out = re.sub(r"\(" + re.escape(acct) + r"\)", "(" + mask_account(acct) + ")", out)

    # 2. An account as the first argument of an account-taking command.
    for cmd in ACCOUNT_ARG_COMMANDS:
        def sub(m):
            target = m.group("acct")
            return m.group("head") + (mask_account(target) if target.lower() in accounts_lower else target)

        accounts_lower = {a.lower() for a in accounts}
        out = re.sub(r"(?P<head>@" + re.escape(cmd) + r"\s+)(?P<acct>\S+)", sub, out)

    return out


async def main():
    apply = "--apply" in sys.argv
    env = load_env()

    token = env.get("DISCORD_BOT_TOKEN")
    channel_id = int(env.get("DISCORD_AUDIT_CHANNEL_ID") or 0)

    if not token or not channel_id:
        print("!! DISCORD_BOT_TOKEN or DISCORD_AUDIT_CHANNEL_ID missing from bot.env")
        return

    # Account names come from the auth DB rather than being hardcoded, so this stays correct as
    # accounts are added. Sorted longest-first - see the prefix note in the module docstring.
    conn = pymysql.connect(host=env.get("SG_DB_HOST", "127.0.0.1"),
                           port=int(env.get("SG_DB_PORT", 3306)),
                           user=env.get("SG_DB_USER", "root"),
                           password=env.get("SG_DB_PASSWORD", ""),
                           database="ace_auth")
    with conn.cursor() as cur:
        cur.execute("SELECT accountName FROM account")
        accounts = sorted((r[0] for r in cur.fetchall()), key=len, reverse=True)
    conn.close()

    print(f"==> {len(accounts)} account name(s) to redact, longest first")

    intents = discord.Intents.default()
    client = discord.Client(intents=intents)

    @client.event
    async def on_ready():
        scanned = changed = failed = 0
        channel = client.get_channel(channel_id) or await client.fetch_channel(channel_id)

        async for msg in channel.history(limit=None, oldest_first=True):
            scanned += 1

            # Only the bot's own messages can be edited, and only they should be.
            if msg.author.id != client.user.id:
                continue

            new = redact(msg.content, accounts)

            if new == msg.content:
                continue

            changed += 1

            if not apply:
                print(f"  would edit {msg.id}: {msg.content[:90]}")
                continue

            try:
                await msg.edit(content=new)
                await asyncio.sleep(0.6)          # well inside Discord's edit rate limit
            except discord.HTTPException as e:
                failed += 1
                print(f"  !! {msg.id}: {e}")

        verb = "edited" if apply else "would edit"
        print(f"==> scanned {scanned}, {verb} {changed}, failed {failed}")

        if not apply:
            print("==> DRY RUN. Re-run with --apply to make the edits.")

        await client.close()

    await client.start(token)


if __name__ == "__main__":
    asyncio.run(main())
