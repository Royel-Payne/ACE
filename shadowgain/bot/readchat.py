#!/usr/bin/env python3
"""
Shadowgain: read recent messages from a Discord channel, for diagnosis.

    python readchat.py --channel chat --limit 400
    python readchat.py --channel chat --limit 600 --grep "attribute|infinity|290"

READ ONLY. It fetches history and prints it; it sends nothing and changes nothing.
Written because a design question ("is the attribute number still climbing, or is the
client clamping it?") had already been answered by players in #chat several times, and
the answer was faster to look up than to re-derive.

The bot relays game chat into #chat, so lines already carry a [General] prefix and the
speaker's character name - the Discord author for those is the bot itself.
"""
import argparse
import os
import re
import sys
import asyncio

import discord

TOKEN = os.environ.get("DISCORD_BOT_TOKEN", "").strip()
GUILD_ID = int(os.environ.get("DISCORD_GUILD_ID", "0"))


async def run(args):
    intents = discord.Intents.default()
    intents.message_content = True          # required to read message text
    client = discord.Client(intents=intents)

    @client.event
    async def on_ready():
        try:
            guild = client.get_guild(GUILD_ID) or await client.fetch_guild(GUILD_ID)
            channels = await guild.fetch_channels()

            target = None
            for c in channels:
                if isinstance(c, discord.TextChannel) and (
                        c.name.lower() == args.channel.lower().lstrip("#") or str(c.id) == args.channel):
                    target = c
                    break

            if target is None:
                print(f"no channel matching '{args.channel}'", file=sys.stderr)
                return

            pattern = re.compile(args.grep, re.IGNORECASE) if args.grep else None

            msgs = []
            async for m in target.history(limit=args.limit):
                msgs.append(m)

            for m in reversed(msgs):
                text = m.content or ""
                if pattern and not pattern.search(text):
                    continue
                stamp = m.created_at.strftime("%m-%d %H:%M")
                print(f"[{stamp}] {m.author.display_name}: {text}")

        except Exception as exc:                      # noqa: BLE001
            print(f"failed: {exc!r}", file=sys.stderr)
        finally:
            await client.close()

    await client.start(TOKEN)


def main():
    ap = argparse.ArgumentParser(description="Read recent Discord channel history (read only).")
    ap.add_argument("--channel", required=True)
    ap.add_argument("--limit", type=int, default=300)
    ap.add_argument("--grep", help="only print lines matching this regex")
    args = ap.parse_args()

    if not TOKEN or not GUILD_ID:
        print("missing DISCORD_BOT_TOKEN or DISCORD_GUILD_ID", file=sys.stderr)
        sys.exit(1)

    asyncio.run(run(args))


if __name__ == "__main__":
    main()
