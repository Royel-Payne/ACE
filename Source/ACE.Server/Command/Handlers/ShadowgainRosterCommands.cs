using System;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 054: the online roster, at a tier a moderator can actually reach.
    ///
    /// WHY THIS EXISTS. The admin/mod GUI wants a roster panel, and neither existing route works
    /// for a moderator:
    ///
    ///   - Decal's WorldFilter only indexes objects the CLIENT knows about, so a plugin can see
    ///     NEARBY players and nothing else. Someone across Dereth simply is not in it.
    ///   - ACE's own `listplayers` does return the full roster, but it is AccessLevel.Developer -
    ///     above Sentinel, so the mod tier cannot call it even after a promotion.
    ///
    /// This is the bridge: the same answer, from Advocate up (names only below Sentinel).
    ///
    /// OUTPUT DELIBERATELY MIRRORS `listplayers` - `Name : AccountId` per line, then a total.
    /// The plugin then needs ONE parser for both, and an Admin using either command gets
    /// identical text. Do not "improve" the format without changing the plugin.
    ///
    /// Below Sentinel the account id is omitted and the line is just `Name`. The TERMINATOR is
    /// deliberately identical in both cases - "Total connected Players: N" - so the plugin still
    /// has one parser and one stop condition rather than a per-tier variant to keep in step.
    ///
    /// Not a security boundary: it only reads. Like every command above Player, it is captured
    /// by the 045 audit hook, so a roster pull is on the record.
    /// </summary>
    public static class ShadowgainRosterCommands
    {
        [CommandHandler("sg-whoami", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Report your own access level.",
            "Answers one question: what tier is the account I am logged in on? "
            + "Exists so the admin console can draw the tabs your rank can actually use.")]
        public static void HandleWhoAmI(Session session, params string[] parameters)
        {
            // WHY A COMMAND FOR THIS. Decal never sees the server's AccessLevel, so the console
            // previously had to GUESS its tier by firing a command only a tier could run and
            // watching whether it was refused. That was fragile in both directions - a probe that
            // failed for an unrelated reason silently demoted the operator, and every probe hit
            // the audit trail and printed usage text into chat. Asking is deterministic, costs one
            // round trip, and reads the same value the server enforces with.
            //
            // AccessLevel.Player because it reports ONLY the caller's own level, which they can
            // already discover by trying a command. There is nothing here to leak: a Player asking
            // learns they are a Player.
            //
            // NOT a security boundary. The console uses the answer to decide what to DRAW; every
            // command it then fires is re-checked server-side, so a forged or wrong answer buys
            // nothing but buttons that get refused.
            var level = session?.AccessLevel ?? AccessLevel.Player;

            // Fixed, machine-readable shape - the plugin string-matches the part after the colon.
            // Do not localise or decorate it.
            CommandHandlerHelper.WriteOutputInfo(session, $"AccessLevel: {level}", ChatMessageType.Broadcast);
        }

        [CommandHandler("sg-roster", AccessLevel.Advocate, CommandHandlerFlag.None, 0,
            "List the players currently online.",
            "\n"
            + "  Same output as @listplayers, which is Developer-only - this is the moderator-tier\n"
            + "  equivalent, so the admin GUI can show a full roster rather than only nearby players.\n"
            + "  Advocates get NAMES ONLY; account ids are Sentinel and above.")]
        public static void HandleRoster(Session session, params string[] parameters)
        {
            try
            {
                // WHY ADVOCATE CAN CALL THIS AT ALL. 054 set it to Sentinel, but the console's
                // Advocate tier draws a roster and its only verb - Go to - acts on a roster
                // SELECTION. At Sentinel-only the tier was shipped functionally dead: the one
                // button an Advocate has could never be reached, because the list feeding it
                // could never populate.
                //
                // The sensitive part was never the names, it is the ACCOUNT ID: it links
                // characters to accounts, which is to say it reveals who is an alt of whom. So the
                // id is what is gated, not the roster. An Advocate sees who is online, which is
                // roughly what standing in a town square tells them anyway.
                var showAccounts = session == null || session.AccessLevel >= AccessLevel.Sentinel;

                var sb = new StringBuilder();
                var count = 0u;

                foreach (var player in PlayerManager.GetAllOnline())
                {
                    // Player.Name carries the 023 progression marker. Left ON deliberately: this
                    // is a human/GUI-facing list, and the dagger is exactly the kind of thing a
                    // moderator wants to see. Anything matching a name against the DB must strip
                    // it, the same way ShadowgainInbound and /sg-multibox already do.
                    sb.Append(showAccounts
                        ? $"{player.Name} : {player.Session.AccountId}\n"
                        : $"{player.Name}\n");

                    count++;
                }

                sb.Append($"Total connected Players: {count}\n");

                CommandHandlerHelper.WriteOutputInfo(session, sb.ToString(), ChatMessageType.Broadcast);
            }
            catch (Exception ex)
            {
                // A roster read must never take the caller down with it.
                CommandHandlerHelper.WriteOutputInfo(session, $"Could not build the roster: {ex.Message}",
                    ChatMessageType.Broadcast);
            }
        }
    }
}
