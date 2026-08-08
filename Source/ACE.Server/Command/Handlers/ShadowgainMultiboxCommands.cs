using System;
using System.Linq;
using System.Net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 044: the operator surface for per-IP session limits.
    ///
    /// TAKES A CHARACTER NAME, NOT AN IP. The cap has to be enforced on IP, because it runs
    /// before authentication - but nobody thinks in IPs, and asking Chris to find one for a
    /// player who is standing in front of him trying to log a second character is the kind of
    /// friction that means the feature never gets used. The command resolves the name itself:
    /// the live session's address if they are online, their last-login address if not, and a
    /// raw IP if that is what was typed.
    ///
    /// ADMIN ONLY, deliberately not Advocate like /sg-dial. Someone who can raise their own
    /// session cap is not capped, and someone who can add their own IP to the override map has
    /// exempted themselves. The dials it edits are held out of /sg-dial's whitelist for the
    /// same reason - see NotTunableHere in ShadowgainCommands.
    /// </summary>
    public static class ShadowgainMultiboxCommands
    {
        [CommandHandler("sg-multibox", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Per-IP concurrent session limits (anti-multibox).",
            "[ <name|ip> <N> | remove <name|ip> | global <N> ]\n"
            + "  (no argument)          - show the global cap and every override\n"
            + "  <name> <N>             - allow N concurrent sessions from that person's IP\n"
            + "                           N of -1 means unlimited. Resolves a character name,\n"
            + "                           an account name, or a raw IP.\n"
            + "  remove <name|ip>       - drop an override, returning that IP to the global cap\n"
            + "  global <N>             - set the cap for everyone without an override\n"
            + "                           -1 = unlimited (the shipped default)\n"
            + "\n"
            + "  ORDER MATTERS. Add your own exemption FIRST, then set the global cap, or you\n"
            + "  can cap yourself out of a second session before you are exempt.")]
        public static void HandleMultibox(Session session, params string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
            {
                ShowStatus(session);
                return;
            }

            var verb = parameters[0].ToLowerInvariant();

            if (verb == "global")
            {
                if (parameters.Length < 2 || !int.TryParse(parameters[1], out var g))
                {
                    Send(session, "Usage: /sg-multibox global <N>   (-1 = unlimited)");
                    return;
                }

                PropertyManager.ModifyLong("multibox_max_sessions_per_ip", g);

                Send(session, g < 0
                    ? "Global cap: UNLIMITED. The anti-multibox limit is now off."
                    : $"Global cap: {g} concurrent session{(g == 1 ? "" : "s")} per IP.");

                if (g >= 0 && ShadowgainMultibox.GetOverrides().Count == 0)
                    Send(session, "WARNING: no exemptions are set. You have just capped yourself too.");

                return;
            }

            if (verb == "remove")
            {
                if (parameters.Length < 2)
                {
                    Send(session, "Usage: /sg-multibox remove <name|ip>");
                    return;
                }

                var target = Resolve(parameters[1], out var how);

                if (target == null)
                {
                    Send(session, $"Could not work out an IP for '{parameters[1]}'.");
                    return;
                }

                Send(session, ShadowgainMultibox.RemoveOverride(target)
                    ? $"Removed the override for {target} ({how}). It now follows the global cap."
                    : $"{target} ({how}) had no override.");
                return;
            }

            // <name|ip> <N>
            if (parameters.Length < 2 || !int.TryParse(parameters[1], out var n))
            {
                Send(session, "Usage: /sg-multibox <name|ip> <N>   (-1 = unlimited)");
                return;
            }

            var ip = Resolve(parameters[0], out var source);

            if (ip == null)
            {
                Send(session, $"Could not work out an IP for '{parameters[0]}'.");
                Send(session, "They may never have logged in. Try again while they are online, or pass a raw IP.");
                return;
            }

            ShadowgainMultibox.SetOverride(ip, n);

            Send(session, n < 0
                ? $"{ip} ({source}) may now open UNLIMITED concurrent sessions."
                : $"{ip} ({source}) may now open {n} concurrent session{(n == 1 ? "" : "s")}.");

            if (source.StartsWith("last known", StringComparison.OrdinalIgnoreCase))
                Send(session, "That was their LAST KNOWN address - if it has changed since, this will not match.");
        }

        private static void ShowStatus(Session session)
        {
            var global = PropertyManager.GetLong("multibox_max_sessions_per_ip").Item;

            Send(session, global < 0
                ? "Global cap: UNLIMITED (anti-multibox is off)."
                : $"Global cap: {global} concurrent session{(global == 1 ? "" : "s")} per IP.");

            var overrides = ShadowgainMultibox.GetOverrides();

            if (overrides.Count == 0)
            {
                Send(session, "No per-IP overrides.");
                return;
            }

            Send(session, $"--- overrides ({overrides.Count}) ---");

            foreach (var kvp in overrides.OrderBy(k => k.Key))
                Send(session, $"  {kvp.Key} = {(kvp.Value < 0 ? "unlimited" : kvp.Value.ToString())}");
        }

        /// <summary>
        /// Character name, account name, or raw IP -> IP string. Null if nothing resolves.
        /// </summary>
        private static string Resolve(string arg, out string source)
        {
            source = "raw IP";

            if (string.IsNullOrWhiteSpace(arg))
                return null;

            arg = arg.Trim();

            if (IPAddress.TryParse(arg, out var parsed))
                return parsed.ToString();

            // Online first: their CURRENT address, which is the one the cap will actually see.
            // The offline path can only offer a last-known value, so prefer this whenever it
            // exists - and it usually will, since this gets run while someone is trying to log in.
            var wanted = StripMarkers(arg);

            var online = PlayerManager.GetAllOnline()
                .FirstOrDefault(p => string.Equals(StripMarkers(p.Name), wanted, StringComparison.OrdinalIgnoreCase));

            if (online?.Session?.EndPointC2S?.Address != null)
            {
                source = "online now";
                return online.Session.EndPointC2S.Address.ToString();
            }

            // Offline: character name -> account -> last login address. Falls back to treating
            // the argument as an account name, so either works without the caller knowing which.
            var offline = PlayerManager.FindByName(wanted);
            var accountName = offline?.Account?.AccountName;

            var account = DatabaseManager.Authentication.GetAccountByName(accountName ?? arg);

            if (account?.LastLoginIP != null && account.LastLoginIP.Length > 0)
            {
                try
                {
                    source = "last known address";
                    return new IPAddress(account.LastLoginIP).ToString();
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Names carry the 023 progression marker when read off a live player, so a typed name
        /// would never match one. Same trim set as the Player.Name setter.
        /// </summary>
        private static string StripMarkers(string name) =>
            name == null ? null : name.Trim().TrimStart('+', '*', '†', '[', ']', ' ').Trim();

        private static void Send(Session session, string text)
        {
            if (session?.Player != null)
                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else
                Console.WriteLine(text);
        }
    }
}
