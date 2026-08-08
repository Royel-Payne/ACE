using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain: a tuning role that is NOT an admin role.
    ///
    /// Chris wants to hand the dials to Greylock - whose idea the whole experiment is - without
    /// handing over the server: "the intermediate level might be the better option for him so
    /// shutting down the server on accident doesn't happen."
    ///
    /// No stock access level separates those. modifybool/modifylong/modifydouble and
    /// shutdown/stop-now are ALL AccessLevel.Admin, so granting the dials grants the off switch.
    /// Dropping to Developer is worse, not better: 189 commands including import-sql (rewrites the
    /// world database), create/createcreature, and magic god. Envoy still carries delete, ban and
    /// smite.
    ///
    /// So this is least-privilege by construction rather than by tier:
    ///   - Advocate level, the lowest non-player rank
    ///   - RequiresWorld, so it can only be used in-game and never from the server console
    ///   - whitelisted to the Shadowgain dials ONLY - identified by "(Shadowgain" in the property
    ///     description, which is exactly the set documented in DIALS.md. Every other server
    ///     property remains untouchable.
    ///
    /// An Advocate therefore gets the experiment's control surface plus read-only server stats,
    /// and cannot shut down, ban, delete, spawn items or import SQL.
    ///
    /// Every change is written to the audit channel and the server log with the character name,
    /// because a shared dial is worthless if nobody can tell who moved it.
    /// </summary>
    public static class ShadowgainCommands
    {
        private const string Marker = "(Shadowgain";

        /// <summary>
        /// Shadowgain 021: choose your progression lane.
        ///
        /// Default is the hard lane. Switching to fast trips a permanent ratchet - the `*` marker
        /// and honour-roll eligibility are forfeit the instant it is chosen, and coming back to the
        /// hard lane restores neither. Without that, a player would race ahead on fast and toggle
        /// back to reclaim the marker, and the marker would mean nothing.
        ///
        /// Deliberately AccessLevel.Player: this is a gameplay choice, not an admin action.
        /// </summary>
        [CommandHandler("masochist", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Choose your progression lane. The hard lane is the default and carries the name mark.",
            "[ on | off ]\n"
            + "  on   - the hard lane. A multi-year climb. Keeps the * if you have never left it.\n"
            + "  off  - the fast lane. Months instead of years.\n"
            + "         PERMANENTLY forfeits the * marker and your place on the honour roll.\n"
            + "         Coming back to the hard lane does NOT restore either.\n"
            + "  (no argument shows your current lane)")]
        public static void HandleMasochist(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (parameters == null || parameters.Length == 0)
            {
                var lane = player.ShadowgainFastPath ? "FAST" : "HARD";

                Send(session, $"Progression lane: {lane} (speed x{player.ProgressionSpeed:0.##}).");

                Send(session, player.IsMasochist
                    ? $"You still carry the {Mark()} - you have never taken the fast lane. Keep it that way."
                    : $"You have taken the fast lane at some point, so the {Mark()} is gone for good.");

                Send(session, "Use /masochist off for the fast lane, or /masochist on for the hard lane.");
                return;
            }

            var arg = parameters[0].ToLowerInvariant();

            if (arg != "on" && arg != "off")
            {
                Send(session, "Usage: /masochist [ on | off ]");
                return;
            }

            var wantFast = arg == "off";

            // Warn once before the irreversible step, and make them repeat it. This is the only
            // action in the game that permanently destroys something earned.
            if (wantFast && player.IsMasochist && !RecentlyWarned(player))
            {
                Warn(player);

                Send(session, $"This will PERMANENTLY remove your {Mark()} and your honour-roll place.");
                Send(session, "Returning to the hard lane later will NOT give them back.");
                Send(session, "Type /masochist off again within 30 seconds if you are sure.");
                return;
            }

            if (!player.SetProgressionLane(wantFast))
            {
                Send(session, $"You are already on the {(wantFast ? "fast" : "hard")} lane.");
                return;
            }

            if (wantFast)
            {
                Send(session, $"Fast lane engaged - progression is now x{player.ProgressionSpeed:0.##}.");
                Send(session, $"Your {Mark()} is gone, permanently. No hard feelings; go and enjoy it.");
            }
            else
            {
                Send(session, $"Hard lane engaged - progression is now x{player.ProgressionSpeed:0.##}.");

                Send(session, player.IsMasochist
                    ? $"Your {Mark()} stands."
                    : $"The {Mark()} does not return, but the long road is open to you again.");
            }
        }

        // 30-second confirmation window for the irreversible switch, kept in memory on purpose -
        // a restart clearing it just means the player is asked to confirm again.
        private static readonly Dictionary<uint, DateTime> lastWarned = new Dictionary<uint, DateTime>();

        private static bool RecentlyWarned(Player player)
        {
            return lastWarned.TryGetValue(player.Guid.Full, out var when)
                && (DateTime.UtcNow - when).TotalSeconds <= 30;
        }

        private static void Warn(Player player) => lastWarned[player.Guid.Full] = DateTime.UtcNow;

        /// <summary>
        /// Shadowgain 037: explain the two lanes on demand.
        ///
        /// `/masochist` is where the CHOICE lives, but nothing in game ever mentions it, so a new
        /// player has no way to discover that a choice exists at all. This is the signpost.
        ///
        /// It reports TWO facts that are easy to conflate and are genuinely different:
        ///   - `ShadowgainFastPath` - the lane you are on RIGHT NOW
        ///   - `IsMasochist` (i.e. !ShadowgainForfeitedMarker) - whether you still carry the mark
        /// A character who took the fast lane once and switched back is on the hard lane but has
        /// forfeited the mark permanently. Reading only the marker (as the task entry originally
        /// suggested) would report that person as "currently on: fast", which is simply wrong -
        /// and would hide the fact that the ratchet is exactly what makes the mark mean anything.
        /// </summary>
        [CommandHandler("paths", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Explain the two progression lanes and show which one you are on.")]
        public static void HandlePaths(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var mark = Mark();

            Send(session, "Two roads to the top of Shadowgain:");
            Send(session, $"  /masochist on   - the hard, slow road (the default). Keeps the {mark} mark and your Honour Roll spot.");
            Send(session, "  /masochist off  - the fast lane, months instead of years.");
            Send(session, $"Taking the fast lane even once forfeits the {mark} mark and your Honour Roll spot FOREVER - returning to the slow road never restores it. Both roads end at the same power.");

            Send(session, $"You are currently on: {(player.ShadowgainFastPath ? "the FAST lane" : "the hard road")}.");

            // Stated separately because it is a separate fact. Someone who switched back to the
            // hard road still reads "hard road" above while having lost the mark for good.
            Send(session, player.IsMasochist
                ? $"You still carry the {mark} mark and remain eligible for the Honour Roll."
                : $"You have forfeited the {mark} mark permanently, and are no longer eligible for the Honour Roll.");
        }

        /// <summary>
        /// The hard-path mark as it currently reads, from the live dial rather than a literal.
        /// 023 made the prefix configurable (dagger by default, ASCII fallback if a client font
        /// lacks it), so hard-coding it here would leave /masochist telling players about a symbol
        /// they cannot see. Trimmed because the stored value carries a trailing space to separate
        /// it from the name.
        /// </summary>
        private static string Mark()
        {
            var prefix = PropertyManager.GetString("progression_marker_prefix").Item;

            return string.IsNullOrWhiteSpace(prefix) ? "mark" : prefix.Trim();
        }

        [CommandHandler("sg-dial", AccessLevel.Advocate, CommandHandlerFlag.RequiresWorld, 0,
            "List, read or set a Shadowgain tuning dial. Shadowgain properties only - no other server settings.",
            "[filter | <dial> | <dial> <value>]\n"
            + "  sg-dial                 - list every dial and its current value\n"
            + "  sg-dial summoning       - list dials matching a word\n"
            + "  sg-dial skill_gain_multiplier        - show one dial with its full description\n"
            + "  sg-dial skill_gain_multiplier 1.5    - set it (takes effect immediately)")]
        public static void HandleDial(Session session, params string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
            {
                ListDials(session, null);
                return;
            }

            var name = parameters[0].ToLowerInvariant();

            if (parameters.Length == 1)
            {
                if (Kind(name) == null)
                {
                    // not a dial name - treat it as a search term
                    ListDials(session, name);
                    return;
                }

                Send(session, $"{name} = {CurrentValue(name)}");
                Send(session, Describe(name));
                return;
            }

            // set - the value is everything after the name, so string dials keep their spaces
            var raw = string.Join(" ", parameters.Skip(1)).Trim();

            SetDial(session, name, raw);
        }

        private static void SetDial(Session session, string name, string raw)
        {
            var kind = Kind(name);

            if (kind == null)
            {
                Send(session, $"'{name}' is not a Shadowgain dial. Use /sg-dial to list them.");
                return;
            }

            var before = CurrentValue(name);

            switch (kind)
            {
                case "bool":
                    if (!TryParseBool(raw, out var b))
                    {
                        Send(session, $"{name} expects true or false.");
                        return;
                    }
                    PropertyManager.ModifyBool(name, b);
                    break;

                case "long":
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        Send(session, $"{name} expects a whole number.");
                        return;
                    }
                    PropertyManager.ModifyLong(name, l);
                    break;

                case "double":
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        Send(session, $"{name} expects a number, e.g. 1.25");
                        return;
                    }
                    PropertyManager.ModifyDouble(name, d);
                    break;

                default:
                    PropertyManager.ModifyString(name, raw);
                    break;
            }

            var after = CurrentValue(name);

            Send(session, $"{name}: {before} -> {after}");

            var who = session?.Player?.Name ?? "CONSOLE";

            PlayerManager.BroadcastToAuditChannel(session?.Player,
                $"{who} set Shadowgain dial {name}: {before} -> {after}");

            // Shadowgain 045: the durable half. The in-game Audit channel is ephemeral (only
            // staff who happen to be online with that channel active ever see it) and the server
            // log needs SSH plus grep. This writes the same fact to sgaudit.jsonl, which the bot
            // mirrors into #audit and which /sg-dial-history reads back.
            //
            // The generic command hook cannot produce this line: it sees that `/sg-dial x 2.0`
            // ran, but has no way to know what x WAS. before/after only exists here.
            ShadowgainAudit.EmitDial(who, name, before, after);
        }

        private static void ListDials(Session session, string filter)
        {
            var names = AllDials()
                .Where(n => filter == null || n.Contains(filter))
                .OrderBy(n => n)
                .ToList();

            if (names.Count == 0)
            {
                Send(session, $"No Shadowgain dials match '{filter}'.");
                return;
            }

            Send(session, $"--- Shadowgain dials ({names.Count}) ---");

            foreach (var n in names)
                Send(session, $"  {n} = {CurrentValue(n)}");

            Send(session, "Use /sg-dial <name> for the full description, or /sg-dial <name> <value> to change it.");
        }

        // ---- whitelist: a dial is a property whose description marks it as ours ----

        private static IEnumerable<string> AllDials()
        {
            foreach (var kvp in DefaultPropertyManager.DefaultBooleanProperties)
                if (Tunable(kvp.Key, kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultLongProperties)
                if (Tunable(kvp.Key, kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultDoubleProperties)
                if (Tunable(kvp.Key, kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultStringProperties)
                if (Tunable(kvp.Key, kvp.Value.Description)) yield return kvp.Key;
        }

        /// <summary>
        /// Shadowgain dials that are deliberately NOT reachable from `/sg-dial`.
        ///
        /// The whitelist below is "any property whose description contains the Shadowgain marker",
        /// and `/sg-dial` is AccessLevel.Advocate. That combination means a normally-described
        /// audit dial would be switchable off by an Advocate - i.e. the audit that exists to watch
        /// them could be silenced by them, without leaving a trace beyond the act of silencing it.
        ///
        /// These stay Admin/console-only (`/modifybool`). Anything whose whole job is to constrain
        /// or observe privileged users belongs here, not in the tuning surface.
        /// </summary>
        private static readonly HashSet<string> NotTunableHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audit_commands_enabled",
        };

        private static bool Marks(string description) =>
            description != null && description.Contains(Marker);

        private static bool Tunable(string name, string description) =>
            Marks(description) && !NotTunableHere.Contains(name);

        private static string Kind(string name)
        {
            if (DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(name, out var b) && Tunable(name, b.Description))
                return "bool";
            if (DefaultPropertyManager.DefaultLongProperties.TryGetValue(name, out var l) && Tunable(name, l.Description))
                return "long";
            if (DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(name, out var d) && Tunable(name, d.Description))
                return "double";
            if (DefaultPropertyManager.DefaultStringProperties.TryGetValue(name, out var s) && Tunable(name, s.Description))
                return "string";
            return null;
        }

        private static string CurrentValue(string name)
        {
            switch (Kind(name))
            {
                case "bool": return PropertyManager.GetBool(name).Item ? "true" : "false";
                case "long": return PropertyManager.GetLong(name).Item.ToString(CultureInfo.InvariantCulture);
                case "double": return PropertyManager.GetDouble(name).Item.ToString("0.####", CultureInfo.InvariantCulture);
                case "string": return PropertyManager.GetString(name).Item;
                default: return "?";
            }
        }

        private static string Describe(string name)
        {
            switch (Kind(name))
            {
                case "bool": return DefaultPropertyManager.DefaultBooleanProperties[name].Description;
                case "long": return DefaultPropertyManager.DefaultLongProperties[name].Description;
                case "double": return DefaultPropertyManager.DefaultDoubleProperties[name].Description;
                case "string": return DefaultPropertyManager.DefaultStringProperties[name].Description;
                default: return "";
            }
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            switch (raw.ToLowerInvariant())
            {
                case "1": case "on": case "yes": case "true": value = true; return true;
                case "0": case "off": case "no": case "false": value = false; return true;
                default: value = false; return false;
            }
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
