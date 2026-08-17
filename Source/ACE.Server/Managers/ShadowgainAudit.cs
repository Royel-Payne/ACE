using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Shadowgain 045: a durable audit trail for every PRIVILEGED command.
    ///
    /// Chris's framing: keep everyone honest, including himself. So this is not a lockout - it
    /// records rather than restricts, and the person it most needs to be tamper-proof against is
    /// whoever holds the second-highest access level on the server.
    ///
    /// WHY ONE HOOK INSTEAD OF PER-COMMAND CALLS. Every command, typed or console, resolves
    /// through CommandManager.GetCommandHandler and only executes when that returns Ok or
    /// SudoOk. Emitting at those two returns covers every privileged command that exists today
    /// and every one added later, with no per-command work and nothing to forget. Verified both
    /// callers: GameActionTalk handles Ok and SudoOk; the console path handles Ok only (a console
    /// session is null, so it can never be sudo).
    ///
    /// It records AUTHORISED ATTEMPTS - the point where the server has agreed to run the command.
    /// A command that then throws still appears here, which is what you want from an audit: the
    /// question is "what did they try to do", not "what succeeded".
    ///
    /// SAFETY: called on the command path for every staff action. Nothing here may throw into its
    /// caller - a broken audit must never break the server's ability to run commands. Every entry
    /// point is wrapped and degrades to "no audit line".
    /// </summary>
    public static class ShadowgainAudit
    {
        // additivity=false in log4net.config, so this JSON stays out of ACE_Log.txt and the console.
        private static readonly ILog auditLog = LogManager.GetLogger("Shadowgain.Audit");

        private static readonly ILog log = LogManager.GetLogger(typeof(ShadowgainAudit));

        /// <summary>
        /// Commands whose ARGUMENTS must never be written to the trail, and how many leading
        /// arguments are safe to keep.
        ///
        /// The audit is mirrored into a Discord channel and kept forever by design, so a password
        /// landing in it is a durable, replicated leak rather than a transient one. Keeping the
        /// first argument preserves the useful half - WHICH account was created or changed - while
        /// dropping the secret. `passwd` is AccessLevel.Player and so is already filtered out
        /// before it reaches here; it is listed anyway, because relying on a filter elsewhere to
        /// protect a secret here is exactly the assumption that stops being true later.
        /// </summary>
        private static readonly Dictionary<string, int> SensitiveArgs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "accountcreate",       1 },   // accountcreate <account> <password> [accesslevel]
            { "set-accountpassword", 1 },   // set-accountpassword <account> <password>
            { "passwd",              0 },   // passwd <old> <new>
        };

        /// <summary>
        /// Commands that are NOT written to the trail.
        ///
        /// 160: THE CRITERION IS UNFAIR GAMEPLAY, NOT ACTIVITY. Chris, after eight days of live
        /// data: "the point of audit isn't to reveal harmless commands, it's to prevent abuse
        /// like giving items or xp or createitem... we shouldn't be monitored for EVERY action
        /// unless it's an action that creates unfair gameplay."
        ///
        /// The evidence agreed. Of 263 entries between 2026-08-09 and 2026-08-16, roughly three
        /// quarters were our own tooling talking to itself - 64 `listplayers`, 20 `sg-appraise`
        /// from one afternoon of 158 diagnostics, 41 shutdown/world lines from routine deploys,
        /// 21 `resyncproperties`, 13 `fetch*` reads. The staff actions that matter - 33 dial
        /// changes, 5 `set-accountaccess`, 12 `sg-multibox`, 7 progression repairs - were a
        /// minority buried inside them. A trail nobody can skim is not a trail.
        ///
        /// THE DEFAULT IS STILL TO AUDIT. This is a denylist, not an allowlist, and that
        /// asymmetry is the point: a new command that is forgotten here gets RECORDED, which is
        /// the safe direction to fail. An allowlist would silently exempt whatever nobody
        /// remembered to add, which is the one failure this file cannot afford.
        ///
        /// Three tests admit a command, and nothing else does:
        ///
        ///   1. READS - answers a question, changes nothing.
        ///   2. SERVER OPERATIONS - changes the SERVER, but cannot advantage a CHARACTER.
        ///      Shutting the world down is disruptive and visible to everyone; it cannot hand
        ///      anyone an item, a level, or a rank. It belongs in a deploy log, not here.
        ///   3. STAFF SELF-MOVEMENT - moves only the caller. Anything that moves ANOTHER
        ///      player stays audited, because that is an action taken ON someone.
        ///
        /// Not admitted, however quiet: anything that creates or destroys an object, moves XP or
        /// levels, edits a character, changes a dial, or changes who holds access.
        /// </summary>
        private static readonly HashSet<string> NotAudited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // --- 1. reads ------------------------------------------------------------------
            "serverstatus",         // polled every 15s by tools/sitedata.sh
            "serverperformance",
            "allstats",
            "sg-roster",            // polled by the admin console plugin to draw its roster panel
            "sg-dial-history",      // reading the trail is not an action on the server
            // 124a: THE SAME TRAP THIS LIST WAS BUILT FOR, SPRUNG A SECOND TIME. `listplayers`
            // is sg-roster's Developer-tier twin - identical output, identically read-only - and
            // it was not here. Putting it on the 30-second status poll to feed the web character
            // sheet's "online" dot flooded Discord #audit with 64 lines in half an hour and
            // buried the staff actions the channel exists to surface.
            //
            // Two commands with the same output and the same effect on the world must not be
            // audited differently, or the next person to poll one of them repeats this.
            "listplayers",
            "finger",
            // The dial READERS. `modify*` and `setproperty` write and stay audited; these four
            // only report what a dial currently holds.
            "fetchbool",
            "fetchlong",
            "fetchdouble",
            "fetchstring",
            // 158's oracle and its siblings: each one serialises state to a file or the log and
            // returns. `sg-appraise` is the reason this entry exists - a single sweep across the
            // nine online characters wrote 20 audit lines about reading other people's armour.
            "sg-appraise",
            "sg-objdesc",
            "sg-xptable",
            "sg-skillattrs",

            // --- 2. server operations ------------------------------------------------------
            // Every LIVE deploy runs the shutdown sequence and reopens the world, which is ~5
            // lines a time and was 41 of the 263. DEPLOY.md is the record of those, and it is a
            // better one: it says what shipped, which this never could.
            "shutdown",
            "cancel-shutdown",
            "set-shutdown-interval",
            "world",
            // Reloads the dial cache from the database. It cannot introduce a value - the write
            // that produced the value was itself audited, which is the line worth keeping.
            "resyncproperties",

            // --- 3. staff self-movement ----------------------------------------------------
            // Going somewhere is not an advantage on a server where staff are not competing.
            // `teletome` and `teleallto` are deliberately ABSENT: they move other people.
            "tele",
            "teleto",
            "telereturn",
            "telepoi",
            "teleloc",
            "telexyz",
            "teletype",
            "teledist",
            "teledungeon",
            "sg-tele",
        };

        /// <summary>
        /// Record an authorised privileged command.
        ///
        /// <paramref name="session"/> is null for the server console.
        /// </summary>
        public static void EmitCommand(Session session, CommandHandlerInfo commandInfo, string[] parameters, bool sudo)
        {
            try
            {
                if (commandInfo?.Attribute == null)
                    return;

                // The noise filter. AccessLevel.Player covers /tell, /roll, @bug and every other
                // ordinary player command - auditing those would bury the staff actions this
                // exists to surface, and would relay ordinary chat into a Discord channel.
                if (commandInfo.Attribute.Access == AccessLevel.Player)
                    return;

                if (!PropertyManager.GetBool("audit_commands_enabled").Item)
                    return;

                var command = commandInfo.Attribute.Command ?? "?";

                if (NotAudited.Contains(command))
                    return;

                var sb = new StringBuilder(256);
                sb.Append("{\"type\":\"command\",\"t\":\"");
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                sb.Append("\",\"account\":");
                AppendJsonString(sb, session?.Account ?? "CONSOLE");
                sb.Append(",\"character\":");
                AppendJsonString(sb, session?.Player?.Name);
                sb.Append(",\"source\":\"");
                sb.Append(session == null ? "console" : "player");
                sb.Append("\",\"access\":\"");
                sb.Append(commandInfo.Attribute.Access.ToString());
                sb.Append("\",\"command\":");
                AppendJsonString(sb, command);
                sb.Append(",\"args\":");
                AppendJsonString(sb, Redact(command, parameters));
                sb.Append(",\"sudo\":");
                sb.Append(sudo ? "true" : "false");
                sb.Append('}');

                auditLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                log.Warn($"[SHADOWGAIN-AUDIT] command emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Record a dial change with its before and after.
        ///
        /// Separate from EmitCommand because the command hook can only see that `/sg-dial x 2.0`
        /// was run - it cannot know what the value WAS. Both lines land in the same feed on
        /// purpose, so #audit shows the action and its effect together, and so
        /// /sg-dial-history has a single stream to read.
        /// </summary>
        public static void EmitDial(string who, string dial, string before, string after)
        {
            try
            {
                if (!PropertyManager.GetBool("audit_commands_enabled").Item)
                    return;

                var sb = new StringBuilder(192);
                sb.Append("{\"type\":\"dial\",\"t\":\"");
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                sb.Append("\",\"who\":");
                AppendJsonString(sb, who);
                sb.Append(",\"dial\":");
                AppendJsonString(sb, dial);
                sb.Append(",\"before\":");
                AppendJsonString(sb, MaskAddresses(before));
                sb.Append(",\"after\":");
                AppendJsonString(sb, MaskAddresses(after));
                sb.Append('}');

                auditLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                log.Warn($"[SHADOWGAIN-AUDIT] dial emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Join the arguments, dropping any that are secret for this command.
        /// Unknown commands keep all arguments - the denylist is explicit, so a new sensitive
        /// command has to be added here deliberately.
        /// </summary>
        private static string Redact(string command, string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return "";

            if (!SensitiveArgs.TryGetValue(command, out var keep))
                return MaskAddresses(string.Join(" ", parameters));

            var sb = new StringBuilder();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i < keep ? parameters[i] : "***");
            }
            return MaskAddresses(sb.ToString());
        }

        /// <summary>
        /// Blank the middle of any IPv4 address, and any IPv6 address entirely.
        ///
        /// #audit is readable by every verified player and is retained forever, so an address
        /// written into it is a durable disclosure of where someone lives, to an audience that
        /// includes the person's peers rather than just the operator. The multibox commands
        /// (044) put real addresses straight into command arguments, which is how this surfaced.
        ///
        /// Masked rather than removed: `72.x.x.79` still lets Chris tell two entries apart, match
        /// one against a report, and see that a change happened - which is the entire job of the
        /// trail. The octets that identify a subscriber are the ones dropped.
        ///
        /// Note this is defence in depth, not the primary answer: /sg-multibox deliberately takes
        /// a CHARACTER NAME, so the normal path never types an address at all.
        /// </summary>
        private static string MaskAddresses(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            try
            {
                text = Ipv4.Replace(text, m => $"{m.Groups[1].Value}.x.x.{m.Groups[4].Value}");
                text = Ipv6.Replace(text, "[ipv6 masked]");
                return text;
            }
            catch (Exception)
            {
                // Never let a masking failure emit the unmasked original.
                return "[redacted]";
            }
        }

        private static readonly Regex Ipv4 = new Regex(
            @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b",
            RegexOptions.Compiled);

        // Deliberately blunt: anything with two or more colons among hex groups. An IPv6 address
        // has no safe "keep the ends" form the way IPv4 does, so it goes entirely.
        private static readonly Regex Ipv6 = new Regex(
            @"\b(?=[0-9A-Fa-f:]*:[0-9A-Fa-f:]*:)[0-9A-Fa-f:]{3,}\b",
            RegexOptions.Compiled);

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters would make the line unparseable to the bot. The
                        // dagger and other printable non-ASCII pass through as UTF-8, which the
                        // appender is configured to write.
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
