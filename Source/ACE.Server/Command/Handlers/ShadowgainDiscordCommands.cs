using System;
using System.Collections.Concurrent;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 031: the two in-game commands the Discord integration needs.
    ///
    ///   @bug &lt;text&gt;    - file a bug report, funnelled to Discord #bugs and to disk
    ///   @verify &lt;code&gt;  - prove character ownership to link a Discord account
    ///
    /// Both are AccessLevel.Player - they are player-facing features, not admin tools - and both
    /// are RequiresWorld, so neither can be run from the server console where there is no player
    /// to attribute the action to.
    ///
    /// Kept in their own file rather than added to ShadowgainCommands.cs, which is specifically
    /// the tuning surface (/sg-dial) and the progression lane switch (/masochist). Different
    /// audience, different access level, different reason to change.
    ///
    /// A note on the AC client: it has no interactive dialogs or modals, so neither of these can
    /// prompt for structured input. Both are therefore one-line free-text commands. The Discord
    /// side of the bug funnel CAN use a modal, because that is Discord's own UI.
    /// </summary>
    public static class ShadowgainDiscordCommands
    {
        /// <summary>
        /// Last @bug time per character guid, for the anti-spam cooldown.
        ///
        /// Concurrent because although the world loop is single-threaded, ACE ticks physics and
        /// landblock groups in parallel and this is cheap insurance either way. Bounded by the
        /// number of distinct characters that have ever filed a bug this uptime, which is a
        /// rounding error next to the object graph the server already holds.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, DateTime> LastBug = new ConcurrentDictionary<uint, DateTime>();

        private const int MinBugLength = 10;
        private const int MaxBugLength = 500;

        [CommandHandler("bug", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Report a bug to the Shadowgain team.",
            "<what went wrong>\n"
            + "  Example: @bug the summoning skill did not rise when my pet got the killing blow\n"
            + "\n"
            + "  Your character name, level and location are attached automatically, so just\n"
            + "  describe the problem. The report is NOT posted to public chat - it goes to the\n"
            + "  team's bug channel. One report per minute.")]
        public static void HandleBug(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (!PropertyManager.GetBool("discord_bug_reports_enabled").Item)
            {
                Send(session, "Bug reporting is currently disabled.");
                return;
            }

            var text = parameters == null ? "" : string.Join(" ", parameters).Trim();

            if (text.Length < MinBugLength)
            {
                Send(session, $"Please describe the problem in a little more detail (at least {MinBugLength} characters).");
                Send(session, "Example: @bug the summoning skill did not rise when my pet got the killing blow");
                return;
            }

            if (text.Length > MaxBugLength)
                text = text.Substring(0, MaxBugLength) + "...";

            // Cooldown. The funnel writes to a file a human reads, so an unthrottled command is an
            // invitation to flood it.
            var cooldown = PropertyManager.GetLong("discord_bug_cooldown_seconds").Item;
            if (cooldown > 0 && LastBug.TryGetValue(player.Guid.Full, out var last))
            {
                var wait = cooldown - (int)(DateTime.UtcNow - last).TotalSeconds;
                if (wait > 0)
                {
                    Send(session, $"Please wait {wait} more second{(wait == 1 ? "" : "s")} before filing another report.");
                    return;
                }
            }

            LastBug[player.Guid.Full] = DateTime.UtcNow;

            ShadowgainRelay.EmitBug(session.Account, player.Name, player.Level ?? 0, DescribeLocation(player.Location), text);

            Send(session, "Bug report filed - thank you. Your character, level and location were attached automatically.");
        }

        /// <summary>
        /// Not a real linking step - a signpost.
        ///
        /// `/link` is the DISCORD slash command; the in-game half is `@verify &lt;code&gt;`. But
        /// the instructions say "run /link", and AC accepts commands with a `/` prefix, so
        /// typing `/link` in game is the obvious wrong guess. Chris made it within a minute of
        /// the feature going live. Without this the player gets a bare "Unknown command: link"
        /// and no path forward.
        ///
        /// Costs ~10 lines and removes a dead end, so it is worth more than the tidiness of
        /// having exactly one command per function.
        /// </summary>
        [CommandHandler("link", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "How to link your character to Discord.")]
        public static void HandleLink(Session session, params string[] parameters)
        {
            if (session?.Player == null)
                return;

            Send(session, "To link your character to Discord:");
            Send(session, "  1. In the Shadowgain Discord, run /link - the bot replies with a code.");
            Send(session, "  2. Back here, type: @verify <code>");
            Send(session, "/link only works in Discord; @verify only works in game.");
        }

        [CommandHandler("verify", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Link your Discord account by entering the code the Shadowgain bot gave you.",
            "<code>\n"
            + "  Run /link in the Shadowgain Discord first - the bot will send you a short code.\n"
            + "  Then type that code here, in game, on the character you want to link.\n"
            + "  Example: @verify K7P2QX")]
        public static void HandleVerify(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var code = parameters == null || parameters.Length == 0 ? "" : parameters[0].Trim();

            // Validate the SHAPE only. Whether the code is real, unexpired or already used is the
            // bot's business - it issued the code and it owns that state. The server's job is to
            // prove that whoever typed this controls this character, and to keep obvious junk out
            // of the events feed.
            if (code.Length < 4 || code.Length > 16 || !code.All(char.IsLetterOrDigit))
            {
                Send(session, "That does not look like a verification code. Codes are 4-16 letters and digits.");
                Send(session, "Run /link in the Shadowgain Discord to get one.");
                return;
            }

            ShadowgainRelay.EmitVerify(session.Account, player.Name, player.Level ?? 0, code.ToUpperInvariant());

            // Deliberately does NOT claim success. The bot decides whether the code matches, and
            // it is the thing that can actually grant the Discord role. Promising success here
            // would be a lie whenever the code is wrong or expired.
            Send(session, "Verification code sent. If it is valid, the Shadowgain bot will confirm in Discord shortly.");
        }

        /// <summary>
        /// Human-usable location string for a bug report.
        ///
        /// GetMapCoordStr() returns NULL for anywhere without surface map coordinates - dungeons,
        /// interiors, the Marketplace - which is precisely where bugs tend to get reported. The
        /// raw LOC string is always available and is what a developer would paste into @teleloc,
        /// so it is the part that must never be missing; the friendly coordinates are a bonus when
        /// they exist.
        /// </summary>
        private static string DescribeLocation(Position location)
        {
            if (location == null)
                return "unknown";

            var loc = location.ToLOCString();
            var coords = location.GetMapCoordStr();

            return coords == null ? loc : $"{coords} {loc}";
        }

        private static void Send(Session session, string text)
        {
            if (session?.Player != null)
                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else
                Console.WriteLine(text);
        }
    }
}
