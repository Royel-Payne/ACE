using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

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
                if (Marks(kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultLongProperties)
                if (Marks(kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultDoubleProperties)
                if (Marks(kvp.Value.Description)) yield return kvp.Key;

            foreach (var kvp in DefaultPropertyManager.DefaultStringProperties)
                if (Marks(kvp.Value.Description)) yield return kvp.Key;
        }

        private static bool Marks(string description) =>
            description != null && description.Contains(Marker);

        private static string Kind(string name)
        {
            if (DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(name, out var b) && Marks(b.Description))
                return "bool";
            if (DefaultPropertyManager.DefaultLongProperties.TryGetValue(name, out var l) && Marks(l.Description))
                return "long";
            if (DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(name, out var d) && Marks(d.Description))
                return "double";
            if (DefaultPropertyManager.DefaultStringProperties.TryGetValue(name, out var s) && Marks(s.Description))
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
