using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Shadowgain 033: the Discord -> game direction.
    ///
    /// 031 shipped one-way (game -> Discord) and deliberately deferred this as a security
    /// boundary. What changed is `/link`: the headline objection was IMPERSONATION - which
    /// in-game identity does a Discord line speak as? - and a verified account/character link
    /// answers that definitively. The 72-hour activity gate means a lapsed account also loses
    /// the ability to speak.
    ///
    /// THE SERVER IS AUTHORITATIVE. The bot proposes; this class disposes. Nothing the bot
    /// writes is taken on trust:
    ///   - the claimed character must actually belong to the claimed account
    ///   - a GAGGED character is silenced here, because the bot cannot see gag state
    ///   - rate limit and length cap are enforced here, not there
    ///   - General is the ONLY destination, in code rather than in a dial
    /// That mirrors 031, where the relay allowlist lives in code for the same reason.
    ///
    /// TRANSPORT: the bot appends JSON lines to /ace/Logs/inbound.jsonl and this polls it.
    /// Symmetric with the outbound half, needs no new port, no new listener, and no write
    /// access to the database - the bot's MySQL user stays READ-ONLY. It also reuses the
    /// already-mounted Logs volume, so there is no compose change and no container recreate.
    ///
    /// Anyone who can write that file can speak in game as any linked character. That is the
    /// same trust boundary as the server binary itself - both are root-owned on the same box -
    /// so it adds no new exposure. It is, however, exactly why an HTTP endpoint was rejected:
    /// that would have created a genuinely new one.
    /// </summary>
    public static class ShadowgainInbound
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(ShadowgainInbound));

        private const string InboundPath = "/ace/Logs/inbound.jsonl";

        /// <summary>
        /// The world ticks at 30-60fps. Touching the filesystem that often would be absurd for
        /// a feature whose input arrives at human typing speed, so the poll is throttled here
        /// rather than by how often Tick() is called.
        /// </summary>
        private const int PollMilliseconds = 1000;

        private static DateTime lastPoll = DateTime.MinValue;
        private static long lastPos = -1;               // -1 = not yet initialised
        private static string partial = "";

        private static readonly Dictionary<string, DateTime> lastSpoke =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Called from WorldManager.UpdateGameWorld, i.e. ON THE WORLD THREAD.
        ///
        /// That placement is load-bearing: this ends up calling Session.Network.EnqueueSend on
        /// every recipient, and doing that from a background timer thread would be exactly the
        /// kind of cross-thread game-state access ACE's threading rules forbid.
        /// </summary>
        public static void Tick()
        {
            try
            {
                if (!PropertyManager.GetBool("discord_inbound_enabled").Item)
                    return;

                if ((DateTime.UtcNow - lastPoll).TotalMilliseconds < PollMilliseconds)
                    return;

                lastPoll = DateTime.UtcNow;

                if (!File.Exists(InboundPath))
                {
                    // No file yet means there is no backlog to skip, so anchor at zero NOW.
                    // Otherwise the first line ever written gets eaten: the file would appear
                    // with content already in it, the "seek to end on first sight" branch below
                    // would skip past it, and a player's very first /say would vanish silently.
                    // Found exactly that way in testing.
                    if (lastPos < 0)
                        lastPos = 0;
                    return;
                }

                // FileShare.ReadWrite | Delete so the bot can keep writing (and rotating) while
                // this holds the file open. Without Delete, a rotation on the bot side would
                // fail with a sharing violation on some filesystems.
                using (var fs = new FileStream(InboundPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    // The file already existed when we first looked, which means the server has
                    // restarted with a backlog on disk. Start at the END: replaying stale
                    // conversation into the world is worse than losing it. (A file that did NOT
                    // exist yet is anchored at zero above, so a first message is not lost.)
                    if (lastPos < 0)
                    {
                        lastPos = fs.Length;
                        return;
                    }

                    // Shorter than we last read means truncated or replaced; start over.
                    if (fs.Length < lastPos)
                    {
                        lastPos = 0;
                        partial = "";
                    }

                    if (fs.Length == lastPos)
                        return;

                    fs.Seek(lastPos, SeekOrigin.Begin);

                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        var chunk = sr.ReadToEnd();
                        lastPos = fs.Length;

                        partial += chunk;

                        // Only process lines whose newline has arrived - the bot may have
                        // flushed mid-line.
                        int nl;
                        while ((nl = partial.IndexOf('\n')) >= 0)
                        {
                            var line = partial.Substring(0, nl);
                            partial = partial.Substring(nl + 1);
                            // TrimStart the BOM as well as whitespace: whoever writes this file
                            // may prepend one, and it silently breaks the parse otherwise.
                            line = line.Trim().TrimStart('﻿').Trim();
                            if (line.Length > 0)
                                HandleLine(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Never throw into the world loop. A broken inbound feed must degrade to
                // "no Discord messages", never to a stalled server.
                log.Warn($"[SHADOWGAIN-INBOUND] tick failed: {ex.Message}");
                lastPoll = DateTime.UtcNow.AddSeconds(5);   // back off rather than spin
            }
        }

        private static void HandleLine(string line)
        {
            string account, character, message;

            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var t) || t.GetString() != "say")
                        return;

                    account = Get(root, "account");
                    character = Get(root, "character");
                    message = Get(root, "message");
                }
            }
            catch (JsonException)
            {
                log.Warn($"[SHADOWGAIN-INBOUND] unparseable line: {Trunc(line, 120)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(character)
                || string.IsNullOrWhiteSpace(message))
                return;

            var max = (int)PropertyManager.GetLong("discord_relay_max_message").Item;
            if (max > 0 && message.Length > max)
                message = message.Substring(0, max) + "...";

            // --- validate the speaker, in memory, no database round trip ---------------------
            //
            // Strip any display prefix before looking the name up. The bot learned this name
            // from the server's own EmitVerify, which sends Player.Name - and that getter has
            // ALREADY applied the 023 dagger for a hard-lane character. The stored name has no
            // marker, so "† Black Breath" would never match and every lookup would fail. Same
            // character set the Player.Name setter trims, for the same reason.
            character = character.TrimStart('+', '*', '†', '[', ']', ' ').Trim();

            // FindByName covers online AND offline characters, so someone can speak from Discord
            // without being logged in - which is most of the point.
            var speaker = PlayerManager.FindByName(character);
            if (speaker == null)
            {
                log.Warn($"[SHADOWGAIN-INBOUND] unknown character '{character}' - dropped");
                return;
            }

            // The bot is trusted infrastructure, but "trusted" is not "unchecked": confirm the
            // character really belongs to the account that verified. Without this, a bug in the
            // bot's link table would let one player speak as another.
            var accountName = speaker.Account?.AccountName;
            if (accountName == null || !accountName.Equals(account, StringComparison.OrdinalIgnoreCase))
            {
                log.Warn($"[SHADOWGAIN-INBOUND] '{character}' does not belong to account '{account}' - dropped");
                return;
            }

            // Gag is checked HERE because the bot cannot see it: IsGagged is a PropertyBool on
            // the character, and the bot's read-only grant does not even include that table.
            // Without this, Discord would be a clean channel for a silenced player.
            if (speaker.GetProperty(PropertyBool.IsGagged) ?? false)
                return;

            // Rate limit per ACCOUNT rather than per character, so alt-hopping does not reset it.
            var cooldown = PropertyManager.GetLong("discord_inbound_rate_seconds").Item;
            if (cooldown > 0 && lastSpoke.TryGetValue(accountName, out var prev)
                && (DateTime.UtcNow - prev).TotalSeconds < cooldown)
                return;

            lastSpoke[accountName] = DateTime.UtcNow;

            Broadcast(speaker, message);
        }

        /// <summary>
        /// Inject into General, and only General (Chris, 2026-08-07: "discord inbound should
        /// only hit general chat /cg"). Trade and LFG from someone who cannot see the goods or
        /// the group would be noise, and Roleplay from outside the world is a category error.
        /// </summary>
        private static void Broadcast(IPlayer speaker, string message)
        {
            if (PropertyManager.GetBool("chat_disable_general").Item)
                return;

            // Marked so nobody can mistake it for someone standing next to them. ASCII on
            // purpose: names go over the wire as CP1252 and the length prefix counts
            // CHARACTERS, so a non-CP1252 glyph here would desync the packet rather than merely
            // render badly (the 023 lesson). The speaker's own name may carry the dagger, which
            // IS CP1252-safe at 0x86.
            var prefix = PropertyManager.GetString("discord_inbound_prefix").Item ?? "[Discord] ";
            var displayName = prefix + speaker.Name;

            var msg = new GameMessageTurbineChat(
                ChatNetworkBlobType.NETBLOB_EVENT_BINARY,
                ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                TurbineChatChannel.General,
                displayName,
                message,
                speaker.Guid.Full,
                ChatType.General);

            // The speaker may be offline, in which case there is no WorldObject to squelch
            // against - SquelchDB.Contains takes one. So squelch is honoured when they are
            // online and silently cannot be when they are not. Documented rather than hidden:
            // if that becomes a real problem, the fix is a name-based squelch check, not a
            // pretence that this one covers it.
            var online = PlayerManager.GetOnlinePlayer(speaker.Guid);

            foreach (var recipient in PlayerManager.GetAllOnline())
            {
                if (!recipient.GetCharacterOption(CharacterOption.ListenToGeneralChat))
                    continue;

                if (recipient.IsOlthoiPlayer)
                    continue;

                if (online != null && recipient.SquelchManager.Squelches.Contains(online, ChatMessageType.AllChannels))
                    continue;

                recipient.Session.Network.EnqueueSend(msg);
            }

            log.Info($"[CHAT][Discord] {displayName} says, \"{message}\"");
        }

        private static string Get(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static string Trunc(string s, int n) =>
            s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}
