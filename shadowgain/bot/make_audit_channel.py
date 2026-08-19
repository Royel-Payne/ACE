#!/usr/bin/env python3
"""
Shadowgain 045: create the read-only #audit channel. Idempotent - safe to re-run.

Read-only for EVERYONE, including staff and including Chris. The bot is the only writer.
An audit channel that humans can post in is one a human can pad, and one they can delete
from is one they can edit; both defeat the point. Discord message history is what makes
this durable on the Discord side, so Read Message History is granted where View is.

READABLE BY VERIFIED PLAYERS (177). It is a transparency mechanism aimed at players -
evidence that nobody is being handed xp, items or favours - not an internal staff log.
Restricting it to staff would leave it proving nothing to the only audience that matters.

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

        # 177: VERIFIED PLAYERS CAN READ THIS, AND THAT IS THE ENTIRE POINT.
        #
        # This was view_channel=False from 045 until 2026-08-19, and the live channel had been
        # changed to True by hand at some point - so re-running this idempotent script would have
        # silently taken the access away again. That is the bug being fixed here: the script now
        # asserts what the channel is actually FOR.
        #
        # Chris, 2026-08-19: "the audit trail exists to reveal any 'abuse' of admin powers,
        # cheating, granting xp, creating items, it's to provide transparency that no favoritism
        # is being offered to anyone. This is a hobby amateur server but we don't want people to
        # think we're gifting some people anything. It's not to be a window into every move we
        # make that's outside that scope."
        #
        # WHY VERIFIED PLAYER AND NOT @everyone. The role is the line between people who linked a
        # game account and people who joined the Discord. Chris, 2026-08-19: "the Verified Player
        # role was granted access to #audit since those are the people who at least made a tiny bit
        # of effort to be part of the community, #audit is a privilege for the players, not the
        # lurkers who happen to join discord."
        #
        # So this is a THREE-TIER decision, not a public/private one, and each tier is deliberate:
        # @everyone sees nothing, Verified Players see the trail, nobody writes to it. Reading it as
        # "transparency, therefore public" would be the wrong simplification - the gate is the
        # point, and it is the same shape as the presence gate on the web portal.
        #
        # So the AUDIENCE IS PLAYERS, not staff. Two consequences worth stating, because they are
        # easy to get backwards:
        #
        #   - The channel being player-readable is a FEATURE. Do not "fix" it back to False.
        #   - ShadowgainAudit's NotAudited list is therefore a TRANSPARENCY decision, not merely a
        #     noise one. Anything added there becomes invisible to the people the channel exists
        #     to reassure, which is why its criterion is unfair gameplay and why its default is
        #     to record.
        #
        # send_messages stays denied: @everyone's deny covers every human, and a trail a human can
        # post in is one they can pad.
        if VERIFIED_ROLE_ID:
            role = guild.get_role(VERIFIED_ROLE_ID)
            if role is not None:
                overwrites[role] = discord.PermissionOverwrite(
                    view_channel=True, send_messages=False, read_message_history=True)

        if existing is None:
            ch = await guild.create_text_channel(
                NAME, category=category, overwrites=overwrites,
                topic=("Read-only, written by the server and never purged. Every privileged action that could "
                       "affect fairness - items, experience, characters, access, dials. Staff movement and "
                       "routine server operations are not listed."),
                reason="Shadowgain 045: durable audit trail")
            print(f"CREATED #{NAME} id={ch.id}")
        else:
            await existing.edit(overwrites=overwrites,
                                topic=("Read-only, written by the server and never purged. Every privileged action that could "
                       "affect fairness - items, experience, characters, access, dials. Staff movement and "
                       "routine server operations are not listed."),
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
