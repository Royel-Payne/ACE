using System;
using System.Text;

using ACE.Server.Entity;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Shadowgain 031: the game -> Discord emission side of the Discord integration.
    ///
    /// The server does not talk to Discord. It appends JSON lines to files under /ace/Logs
    /// (host-visible at /opt/ACE/Logs, already mounted rw), and a separate bot tails them and
    /// posts. That split is deliberate: the bot can be restarted, retuned or rewritten without
    /// touching the game server, and the server needs no outbound HTTP, no POST helper and no
    /// Discord token.
    ///
    /// Why log4net appenders rather than a tail file we open ourselves, or a chat table:
    ///   - rotation, file handles, flushing and crash-safety are already solved
    ///   - no schema change, no DB write path - so the bot's MySQL user stays READ-ONLY
    ///   - the appender layout lives in log4net.config, which is on the mounted Config/ volume,
    ///     so the emitted format is a config change rather than a code change
    ///
    /// TWO files, not one. Chat is high-volume and disposable; bug reports and verification
    /// codes are low-volume and precious. Kept apart so a chat flood can never roll a bug
    /// report out of the backup window before the bot has read it.
    ///
    /// SAFETY: every public method here is called from live gameplay paths - the chat handler
    /// most of all. Nothing in this class may throw into its caller. Every entry point is
    /// wrapped, and a relay failure degrades to "no Discord message", never to broken chat.
    /// </summary>
    public static class ShadowgainRelay
    {
        // Separate loggers so log4net.config can route, filter or silence them independently.
        // Both are configured with additivity=false, which keeps this JSON out of ACE_Log.txt
        // and off the console - otherwise every chat line would be duplicated into the main log.
        private static readonly ILog chatLog = LogManager.GetLogger("Shadowgain.ChatRelay");
        private static readonly ILog eventLog = LogManager.GetLogger("Shadowgain.Events");

        private static readonly ILog log = LogManager.GetLogger(typeof(ShadowgainRelay));

        /// <summary>
        /// The relay allowlist: General, Trade, LFG, Roleplay.
        ///
        /// An ALLOWLIST, not a denylist, at Chris's explicit direction - any channel type added
        /// later (or any ID we failed to anticipate) defaults to NOT relayed. Allegiance (1),
        /// Society (6-9) and Olthoi (10) are private by nature and are never relayed; neither is
        /// local say, which is a different system entirely (GameActionTalk), nor the Channel-enum
        /// private channels (fellow / patron / vassals / monarch / staff), which never reach here.
        /// </summary>
        private static bool IsRelayableChannel(uint channelId) =>
            channelId == TurbineChatChannel.General ||
            channelId == TurbineChatChannel.Trade ||
            channelId == TurbineChatChannel.LFG ||
            channelId == TurbineChatChannel.Roleplay;

        private static string ChannelName(uint channelId)
        {
            if (channelId == TurbineChatChannel.General) return "General";
            if (channelId == TurbineChatChannel.Trade) return "Trade";
            if (channelId == TurbineChatChannel.LFG) return "LFG";
            if (channelId == TurbineChatChannel.Roleplay) return "Roleplay";
            return "Unknown";
        }

        /// <summary>
        /// Relay one public chat line.
        ///
        /// CALL SITE MATTERS. This is invoked at the END of TurbineChatHandler, next to
        /// LogTurbineChat - after every delivery branch, not near the top where the channel ID is
        /// resolved. The early design said "hook right after adjustedChannelID", which is wrong:
        /// the reject paths (chat_disable_*, the account-age / player-age / level gates,
        /// chat_echo_only) all return AFTER that point, so hooking there would relay lines that
        /// were never delivered in game. Reaching this call means the message actually went out.
        /// Gagged players return even earlier and so never reach here either.
        ///
        /// `name` is expected to be Player.Name, which already carries the 023 marker prefix,
        /// so the mark renders in Discord for free.
        /// </summary>
        public static void EmitChat(uint channelId, string name, string message)
        {
            try
            {
                if (!PropertyManager.GetBool("discord_relay_enabled").Item)
                    return;

                if (!IsRelayableChannel(channelId))
                    return;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message))
                    return;

                var max = (int)PropertyManager.GetLong("discord_relay_max_message").Item;
                if (max > 0 && message.Length > max)
                    message = message.Substring(0, max) + "...";

                var sb = new StringBuilder(256);
                sb.Append('{');
                AppendField(sb, "type", "chat", true);
                AppendField(sb, "ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                AppendField(sb, "channel", ChannelName(channelId));
                AppendNumber(sb, "channelId", channelId);
                AppendField(sb, "name", name);
                AppendField(sb, "message", message);
                sb.Append('}');

                chatLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                // Never let the relay break chat. Logged at WARN so a persistent failure is
                // visible in ACE_Log.txt without spamming ERROR on every line.
                log.Warn($"[SHADOWGAIN-RELAY] chat emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Emit a player bug report. Goes to the events file, never to public chat.
        /// </summary>
        public static void EmitBug(string account, string character, int level, string location, string text)
        {
            try
            {
                var sb = new StringBuilder(512);
                sb.Append('{');
                AppendField(sb, "type", "bug", true);
                AppendField(sb, "ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                AppendField(sb, "account", account);
                AppendField(sb, "character", character);
                AppendNumber(sb, "level", (uint)Math.Max(0, level));
                AppendField(sb, "location", location);
                AppendField(sb, "text", text);
                sb.Append('}');

                eventLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                log.Warn($"[SHADOWGAIN-RELAY] bug emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Emit a /verify attempt so the bot can match the code it issued to a real character.
        ///
        /// The account name is included because that, not the character, is the thing being
        /// linked - one Discord user owns an account, and every character on it.
        /// </summary>
        public static void EmitVerify(string account, string character, int level, string code)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append('{');
                AppendField(sb, "type", "verify", true);
                AppendField(sb, "ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                AppendField(sb, "account", account);
                AppendField(sb, "character", character);
                AppendNumber(sb, "level", (uint)Math.Max(0, level));
                AppendField(sb, "code", code);
                sb.Append('}');

                eventLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                log.Warn($"[SHADOWGAIN-RELAY] verify emit failed: {ex.Message}");
            }
        }

        private static void AppendField(StringBuilder sb, string key, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":\"");
            EscapeJson(sb, value);
            sb.Append('"');
        }

        private static void AppendNumber(StringBuilder sb, string key, uint value)
        {
            sb.Append(",\"").Append(key).Append("\":").Append(value);
        }

        /// <summary>
        /// Minimal JSON string escaping.
        ///
        /// Hand-rolled rather than pulled from a serializer because this runs per chat line and
        /// the input shape is known. It must still be correct: player names and chat text are
        /// arbitrary user input, and one unescaped quote or backslash produces a malformed line
        /// that breaks the bot's parser.
        ///
        /// Non-ASCII (the dagger among them) is passed through as-is and written as UTF-8 by the
        /// appender, which sets `encoding value="utf-8"` explicitly. That explicitness is
        /// deliberate - a previous exporter silently transcoded the dagger to CP1252 0x86 by
        /// inheriting a default encoding, and the corruption was invisible until a byte-level look.
        /// </summary>
        private static void EscapeJson(StringBuilder sb, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Remaining C0 controls have no short escape and are illegal raw in JSON.
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
