using System;
using System.Collections.Generic;
using System.Text;

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
        /// Privileged but PURELY INFORMATIONAL commands, excluded from the trail.
        ///
        /// This is a deliberate narrowing of "audit every privileged command", and it exists
        /// because the first live test produced four audit lines a minute from nobody: the
        /// site's own status exporter runs `serverstatus` on a 15s systemd timer, which is
        /// ~5,700 machine-generated lines a day. That would bury every real staff action, and
        /// would rate-limit the Discord mirror for no benefit.
        ///
        /// The test for inclusion here is "reads state, changes nothing". An audit exists to
        /// answer "who did what to the server"; a read that alters nothing has no answer to
        /// contribute, so excluding it costs no accountability. Anything that MUTATES - even
        /// harmlessly - stays audited.
        /// </summary>
        private static readonly HashSet<string> ReadOnlyNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serverstatus",         // polled every 15s by tools/sitedata.sh
            "serverperformance",
            "allstats",
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

                if (ReadOnlyNoise.Contains(command))
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
                AppendJsonString(sb, before);
                sb.Append(",\"after\":");
                AppendJsonString(sb, after);
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
                return string.Join(" ", parameters);

            var sb = new StringBuilder();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i < keep ? parameters[i] : "***");
            }
            return sb.ToString();
        }

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
