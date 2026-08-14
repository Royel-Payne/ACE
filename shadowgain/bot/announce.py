#!/usr/bin/env python3
"""
Shadowgain: post an announcement embed to a Discord channel, as the bot.

    python announce.py --list                      # enumerate channels, post nothing
    python announce.py --channel general --file msg.md --dry-run
    python announce.py --channel general --file msg.md

WHY THIS EXISTS. The bot had no outbound announcement path - only the inbound relay, /link,
/bug and the role sweep. That was mistaken for "the bot cannot post announcements", which is
wrong twice over: it already builds and sends embeds for every bug report, and the Shadowgain
bot role deliberately keeps Administrator precisely so building things in Discord does not
need a permission round-trip each time (Chris, 2026-08-08).

Not to be confused with `/say`, removed in 034. That was an INBOUND path - Discord text into
game chat - and it was removed because it bypassed the `Verified Player` gate. This is
outbound only. It cannot put anything into the game and touches no gate.

The message body is read from a FILE rather than argv, so wording can be reviewed and edited
as a normal diff before anything reaches players. First line beginning with '# ' becomes the
embed title; the rest is the description. `--dry-run` renders exactly what would be sent.
"""
import argparse
import hashlib
import json
import os
import sys
import asyncio
import time

import discord

TOKEN = os.environ.get("DISCORD_BOT_TOKEN", "").strip()
GUILD_ID = int(os.environ.get("DISCORD_GUILD_ID", "0"))

# Shadowgain gold, matching the Ascendant role colour used elsewhere in the server.
EMBED_COLOUR = 0xC8A24B

# Ledger of what has already been posted, so the same announcement cannot go out twice.
#
# WHY: on 2026-08-14 the 119 announcement was posted twice, five minutes apart, because a
# staged .md file looks EXACTLY the same whether it has been sent or not - and --dry-run
# renders it happily either way, so the rehearsal gives no hint. Two people mid-deploy each
# reasonably concluded it still needed posting. Nothing in the file, the filesystem or this
# tool could have told them otherwise. That is a missing interlock, not carelessness.
#
# Keyed on the file's CONTENT HASH as well as its name, so revising an announcement and
# re-posting it is allowed - it is a different message. Only the identical thing is refused.
LEDGER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "announce-posted.json")


def _load_ledger():
    try:
        with open(LEDGER, encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:                                   # noqa: BLE001 - absent or unreadable
        return {}


def _record(key, entry):
    ledger = _load_ledger()
    ledger[key] = entry

    try:
        with open(LEDGER, "w", encoding="utf-8") as fh:
            json.dump(ledger, fh, indent=2, sort_keys=True)
    except Exception as exc:                            # noqa: BLE001
        # A ledger we cannot write is worth a warning, never a failed post - the message is
        # already sent by this point and losing the record is strictly better than pretending
        # the send failed.
        print(f"warning: could not update {LEDGER}: {exc!r}", file=sys.stderr)


def parse_message(raw):
    """First '# ' heading is the title; everything after is the body."""
    lines = raw.strip().splitlines()

    title = None
    if lines and lines[0].startswith("# "):
        title = lines[0][2:].strip()
        lines = lines[1:]

    return title, "\n".join(lines).strip()


async def run(args):
    intents = discord.Intents.default()
    client = discord.Client(intents=intents)

    result = {"code": 1}

    @client.event
    async def on_ready():
        try:
            guild = client.get_guild(GUILD_ID) or await client.fetch_guild(GUILD_ID)
            if guild is None:
                print(f"guild {GUILD_ID} not found", file=sys.stderr)
                return

            channels = await guild.fetch_channels()
            text_channels = [c for c in channels if isinstance(c, discord.TextChannel)]

            if args.list:
                print(f"{'channel':<32} {'id':<20} category")
                for c in sorted(text_channels, key=lambda c: (str(c.category), c.position)):
                    print(f"#{c.name:<31} {c.id:<20} {c.category.name if c.category else '-'}")
                result["code"] = 0
                return

            target = None
            for c in text_channels:
                if c.name.lower() == args.channel.lower().lstrip("#") or str(c.id) == args.channel:
                    target = c
                    break

            if target is None:
                print(f"no channel matching '{args.channel}' - run --list", file=sys.stderr)
                return

            with open(args.file, encoding="utf-8") as fh:
                raw = fh.read()

            title, body = parse_message(raw)

            key = f"{os.path.basename(args.file)}:{target.id}"
            digest = hashlib.sha256(raw.encode("utf-8")).hexdigest()[:16]
            prior = _load_ledger().get(key)
            already = prior is not None and prior.get("sha256") == digest

            embed = discord.Embed(title=title, description=body, colour=EMBED_COLOUR)

            if args.dry_run:
                print(f"--- DRY RUN: would post to #{target.name} ({target.id}) ---")
                print(f"title: {title}")
                print("-" * 60)
                print(body)
                print("-" * 60)
                print(f"{len(body)} chars (embed description limit is 4096)")

                # The whole point of the ledger is that the rehearsal warns you too. A dry run
                # that looks identical whether or not the thing has already gone out is exactly
                # how the duplicate happened.
                if already:
                    print(f"NOTE: this exact file was ALREADY POSTED to #{target.name} "
                          f"at {prior.get('posted')} as message {prior.get('message_id')}. "
                          f"A real run would refuse without --force.")

                result["code"] = 0
                return

            if already and not args.force:
                print(f"REFUSING: this exact file was already posted to #{target.name} at "
                      f"{prior.get('posted')} as message {prior.get('message_id')}.",
                      file=sys.stderr)
                print("Edit the file to post a revised version, or pass --force to post it "
                      "again deliberately.", file=sys.stderr)
                return

            if prior is not None and not already:
                print(f"note: {os.path.basename(args.file)} was posted before at "
                      f"{prior.get('posted')}, but its contents have changed since - posting "
                      f"the new version.")

            # allowed_mentions=none: an announcement must never ping the server, and the
            # same guard is applied on every other outbound send in the bot.
            msg = await target.send(embed=embed, allowed_mentions=discord.AllowedMentions.none())
            print(f"posted to #{target.name}: message id {msg.id}")

            _record(key, {
                "message_id": str(msg.id),
                "channel": target.name,
                "sha256": digest,
                "posted": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            })

            result["code"] = 0

        except Exception as exc:                       # noqa: BLE001 - report and exit non-zero
            print(f"failed: {exc!r}", file=sys.stderr)
        finally:
            await client.close()

    await client.start(TOKEN)
    return result["code"]


def main():
    ap = argparse.ArgumentParser(description="Post an announcement embed as the Shadowgain bot.")
    ap.add_argument("--list", action="store_true", help="enumerate text channels and exit")
    ap.add_argument("--channel", help="channel name or id to post to")
    ap.add_argument("--file", help="path to the message body (markdown)")
    ap.add_argument("--dry-run", action="store_true", help="render the embed without sending")
    ap.add_argument("--force", action="store_true",
                    help="post even if this exact file has already been posted to this channel")
    args = ap.parse_args()

    if not TOKEN or not GUILD_ID:
        print("missing DISCORD_BOT_TOKEN or DISCORD_GUILD_ID", file=sys.stderr)
        sys.exit(1)

    if not args.list and not (args.channel and args.file):
        ap.error("--channel and --file are required unless --list")

    sys.exit(asyncio.run(run(args)))


if __name__ == "__main__":
    main()
