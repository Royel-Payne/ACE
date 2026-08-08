#!/usr/bin/env python3
"""
Shadowgain 045: create the read-only #audit channel. Idempotent - safe to re-run.

Read-only for EVERYONE, including staff and including Chris. The bot is the only writer.
An audit channel that humans can post in is one a human can pad, and one they can delete
from is one they can edit; both defeat the point. Discord message history is what makes
this durable on the Discord side, so Read Message History is granted where View is.

Requires Manage Channels on the bot, which Chris granted for this and intends to revoke.
"""
import os, sys, asyncio, discord

TOKEN = os.environ.get("DISCORD_BOT_TOKEN", "").strip()
GUILD_ID = int(os.environ.get("DISCORD_GUILD_ID", "0"))
RELAY_ID = int(os.environ.get("DISCORD_RELAY_CHANNEL_ID", "0"))
VERIFIED_ROLE_ID = int(os.environ.get("DISCORD_VERIFIED_ROLE_ID", "0"))
NAME = "audit"

if not TOKEN or not GUILD_ID:
    print("missing DISCORD_BOT_TOKEN or DISCORD_GUILD_ID", file=sys.stderr)
    sys.exit(1)

client = discord.Client(intents=discord.Intents.default())


@client.event
async def on_ready():
    try:
        guild = client.get_guild(GUILD_ID) or await client.fetch_guild(GUILD_ID)

        existing = None
        for ch in await guild.fetch_channels():
            if isinstance(ch, discord.TextChannel) and ch.name == NAME:
                existing = ch
                break

        # Put it beside the relay channel so it inherits the same private category rather
        # than landing at the top of the server for everyone to notice.
        category = None
        relay = guild.get_channel(RELAY_ID)
        if relay is not None:
            category = relay.category

        me = guild.me or await guild.fetch_member(client.user.id)

        overwrites = {
            # Nobody types here. send_messages=False for @everyone covers every human,
            # since role grants cannot re-enable what the base role denies for sending
            # unless a role explicitly allows it - so no role is given send.
            guild.default_role: discord.PermissionOverwrite(
                view_channel=False, send_messages=False, add_reactions=False,
                create_public_threads=False, create_private_threads=False),
            me: discord.PermissionOverwrite(
                view_channel=True, send_messages=True, read_message_history=True,
                embed_links=True, manage_messages=True),
        }

        # Chris reads it; he just cannot write to it. Same for anyone he later trusts.
        if VERIFIED_ROLE_ID:
            role = guild.get_role(VERIFIED_ROLE_ID)
            if role is not None:
                overwrites[role] = discord.PermissionOverwrite(
                    view_channel=False, send_messages=False)

        if existing is None:
            ch = await guild.create_text_channel(
                NAME, category=category, overwrites=overwrites,
                topic="Read-only. Every privileged command, written by the server. Not purged.",
                reason="Shadowgain 045: durable audit trail")
            print(f"CREATED #{NAME} id={ch.id}")
        else:
            await existing.edit(overwrites=overwrites,
                                topic="Read-only. Every privileged command, written by the server. Not purged.",
                                reason="Shadowgain 045: re-assert read-only")
            print(f"EXISTS  #{NAME} id={existing.id} (permissions re-asserted)")
            ch = existing

        print(f"DISCORD_AUDIT_CHANNEL_ID={ch.id}")

        # Report what a normal member can actually do, rather than what we intended.
        perms = ch.permissions_for(guild.default_role)
        print(f"@everyone: view={perms.view_channel} send={perms.send_messages}")
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
    finally:
        await client.close()


client.run(TOKEN, log_handler=None)
