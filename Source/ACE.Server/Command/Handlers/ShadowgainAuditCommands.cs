using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 045: reading the audit trail back, and undoing a dial change from it.
    ///
    /// DELIBERATELY IN ITS OWN FILE AND AT A HIGHER ACCESS LEVEL THAN THE DIALS THEMSELVES.
    /// `/sg-dial` is Advocate, because Chris wants Greylock tuning the experiment. These are
    /// Admin, because Chris's hard requirement is that the person being audited cannot read
    /// around, rewind, or undo the record of what they did. Same reasoning that put the audit
    /// dial itself out of `/sg-dial`'s reach.
    ///
    /// History is read straight back out of sgaudit.jsonl rather than kept in a table. The file
    /// is already the durable artifact - the bot mirrors it into #audit and it survives restarts -
    /// so a second store would be a second thing to keep in sync, and the first one to disagree
    /// with the record it is supposed to describe. These commands are Admin-only and rare, so
    /// reading a file on demand costs nothing that matters.
    /// </summary>
    public static class ShadowgainAuditCommands
    {
        /// <summary>
        /// Matches the appender's file in log4net.config.docker. Container-side path: the bot sees
        /// the same file at /opt/ACE/Logs through the bind mount.
        /// </summary>
        private const string AuditPath = "/ace/Logs/sgaudit.jsonl";

        private const int MaxShown = 10;

        /// <summary>
        /// How far back to read. The file is one line per privileged command, so it grows slowly,
        /// but this is unbounded input being read on a command - cap it rather than trust that.
        /// </summary>
        private const int MaxLinesScanned = 20000;

        [CommandHandler("sg-dial-history", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Show recorded dial changes, newest first.",
            "[dial]\n"
            + "  (no argument) - every dial that has been changed\n"
            + "  <dial>        - just that one\n"
            + "  Reads the durable audit trail, so it survives restarts and shows who changed what.\n"
            + "  Admin only: the trail must not be readable-around by the people it records.")]
        public static void HandleDialHistory(Session session, params string[] parameters)
        {
            // 077: no argument now means EVERY dial, rather than a usage error.
            //
            // The Oversight tab used to open empty and demand a dial name, which assumes the
            // operator already knows which dial to be suspicious of. Chris: "this would be more
            // useful if it auto-populated with any changes that are made and only stays empty when
            // the server is on defaults." Right - the interesting question is "what has been
            // touched", and that is unanswerable if you must name the answer to ask it.
            var dial = parameters == null || parameters.Length == 0 ? null : parameters[0].Trim();

            if (dial != null && dial.Length == 0)
                dial = null;

            var entries = ReadDialHistory(dial, out var error);

            if (error != null)
            {
                Send(session, error);
                return;
            }

            if (entries.Count == 0)
            {
                if (dial == null)
                {
                    Send(session, "No dial changes recorded - every dial is at its shipped default.");
                }
                else
                {
                    Send(session, $"No recorded changes for '{dial}'.");
                    Send(session, "Either it has never been changed, or the change predates the audit trail.");
                }

                return;
            }

            var subject = dial ?? "all dials";
            Send(session, $"--- {subject}: {entries.Count} recorded change{(entries.Count == 1 ? "" : "s")} ---");

            // The dial name goes on EVERY line, in both modes. Redundant when one dial was asked
            // for, and deliberately so: one output shape means the console needs one parser rather
            // than two that can drift apart.
            var shown = 0;
            for (var i = entries.Count - 1; i >= 0 && shown < MaxShown; i--, shown++)
            {
                var e = entries[i];
                Send(session, $"  {e.When}  {e.Dial}  {e.Who}: {e.Before} -> {e.After}");
            }

            if (entries.Count > MaxShown)
                Send(session, $"  ... {entries.Count - MaxShown} older change(s) not shown.");
        }

        [CommandHandler("sg-revert", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Undo the most recent recorded change to a Shadowgain dial.",
            "<dial>\n"
            + "  Sets the dial back to the 'before' value of its latest recorded change.\n"
            + "  Run /sg-dial-history <dial> first to see what that will be.\n"
            + "  Admin only.")]
        public static void HandleRevert(Session session, params string[] parameters)
        {
            var dial = parameters == null || parameters.Length == 0 ? null : parameters[0].Trim();

            if (string.IsNullOrWhiteSpace(dial))
            {
                Send(session, "Usage: /sg-revert <dial>");
                return;
            }

            var entries = ReadDialHistory(dial, out var error);

            if (error != null)
            {
                Send(session, error);
                return;
            }

            if (entries.Count == 0)
            {
                Send(session, $"No recorded change for '{dial}' to revert.");
                return;
            }

            var latest = entries[entries.Count - 1];

            // Deliberately routed through the SAME command the change came from, rather than
            // writing the property directly. That way the revert is itself audited, announced on
            // the audit channel, and validated/parsed exactly like any other change - an undo that
            // left no trace would be the one hole in the trail.
            Send(session, $"Reverting {dial}: {latest.After} -> {latest.Before} (undoing {latest.Who} at {latest.When}).");

            ShadowgainCommands.HandleDial(session, dial, latest.Before);
        }

        private struct DialChange
        {
            public string Dial;
            public string When;
            public string Who;
            public string Before;
            public string After;
        }

        /// <summary>
        /// Oldest-first list of recorded changes for one dial.
        /// </summary>
        private static List<DialChange> ReadDialHistory(string dial, out string error)
        {
            error = null;
            var results = new List<DialChange>();

            if (!File.Exists(AuditPath))
            {
                error = "No audit trail on disk yet - nothing has been recorded since the feed was enabled.";
                return results;
            }

            try
            {
                // FileShare.ReadWrite because log4net holds this file open for writing. Opening it
                // exclusively would fail, and worse, could disturb the writer.
                using (var fs = new FileStream(AuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    var scanned = 0;
                    string line;

                    while ((line = reader.ReadLine()) != null && scanned++ < MaxLinesScanned)
                    {
                        // log4net writes a UTF-8 BOM when it creates the file, which makes the
                        // first line unparseable unless it is stripped. Same trap as 031.
                        line = line.Trim().TrimStart('﻿').Trim();

                        if (line.Length == 0 || line[0] != '{')
                            continue;

                        // Cheap pre-filter so a large file is not fully JSON-parsed line by line.
                        if (line.IndexOf("\"type\":\"dial\"", StringComparison.Ordinal) < 0)
                            continue;

                        try
                        {
                            using (var doc = JsonDocument.Parse(line))
                            {
                                var root = doc.RootElement;

                                if (!root.TryGetProperty("dial", out var nameEl))
                                    continue;

                                // A null dial means "every dial" - what the console asks for when
                                // the Oversight tab is opened, so the operator sees what has been
                                // changed without having to already know its name.
                                if (dial != null && !string.Equals(nameEl.GetString(), dial, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                results.Add(new DialChange
                                {
                                    Dial = nameEl.GetString(),
                                    When = root.TryGetProperty("t", out var t) ? t.GetString() : "?",
                                    Who = root.TryGetProperty("who", out var w) ? w.GetString() : "?",
                                    Before = root.TryGetProperty("before", out var b) ? b.GetString() : "?",
                                    After = root.TryGetProperty("after", out var a) ? a.GetString() : "?",
                                });
                            }
                        }
                        catch (JsonException)
                        {
                            // One malformed line must not hide the rest of the history.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"Could not read the audit trail: {ex.Message}";
            }

            return results;
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
