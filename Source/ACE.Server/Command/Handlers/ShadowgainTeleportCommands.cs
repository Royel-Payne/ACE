using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 055 + 058: the two teleport-flavoured commands the admin/mod GUI needs.
    ///
    /// Both exist because ACE's own equivalents sit at the wrong tier, or do not exist at all:
    ///
    ///   /sg-tele        - `telepoi` is Developer, so a moderator cannot use it. This is the
    ///                     Sentinel-tier equivalent, and it covers MORE than the alternative.
    ///   /sg-portalstorm - ACE has no working storm trigger whatsoever. The `@storm*`/`@lb*`
    ///                     knobs are empty stubs and `portalstorm` is a Developer SELF-test that
    ///                     fires the events on the caller and drops them at 0,0. The four client
    ///                     events are implemented and proven though, so this is selection and
    ///                     delivery only - no new client work.
    ///
    /// Neither is a security boundary. Both are above AccessLevel.Player, so both are captured by
    /// the 045 audit hook and land in #audit.
    /// </summary>
    public static class ShadowgainTeleportCommands
    {
        // ------------------------------------------------------------------ 058: /sg-tele

        [CommandHandler("sg-tele", AccessLevel.Sentinel, CommandHandlerFlag.RequiresWorld, 1,
            "Teleport yourself to a named Point of Interest.",
            "<poi|list>\n"
            + "  Example: /sg-tele arwic\n"
            + "  Example: /sg-tele town network\n"
            + "  Matching is case-insensitive and accepts a unique prefix, so 'crag' finds Cragstone.\n"
            + "  Use /sg-tele list to see every destination.")]
        public static void HandleSgTele(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            // Multi-word names arrive as separate parameters ("town network").
            var query = string.Join(" ", parameters ?? new string[0]).Trim();

            if (string.IsNullOrWhiteSpace(query))
            {
                Send(session, "Usage: /sg-tele <poi>   (or /sg-tele list)");
                return;
            }

            DatabaseManager.World.CacheAllPointsOfInterest();
            var pois = DatabaseManager.World.GetPointsOfInterestCache();

            if (query.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var names = pois.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                Send(session, $"--- {names.Count} destinations ---");

                // Chunked: one line per POI would be 60+ messages, and a single line would blow
                // past the client's string limit.
                for (var i = 0; i < names.Count; i += 8)
                    Send(session, "  " + string.Join(", ", names.Skip(i).Take(8).ToArray()));

                return;
            }

            var match = Resolve(pois.Keys, query, out var ambiguous);

            if (match == null)
            {
                if (ambiguous != null && ambiguous.Count > 0)
                {
                    Send(session, $"'{query}' matches {ambiguous.Count} destinations: {string.Join(", ", ambiguous.Take(8).ToArray())}");
                    Send(session, "Be more specific.");
                }
                else
                {
                    Send(session, $"No destination matches '{query}'. Use /sg-tele list.");
                }
                return;
            }

            var poi = DatabaseManager.World.GetCachedPointOfInterest(match);

            if (poi == null)
            {
                Send(session, $"'{match}' is in the index but could not be loaded.");
                return;
            }

            var weenie = DatabaseManager.World.GetCachedWeenie(poi.WeenieClassId);
            var dest = weenie?.GetPosition(PositionType.Destination);

            if (dest == null)
            {
                Send(session, $"'{match}' has no destination recorded.");
                return;
            }

            var pos = new ACE.Entity.Position(dest);

            // The reason this covers all 62 destinations and the alternative covered 50.
            // Driving `@tele` needs MAP coordinates, and GetMapCoords returns null for any
            // indoor cell - so the Marketplace, the Town Network and the rest of the interiors
            // were simply unreachable that way. Teleporting from the stored Position skips the
            // conversion entirely. AdjustDungeon is what makes the interior landing correct,
            // and is exactly what telepoi does.
            WorldObject.AdjustDungeon(pos);

            // Capture the return point BEFORE the jump - the other half of 052's "Return me",
            // which was specified but never built. Without it the button could only ever undo
            // an admin summon, because that is the only thing stock ACE records.
            if (player.Location != null)
                player.SetPosition(PositionType.TeleportedCharacter, new ACE.Entity.Position(player.Location));

            Send(session, $"Teleporting to {match}.");
            player.Teleport(pos);
        }

        /// <summary>
        /// Exact match first, then unique case-insensitive prefix, then unique substring.
        /// Returns null and fills <paramref name="ambiguous"/> when a query matches several.
        /// </summary>
        private static string Resolve(ICollection<string> names, string query, out List<string> ambiguous)
        {
            ambiguous = null;

            var exact = names.FirstOrDefault(n => string.Equals(n, query, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var prefix = names.Where(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();

            // Aliases point at one destination (Hotel / Hotel Swank / HotelSwank / Swank), so a
            // prefix hitting several names is not necessarily ambiguous - dedupe on the target
            // before giving up.
            if (prefix.Count == 1)
                return prefix[0];

            if (prefix.Count > 1)
            {
                var single = SingleDestination(prefix);
                if (single != null)
                    return single;

                ambiguous = prefix;
                return null;
            }

            var contains = names.Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (contains.Count == 1)
                return contains[0];

            if (contains.Count > 1)
            {
                var single = SingleDestination(contains);
                if (single != null)
                    return single;

                ambiguous = contains;
            }

            return null;
        }

        /// <summary>
        /// If every candidate resolves to the same weenie, they are aliases - pick the shortest
        /// name and treat it as unambiguous.
        /// </summary>
        private static string SingleDestination(List<string> candidates)
        {
            try
            {
                var ids = candidates
                    .Select(n => DatabaseManager.World.GetCachedPointOfInterest(n))
                    .Where(p => p != null)
                    .Select(p => p.WeenieClassId)
                    .Distinct()
                    .ToList();

                if (ids.Count == 1)
                    return candidates.OrderBy(n => n.Length).First();
            }
            catch (Exception)
            {
                // Fall through to "ambiguous" - a lookup failure must not teleport someone
                // somewhere they did not ask for.
            }

            return null;
        }

        // ------------------------------------------------------- 055: /sg-portalstorm

        [CommandHandler("sg-portalstorm", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Clear a congested landblock with a portal storm.",
            "[landblock] [count]\n"
            + "  (no argument) - storms YOUR current landblock, everyone in it\n"
            + "  /sg-portalstorm 0xC6A9      - storms that landblock\n"
            + "  /sg-portalstorm 0xC6A9 3    - storms it, but only the first 3 players\n"
            + "\n"
            + "  Players are warned, then sent to their LIFESTONE. You are never stormed.\n"
            + "  Anyone with no lifestone recorded is left alone.")]
        public static void HandleSgPortalStorm(Session session, params string[] parameters)
        {
            var caller = session?.Player;

            if (caller?.Location == null)
                return;

            var landblock = caller.Location.LandblockId.Landblock;
            var max = int.MaxValue;

            if (parameters != null && parameters.Length > 0 && !string.IsNullOrWhiteSpace(parameters[0]))
            {
                var raw = parameters[0].Trim();
                var hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;

                if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out landblock))
                {
                    Send(session, $"'{raw}' is not a landblock id. Expected something like 0xC6A9.");
                    return;
                }
            }

            if (parameters != null && parameters.Length > 1 && !int.TryParse(parameters[1], out max))
            {
                Send(session, $"'{parameters[1]}' is not a number.");
                return;
            }

            if (max <= 0)
            {
                Send(session, "Count must be at least 1.");
                return;
            }

            // The invoking admin is excluded deliberately: this is a tool for moving OTHER
            // people, and storming yourself out of the landblock you are trying to observe is
            // never what was meant.
            var targets = PlayerManager.GetAllOnline()
                .Where(p => p.Location != null
                         && p.Location.LandblockId.Landblock == landblock
                         && p.Guid.Full != caller.Guid.Full)
                .Take(max)
                .ToList();

            if (targets.Count == 0)
            {
                Send(session, $"No other players in landblock 0x{landblock:X4}.");
                return;
            }

            // Skip anyone with no lifestone rather than inventing a destination. ACE does the
            // same in its own no-log-landblock handling: no Sanctuary recorded, no move. The
            // self-test command's 0x7F7F001C (0,0) is a debug artifact and is NOT used here.
            var movable = targets.Where(p => p.GetPosition(PositionType.Sanctuary) != null).ToList();
            var skipped = targets.Count - movable.Count;

            Send(session, $"Portal storm on landblock 0x{landblock:X4}: {movable.Count} player(s) will be moved to their lifestone.");

            if (skipped > 0)
                Send(session, $"{skipped} skipped - no lifestone recorded.");

            // Replayed with delays so it reads as weather rather than a yank. The client already
            // knows all four of these; only the timing and the teleport are ours.
            foreach (var target in movable)
                target.Session.Network.EnqueueSend(new GameEventPortalStormBrewing(target.Session));

            var chain = new ActionChain();

            chain.AddDelaySeconds(5.0f);
            chain.AddAction(caller, () =>
            {
                foreach (var target in movable)
                    target.Session.Network.EnqueueSend(new GameEventPortalStormImminent(target.Session));
            });

            chain.AddDelaySeconds(5.0f);
            chain.AddAction(caller, () =>
            {
                foreach (var target in movable)
                {
                    // Re-read the lifestone at fire time: five seconds is long enough for
                    // someone to have logged out, died, or walked off the landblock.
                    var home = target.GetPosition(PositionType.Sanctuary);

                    if (home == null || target.Session == null)
                        continue;

                    // The event immediately precedes the teleport, matching the client's
                    // expectation from the stock self-test.
                    target.Session.Network.EnqueueSend(new GameEventPortalStorm(target.Session));
                    target.Teleport(new ACE.Entity.Position(home));
                }
            });

            chain.AddDelaySeconds(2.0f);
            chain.AddAction(caller, () =>
            {
                foreach (var target in movable)
                {
                    if (target.Session != null)
                        target.Session.Network.EnqueueSend(new GameEventPortalStormSubsided(target.Session));
                }

                Send(session, "Portal storm complete.");
            });

            chain.EnqueueChain();
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
