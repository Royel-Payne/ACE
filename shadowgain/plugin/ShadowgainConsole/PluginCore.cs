using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;

using Decal.Adapter;
using Decal.Adapter.Wrappers;
using MyClasses.MetaViewWrappers;

namespace ShadowgainConsole
{
    /// <summary>
    /// Shadowgain 059: the in-client admin/mod console.
    ///
    /// WHAT THIS IS NOT: a security boundary. Every button fires the same text command the
    /// operator could type, as the logged-in character, so the SERVER re-checks AccessLevel and
    /// the 045 audit hook records it. Nothing drawn here can grant power the account lacks.
    ///
    /// TIER GATING IS NOT IMPLEMENTED, deliberately, and the probes that used to fire for it have
    /// been REMOVED. The brief calls for tabs a tier cannot use to be absent rather than greyed,
    /// but MetaViewWrappers' INotebook exposes only ActiveTab and Change - there is no remove-page
    /// method - and MVWireupHelper.WireupStart takes no raw-XML overload, so the view can only be
    /// built from the embedded resource as written. Doing it properly means bypassing the wireup
    /// helper and calling IView.InitializeRawXML with pages stripped from the XML string; that is
    /// a real change, not a tweak. Until then the probes were pure cost: two commands fired at
    /// every login whose answer nothing could act on, one of which printed
    /// "Usage: /sg-dial-history <dial>" into the operator's chat and both of which hit the audit.
    ///
    /// TWO MEDIUM RULES THIS FILE OBEYS:
    ///   - no modals exist in VVS, so destructive actions use inline arm -> confirm;
    ///   - do NOT touch raw network interop. ACBridge's ServerDispatch hook was the suspected
    ///     cause of the client silently closing after ~1 minute, so everything here stays on the
    ///     documented Actions / ChatBoxMessage surface.
    /// </summary>
    [WireUpBaseEvents]
    [MVView("ShadowgainConsole.mainView.xml")]
    [MVWireUpControlEvents]
    [FriendlyName("Shadowgain Console")]
    public class PluginCore : PluginBase
    {
        // ---- roster -------------------------------------------------------------------
        private readonly List<string> roster = new List<string>();
        private string target = null;

        // ---- points of interest -------------------------------------------------------
        private sealed class Poi
        {
            public string Name;
            public string Coords;
        }

        private readonly List<Poi> allPoi = new List<Poi>();
        private readonly List<Poi> shownPoi = new List<Poi>();
        private string selectedPoi = null;

        // ---- multibox overrides / dial history ----------------------------------------
        private readonly List<string[]> mbRows = new List<string[]>();
        private readonly List<string[]> histRows = new List<string[]>();

        // ---- command-output capture ---------------------------------------------------
        // The plugin has no API to "run a command and get output" - it fires text and watches
        // chat. So a capture is a short window during which matching lines are consumed rather
        // than ignored. Timestamped so a lost reply cannot leave capture stuck on forever.
        private enum Capture { None, Roster, Status, Multibox, History }
        private Capture capturing = Capture.None;
        private DateTime captureStarted = DateTime.MinValue;
        private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(3);

        // ---- inline arm/confirm -------------------------------------------------------
        // Which destructive button is armed, and when. Arming expires so a console left open
        // overnight cannot be one stray click away from a shutdown.
        private string armed = null;
        private DateTime armedAt = DateTime.MinValue;
        private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(10);

        // ---- misc state ---------------------------------------------------------------
        private Timer timer;
        private DateTime lastAutoRefresh = DateTime.MinValue;
        private bool viewCreated = false;

        // -1 unknown, 0 off, 1 on. Unknown until the operator clicks, because the server does
        // not volunteer the current value and guessing "off" would make the first click a no-op.
        private int attackable = -1;

        private string statusPlayers = "?";
        private string statusUptime = "?";
        private string queriedDial = "";

