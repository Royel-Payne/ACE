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
    /// This is the bridge: the same answer, at Sentinel.
    ///
    /// OUTPUT DELIBERATELY MIRRORS `listplayers` - `Name : AccountId` per line, then a total.
    /// The plugin then needs ONE parser for both, and an Admin using either command gets
    /// identical text. Do not "improve" the format without changing the plugin.
    ///
    /// Not a security boundary: it only reads. Like every command above Player, it is captured
    /// by the 045 audit hook, so a roster pull is on the record.
    /// </summary>
    public static class ShadowgainRosterCommands
    {
        [CommandHandler("sg-roster", AccessLevel.Sentinel, CommandHandlerFlag.None, 0,
            "List the players currently online.",
            "\n"
            + "  Same output as @listplayers, which is Developer-only - this is the moderator-tier\n"
            + "  equivalent, so the admin GUI can show a full roster rather than only nearby players.")]
        public static void HandleRoster(Session session, params string[] parameters)
        {
            try
            {
                var sb = new StringBuilder();
                var count = 0u;

                foreach (var player in PlayerManager.GetAllOnline())
                {
                    // Player.Name carries the 023 progression marker. Left ON deliberately: this
                    // is a human/GUI-facing list, and the dagger is exactly the kind of thing a
                    // moderator wants to see. Anything matching a name against the DB must strip
                    // it, the same way ShadowgainInbound and /sg-multibox already do.
                    sb.Append($"{player.Name} : {player.Session.AccountId}\n");
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