        // `Name : AccountId`, the format /sg-roster shares with listplayers so one parser serves
        // both.
        private static readonly Regex RosterLine = new Regex(@"^(?<name>.+?)\s+:\s+(?<acct>\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex StatusPlayers = new Regex(@"(?<n>\d+)\s+players online", RegexOptions.Compiled);
        private static readonly Regex StatusUptime = new Regex(@"Server Runtime:\s*(?<up>[^\r\n,]+)", RegexOptions.Compiled);

        // "  10.0.0.5 = unlimited" / "  10.0.0.5 = 2"
        private static readonly Regex MbOverride = new Regex(@"^(?<who>\S+)\s*=\s*(?<n>\S+)$", RegexOptions.Compiled);
        // "  2026-08-09 01:22  Chris: 5 -> 8"
        private static readonly Regex HistLine = new Regex(@"^(?<when>.+?)\s{2,}(?<who>[^:]+):\s*(?<before>.*?)\s*->\s*(?<after>.*)$", RegexOptions.Compiled);

        protected override void Startup()
        {
            try
            {
                Globals.Init("ShadowgainConsole", Host, Core);

                // NOTE: the view is deliberately NOT created here. MVWireupHelper picks a view
                // system by scanning LOADED assemblies for VirindiViewService, so creating the
                // view during Startup is a race against VVS's own load order. Losing that race
                // does not throw - it silently falls back to Decal's view system, and the plugin
                // then runs perfectly while never drawing a window. Created at LoginComplete
                // instead, by which point VVS is certainly loaded.
                Core.ChatBoxMessage += Core_ChatBoxMessage;

                timer = new Timer();
                timer.Interval = 1000;
                timer.Tick += Timer_Tick;
                timer.Start();
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        protected override void Shutdown()
        {
            try
            {
                if (timer != null) { timer.Stop(); timer.Tick -= Timer_Tick; timer = null; }
                Core.ChatBoxMessage -= Core_ChatBoxMessage;

                if (viewCreated)
                {
                    MVWireupHelper.WireupEnd(this);
                    viewCreated = false;
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ login

        [BaseEvent("LoginComplete", "CharacterFilter")]
        private void CharacterFilter_LoginComplete(object sender, EventArgs e)
        {
            try
            {
                if (!viewCreated)
                {
                    // Report which system was chosen. A silent fallback to DecalInject is the
                    // difference between a working console and an invisible one, so it is worth
                    // saying out loud rather than discovering it from an empty screen.
                    var vvs = ViewSystemSelector.IsPresent(Host, ViewSystemSelector.eViewSystem.VirindiViewService);

                    MVWireupHelper.WireupStart(this, Host);
                    viewCreated = true;

                    Util.WriteToChat(vvs
                        ? "console ready (Virindi views)."
                        : "VirindiViewService not detected - the window will not appear. Is VVS enabled?");

                    LoadPoi();
                }

                Refresh();
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ chat capture

        private void Core_ChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                if (e == null || e.Text == null)
                    return;

                if (capturing == Capture.None)
                    return;

                // Consume it: this is the console's own plumbing talking to itself, and the
                // operator should not have to read it.
                e.Eat = true;

                if (DateTime.UtcNow - captureStarted > CaptureWindow)
                {
                    // The window closed without a terminator. Keep whatever arrived rather than
                    // discarding it - a short list is better than a blank panel.
                    FinishCapture();
                    return;
                }

                foreach (var raw in e.Text.Split('\n'))
                {
                    var line = raw.Trim();

                    if (line.Length == 0)
                        continue;

                    if (capturing == Capture.Roster)
                    {
                        if (line.StartsWith("Total connected Players", StringComparison.OrdinalIgnoreCase))
                        {
                            FinishCapture();
                            return;
                        }

                        var m = RosterLine.Match(line);
                        if (m.Success)
                            roster.Add(m.Groups["name"].Value.Trim());
                    }
                    else if (capturing == Capture.Status)
                    {
                        var p = StatusPlayers.Match(line);
                        var u = StatusUptime.Match(line);

                        if (u.Success) statusUptime = u.Groups["up"].Value.Trim();

                        // Finish on the PLAYER count, not on uptime: serverstatus prints
                        // "Server Runtime:" first, so finishing there closed the window before
                        // the count arrived and the strip permanently read "? online".
                        if (p.Success)
                        {
                            statusPlayers = p.Groups["n"].Value;
                            FinishCapture();
                            return;
                        }
                    }
                    else if (capturing == Capture.Multibox)
                    {
                        if (line.StartsWith("Global cap:", StringComparison.OrdinalIgnoreCase))
                        {
                            SetText("lblCapHint", line);
                            continue;
                        }

                        if (line.StartsWith("No per-IP overrides", StringComparison.OrdinalIgnoreCase))
                        {
                            FinishCapture();
                            return;
                        }

                        // Skip the "--- overrides (n) ---" header; only rows carry '='.
                        if (line.StartsWith("---"))
                            continue;

                        var m = MbOverride.Match(line);
                        if (m.Success)
                            mbRows.Add(new string[] { m.Groups["who"].Value, m.Groups["n"].Value });
                    }
                    else if (capturing == Capture.History)
                    {
                        if (line.StartsWith("No recorded changes", StringComparison.OrdinalIgnoreCase))
                        {
                            SetText("lblOversight", line);
                            FinishCapture();
                            return;
                        }

                        if (line.StartsWith("---") || line.StartsWith("..."))
                            continue;

                        var m = HistLine.Match(line);
                        if (m.Success)
                            histRows.Add(new string[]
                            {
                                m.Groups["when"].Value.Trim(),
                                queriedDial,
                                m.Groups["before"].Value.Trim() + " -> " + m.Groups["after"].Value.Trim(),
                                m.Groups["who"].Value.Trim()
                            });
                    }
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private void FinishCapture()
        {
            var was = capturing;
            capturing = Capture.None;

            if (was == Capture.Roster)
                RedrawRoster();
            else if (was == Capture.Status)
                RedrawStatus();
            else if (was == Capture.Multibox)
                RedrawMultibox();
            else if (was == Capture.History)
                RedrawHistory();
        }

        private void BeginCapture(Capture what)
        {
            capturing = what;
            captureStarted = DateTime.UtcNow;
        }

        // ------------------------------------------------------------------ refresh

        private void Refresh()
        {
            roster.Clear();
            BeginCapture(Capture.Roster);
            Fire("/sg-roster");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Expire an arm the operator walked away from.
                if (armed != null && DateTime.UtcNow - armedAt > ArmWindow)
                    Disarm("Confirmation expired.");

                if (capturing != Capture.None && DateTime.UtcNow - captureStarted > CaptureWindow)
                    FinishCapture();

                // Auto-refresh ONLY while the window is open, and slowly.
                //
                // The first version polled every 30s regardless, which flooded #audit with
                // sg-roster - the exact thing a comment two files away warns against. Two fixes:
                // sg-roster is now in the audit's read-only filter server-side, and the poll no
                // longer runs at all for a console nobody is looking at.
                if (!ViewIsOpen())
                    return;

                if (DateTime.UtcNow - lastAutoRefresh > TimeSpan.FromSeconds(60))
                {
                    lastAutoRefresh = DateTime.UtcNow;

                    if (capturing == Capture.None)
                        Refresh();
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Send a command as the logged-in character. This is the ONLY way this plugin acts, and
        /// it echoes what it sent - the mock's green strip, and the quickest way to see that a
        /// button built the argument it was supposed to.
        /// </summary>
        private void Fire(string command)
        {
            try
            {
                SetText("lblEcho", "> " + command);
                Globals.Host.Actions.InvokeChatParser(command);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        /// <summary>
        /// Strip the display marker from a name so it can be used as a command argument.
        ///
        /// The previous version listed the characters to remove - '+', '*', the 023 dagger - and
        /// was wrong in a way that only showed up in game. The server sends chat as CP1252
        /// (WriteString16L), where the dagger is 0x86, and by the time Decal hands the line to a
        /// plugin it has become '?'. A TrimStart looking for '†' matched nothing, so
        /// "? Black Breath" went to the server verbatim and it answered
        /// "Player Black Breath was not found."
        ///
        /// A character class cannot be wrong about which glyph survived the round trip: an AC
        /// character name always begins with a letter, so anything before the first letter or
        /// digit is decoration by definition.
        /// </summary>
        private static string Bare(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var i = 0;
            while (i < name.Length && !char.IsLetterOrDigit(name[i]))
                i++;

            return name.Substring(i).Trim();
        }

        private bool RequireTarget()
        {
            if (!string.IsNullOrEmpty(target))
                return true;

            SetText("lblPlayers", "Select a player from the roster first.");
            return false;
        }

        /// <summary>
        /// Inline arm/confirm, because VVS has no modal. First click arms and relabels; a second
        /// click inside the window commits.
        /// </summary>
        private bool ArmOrConfirm(string key, string label, string prompt)
        {
            if (armed == key)
            {
                armed = null;
                return true;
            }

            armed = key;
            armedAt = DateTime.UtcNow;
            SetText(label, prompt);
            return false;
        }

        private void Disarm(string note)
        {
            armed = null;
            SetText("lblPlayers", note);
            SetText("lblServer", note);
            SetText("lblOversight", note);
        }

        // ------------------------------------------------------------------ redraws

        private void RedrawStatus()
        {
            SetText("lblStatus", "Players online: " + statusPlayers + "   -   Uptime: " + statusUptime);
            SetText("lblSrv1", "ShadowgainSVR (ACE) - up " + statusUptime);
            SetText("lblSrv2", "players " + statusPlayers);
            SetText("lblSrv3", "");
        }

        private void RedrawRoster()
        {
            SetText("lblRoster", "Online - " + roster.Count);

            var list = GetList("lstRoster");
            if (list == null) return;

            try
            {
                // Rebuilt wholesale rather than diffed. The roster is tiny and refreshes once a
                // minute, so a clear-and-refill costs nothing and cannot drift out of sync with
                // the `roster` list that the selection handler indexes into.
                list.Clear();

                for (var i = 0; i < roster.Count; i++)
                {
                    var row = list.AddRow();

                    // Visible column is cleaned: the marker arrives mangled to '?' through the
                    // CP1252 chat path, and a column of "? Name" reads like an error rather than
                    // a badge. The hidden column carries exactly what goes on the wire.
                    row[0][0] = Bare(roster[i]);
                    row[1][0] = Bare(roster[i]);
                }

                // A selection that no longer exists would target the wrong person after a
                // refresh, so drop it if the player has gone.
                if (target != null && !roster.Contains(target))
                {
                    target = null;
                    SetText("lblTarget", "Target: (none selected)");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private void RedrawMultibox()
        {
            var list = GetList("lstMb");
            if (list == null) return;

            try
            {
                list.Clear();

                for (var i = 0; i < mbRows.Count; i++)
                {
                    var row = list.AddRow();
                    row[0][0] = mbRows[i][0];
                    row[1][0] = mbRows[i][1];
                }

                SetText("lblAccess", mbRows.Count == 0
                    ? "No per-IP overrides."
                    : mbRows.Count + " override(s). Selecting a row fills the fields.");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private void RedrawHistory()
        {
            var list = GetList("lstHist");
            if (list == null) return;

            try
            {
                list.Clear();

                for (var i = 0; i < histRows.Count; i++)
                {
                    var row = list.AddRow();
                    row[0][0] = histRows[i][0];
                    row[1][0] = histRows[i][1];
                    row[2][0] = histRows[i][2];
                    row[3][0] = histRows[i][3];
                }

                if (histRows.Count > 0)
                    SetText("lblOversight", histRows.Count + " change(s). Revert undoes the newest.");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ points of interest

        /// <summary>
        /// Load the destination list shipped beside the DLL.
        ///
        /// Names drive the command - /sg-tele takes a NAME and the server resolves it - so the
        /// coordinates here are display only. That is what makes indoor destinations work: there
        /// is no coordinate maths on this side to fail on a dungeon cell.
        /// </summary>
        private void LoadPoi()
        {
            try
            {
                allPoi.Clear();

                var dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                var path = System.IO.Path.Combine(dir, "poi.tsv");

                if (!System.IO.File.Exists(path))
                {
                    SetText("lblPoi", "poi.tsv not found beside the plugin.");
                    return;
                }

                var seen = new List<string>();

                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;

                    var parts = line.Split('\t');
                    var name = parts[0].Trim();

                    if (name.Length == 0 || seen.Contains(name)) continue;

                    seen.Add(name);

                    var poi = new Poi();
                    poi.Name = name;
                    poi.Coords = parts.Length > 1 ? parts[1].Trim() : "";
                    allPoi.Add(poi);
                }

                FilterPoi("");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private void FilterPoi(string query)
        {
            shownPoi.Clear();

            if (query == null) query = "";
            query = query.Trim().ToLowerInvariant();

            for (var i = 0; i < allPoi.Count; i++)
            {
                if (query.Length == 0 || allPoi[i].Name.ToLowerInvariant().IndexOf(query) >= 0)
                    shownPoi.Add(allPoi[i]);
            }

            var list = GetList("lstPoi");
            if (list == null) return;

            try
            {
                list.Clear();

                for (var i = 0; i < shownPoi.Count; i++)
                {
                    var row = list.AddRow();
                    row[0][0] = shownPoi[i].Name;
                    row[1][0] = shownPoi[i].Coords;
                    row[2][0] = shownPoi[i].Name;
                }

                SetText("lblPoiHead", "Destination - " + shownPoi.Count + " of " + allPoi.Count + " shown");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ view plumbing

        /// <summary>
        /// True only when the console is actually on screen. Polling for a window nobody has
        /// open is pure noise - in chat, in #audit, and on the server.
        /// </summary>
        private bool ViewIsOpen()
        {
            try
            {
                if (!viewCreated) return false;
                var view = MVWireupHelper.GetDefaultView(this);
                return view != null && view.Visible;
            }
            catch { return false; }
        }

        private IList GetList(string control)
        {
            try
            {
                var view = MVWireupHelper.GetDefaultView(this);
                if (view == null) return null;
                return view[control] as IList;
            }
            catch { return null; }
        }

        /// <summary>
        /// Set the caption of a label, a button, or a text box.
        ///
        /// All three, because the old version only handled IStaticText and every
        /// SetText("txtMbName", ...) it made silently did nothing - the roster selection was
        /// supposed to fill the Access tab's name field and never did.
        /// </summary>
        private void SetText(string control, string text)
        {
            try
            {
                var view = MVWireupHelper.GetDefaultView(this);
                if (view == null) return;

                var c = view[control];
                if (c == null) return;

                var st = c as IStaticText;
                if (st != null) { st.Text = text; return; }

                var bt = c as IButton;
                if (bt != null) { bt.Text = text; return; }

                var tb = c as ITextBox;
                if (tb != null) { tb.Text = text; return; }
            }
            catch { }
        }

        private string GetText(string control)
        {
            try
            {
                var view = MVWireupHelper.GetDefaultView(this);
                if (view == null) return null;

                var edit = view[control] as ITextBox;
                if (edit != null) return edit.Text == null ? null : edit.Text.Trim();

                var st = view[control] as IStaticText;
                if (st != null) return st.Text == null ? null : st.Text.Trim();

                return null;
            }
            catch { return null; }
        }

        /// <summary>The logged-in character's own name, with any display marker removed.</summary>
        private string MyName()
        {
            try { return Bare(Globals.Core.CharacterFilter.Name); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Players tab

        [MVControlEvent("btnGoTo", "Click")]
        private void btnGoTo_Click(object sender, MVControlEventArgs e)
        {
            try { if (RequireTarget()) Fire("/teleto " + Bare(target)); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnSummon", "Click")]
        private void btnSummon_Click(object sender, MVControlEventArgs e)
        {
            try { if (RequireTarget()) Fire("/teletome " + Bare(target)); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnSendBack", "Click")]
        private void btnSendBack_Click(object sender, MVControlEventArgs e)
        {
            try { if (RequireTarget()) Fire("/telereturn " + Bare(target)); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnGag", "Click")]
        private void btnGag_Click(object sender, MVControlEventArgs e)
        {
            try { if (RequireTarget()) Fire("/gag " + Bare(target)); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnKick", "Click")]
        private void btnKick_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                if (!RequireTarget()) return;

                var who = Bare(target);

                if (ArmOrConfirm("kick", "lblPlayers", "Kick " + who + " to login? Click Kick again to confirm."))
                {
                    Fire("/boot " + who);
                    SetText("lblPlayers", "Kicked " + who + ".");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnKickNo", "Click")]
        private void btnKickNo_Click(object sender, MVControlEventArgs e)
        {
            try { Disarm("Cancelled."); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ Me tab

        [MVControlEvent("btnAttack", "Click")]
        private void btnAttack_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                // Unknown starts by turning it ON, which is the state a player is normally in -
                // so the first click on a fresh console is a no-op rather than a surprise.
                var next = attackable == 1 ? 0 : 1;

                Fire("/attackable " + (next == 1 ? "on" : "off"));

                attackable = next;
                SetText("btnAttack", "Attackable: " + (next == 1 ? "ON" : "OFF"));
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnReturn", "Click")]
        private void btnReturn_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                // telereturn requires a name - it has no self form - so the console supplies the
                // caller's own. The mock's bare "@telereturn" echo was mock text, not a command.
                var me = MyName();

                if (string.IsNullOrEmpty(me))
                {
                    SetText("lblPoi", "Could not read your character name.");
                    return;
                }

                Fire("/telereturn " + me);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("txtPoiFilter", "Change")]
        private void txtPoiFilter_Change(object sender, MVTextBoxChangeEventArgs e)
        {
            try { FilterPoi(GetText("txtPoiFilter")); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("lstPoi", "Selected")]
        private void lstPoi_Selected(object sender, MVListSelectEventArgs e)
        {
            try
            {
                if (e.Row < 0 || e.Row >= shownPoi.Count)
                    return;

                selectedPoi = shownPoi[e.Row].Name;
                SetText("lblPoi", selectedPoi + "   " + shownPoi[e.Row].Coords);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnPoiGo", "Click")]
        private void btnPoiGo_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(selectedPoi))
                {
                    SetText("lblPoi", "Pick a destination first.");
                    return;
                }

                Fire("/sg-tele " + selectedPoi);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnCoordGo", "Click")]
        private void btnCoordGo_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var coord = GetText("txtCoord");

                if (string.IsNullOrEmpty(coord))
                {
                    SetText("lblPoi", "Enter coordinates like 33.2N,56.5E");
                    return;
                }

                Fire("/tele " + coord);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ Server tab

        [MVControlEvent("btnStorm", "Click")]
        private void btnStorm_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                if (ArmOrConfirm("storm", "lblServer", "Storm this landblock - every other player here goes to their lifestone. Click Portal Storm again to confirm."))
                {
                    Fire("/sg-portalstorm");
                    SetText("lblServer", "Portal storm sent.");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnStormNo", "Click")]
        private void btnStormNo_Click(object sender, MVControlEventArgs e)
        {
            try { Disarm("Cancelled."); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnShutdown", "Click")]
        private void btnShutdown_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var secs = GetText("txtShutInt");

                if (ArmOrConfirm("shutdown", "lblServer", "SHUT DOWN the server in " + secs + "s? Click Shutdown again to confirm."))
                {
                    if (!string.IsNullOrEmpty(secs))
                        Fire("/set-shutdown-interval " + secs);

                    Fire("/shutdown");
                    SetText("lblServer", "Shutdown initiated.");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnShutNo", "Click")]
        private void btnShutNo_Click(object sender, MVControlEventArgs e)
        {
            try { Disarm("Cancelled."); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ Access tab

        [MVControlEvent("btnCapSet", "Click")]
        private void btnCapSet_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var n = GetText("txtCap");

                if (string.IsNullOrEmpty(n))
                {
                    SetText("lblAccess", "Enter a number. -1 = unlimited.");
                    return;
                }

                // The server's own help is emphatic about this: exempt yourself BEFORE capping
                // everyone, or you can lock yourself out of a second session. Worth repeating
                // where the button is, not just in the command's usage text.
                if (ArmOrConfirm("cap", "lblAccess", "Set the GLOBAL cap to " + n + "? Add your own override first. Click Set again to confirm."))
                {
                    Fire("/sg-multibox global " + n);
                    ShowMultibox();
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnMbAdd", "Click")]
        private void btnMbAdd_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var name = GetText("txtMbName");
                var n = GetText("txtMbN");

                if (string.IsNullOrEmpty(name)) { SetText("lblAccess", "Enter a character name or IP."); return; }

                Fire("/sg-multibox " + Bare(name) + " " + (string.IsNullOrEmpty(n) ? "-1" : n));
                ShowMultibox();
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnMbRemove", "Click")]
        private void btnMbRemove_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var name = GetText("txtMbName");
                if (string.IsNullOrEmpty(name)) { SetText("lblAccess", "Select a row, or enter a name or IP."); return; }

                Fire("/sg-multibox remove " + Bare(name));
                ShowMultibox();
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnMbShow", "Click")]
        private void btnMbShow_Click(object sender, MVControlEventArgs e)
        {
            try { ShowMultibox(); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private void ShowMultibox()
        {
            mbRows.Clear();
            BeginCapture(Capture.Multibox);
            Fire("/sg-multibox");
        }

        [MVControlEvent("lstMb", "Selected")]
        private void lstMb_Selected(object sender, MVListSelectEventArgs e)
        {
            try
            {
                if (e.Row < 0 || e.Row >= mbRows.Count)
                    return;

                SetText("txtMbName", mbRows[e.Row][0]);
                SetText("txtMbN", mbRows[e.Row][1] == "unlimited" ? "-1" : mbRows[e.Row][1]);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ Oversight tab

        [MVControlEvent("btnDialHist", "Click")]
        private void btnDialHist_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var dial = GetText("txtDial");
                if (string.IsNullOrEmpty(dial)) { SetText("lblOversight", "Enter a dial name."); return; }

                queriedDial = dial;
                histRows.Clear();
                BeginCapture(Capture.History);
                Fire("/sg-dial-history " + dial);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnRevert", "Click")]
        private void btnRevert_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var dial = GetText("txtDial");
                if (string.IsNullOrEmpty(dial)) { SetText("lblOversight", "Enter a dial name."); return; }

                if (ArmOrConfirm("revert", "lblOversight", "Revert " + dial + " to its previous value? Click Revert again to confirm."))
                {
                    Fire("/sg-revert " + dial);
                    SetText("lblOversight", "Reverted " + dial + ".");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnRevertNo", "Click")]
        private void btnRevertNo_Click(object sender, MVControlEventArgs e)
        {
            try { Disarm("Cancelled."); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ shared

        [MVControlEvent("btnRefresh", "Click")]
        private void btnRefresh_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                Refresh();
                BeginCapture(Capture.Status);
                Fire("/serverstatus");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("lstRoster", "Selected")]
        private void lstRoster_Selected(object sender, MVListSelectEventArgs e)
        {
            try
            {
                if (e.Row < 0 || e.Row >= roster.Count)
                    return;

                target = roster[e.Row];

                SetText("lblTarget", "Target: " + Bare(target));
                SetText("txtMbName", Bare(target));
                SetText("lblPlayers", "Selected " + Bare(target) + ".");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }
    }
}
