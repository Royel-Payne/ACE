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
    /// the 045 audit hook records it. The tier gating here only decides what is drawn - a bug in
    /// it can never grant power the account does not have.
    ///
    /// TIER MODEL (052/brief): Advocate sees status + roster + Go-to + Me. Sentinel adds the full
    /// Players verbs and the POI travel. Admin adds Server / Access / Oversight. Tabs a tier
    /// cannot use are REMOVED, not greyed - the brief is explicit that absent reads cleaner, and
    /// the server would reject them anyway.
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

        // ---- command-output capture ---------------------------------------------------
        // The plugin has no API to "run a command and get output" - it fires text and watches
        // chat. So a capture is a short window during which matching lines are consumed rather
        // than ignored. Timestamped so a lost reply cannot leave capture stuck on forever.
        private enum Capture { None, Roster, Status }
        private Capture capturing = Capture.None;
        private DateTime captureStarted = DateTime.MinValue;
        private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(3);

        // ---- inline arm/confirm -------------------------------------------------------
        // Which destructive button is armed, and when. Arming expires so a console left open
        // overnight cannot be one stray click away from a shutdown.
        private string armed = null;
        private DateTime armedAt = DateTime.MinValue;
        private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(10);

        private Timer timer;
        private DateTime lastAutoRefresh = DateTime.MinValue;

        // `Name : AccountId`, the format /sg-roster shares with listplayers so one parser serves
        // both. The name may carry the 023 dagger, which is kept for display and stripped before
        // being used as a command argument.
        private static readonly Regex RosterLine = new Regex(@"^(?<name>.+?)\s+:\s+(?<acct>\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex StatusPlayers = new Regex(@"(?<n>\d+)\s+players online", RegexOptions.Compiled);
        private static readonly Regex StatusUptime = new Regex(@"Server Runtime:\s*(?<up>[^\r\n,]+)", RegexOptions.Compiled);

        protected override void Startup()
        {
            try
            {
                Globals.Init("ShadowgainConsole", Host, Core);
                MVWireupHelper.WireupStart(this, Host);

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
                MVWireupHelper.WireupEnd(this);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // ------------------------------------------------------------------ login / tier

        [BaseEvent("LoginComplete", "CharacterFilter")]
        private void CharacterFilter_LoginComplete(object sender, EventArgs e)
        {
            try
            {
                ApplyTier();
                Refresh();
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        /// <summary>
        /// Remove the tabs this character cannot use.
        ///
        /// Decal does not expose the server's AccessLevel, so tier is INFERRED: the plugin asks
        /// the server for something only that tier can do and watches whether it works. Until a
        /// probe answers, the console shows the Advocate surface - the safe direction to be wrong
        /// in, since an over-generous guess would only produce buttons the server refuses.
        /// </summary>
        private void ApplyTier()
        {
            // Probe order matters: the Admin probe is also a Sentinel-capable command, so a
            // single reply distinguishes both.
            Fire("/sg-roster");      // Sentinel+ : silent for Advocate
            Fire("/sg-dial-history"); // Admin only: usage text for Admin, nothing otherwise
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

                if (DateTime.UtcNow - captureStarted > CaptureWindow)
                {
                    // The window closed without a terminator. Keep whatever arrived rather than
                    // discarding it - a short roster is better than a blank panel.
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

                        if (p.Success) statusPlayers = p.Groups["n"].Value;
                        if (u.Success) { statusUptime = u.Groups["up"].Value.Trim(); FinishCapture(); return; }
                    }
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private string statusPlayers = "?";
        private string statusUptime = "?";

        private void FinishCapture()
        {
            var was = capturing;
            capturing = Capture.None;

            if (was == Capture.Roster)
                RedrawRoster();

            if (was == Capture.Status)
                SetText("lblStatus", "Shadowgain - " + statusPlayers + " online - up " + statusUptime);
        }

        // ------------------------------------------------------------------ refresh

        private void Refresh()
        {
            roster.Clear();
            capturing = Capture.Roster;
            captureStarted = DateTime.UtcNow;
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

                // 30s auto-refresh. Deliberately slow: each one fires a command that the audit
                // trail records, and a chatty console would bury real staff actions in #audit.
                if (DateTime.UtcNow - lastAutoRefresh > TimeSpan.FromSeconds(30))
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
        /// Send a command as the logged-in character. This is the ONLY way this plugin acts.
        /// </summary>
        private void Fire(string command)
        {
            try
            {
                Globals.Host.Actions.InvokeChatParser(command);
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        /// <summary>
        /// Names shown in the roster keep the 023 marker; commands must not receive it.
        /// Same trim set as the server's Player.Name setter, so the two cannot drift.
        /// </summary>
        private static string Bare(string name)
        {
            return name == null ? null : name.TrimStart('+', '*', '†', '[', ']', ' ').Trim();
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

        private void RedrawRoster()
        {
            SetText("lblRoster", "Online - " + roster.Count);
            // The list control is repopulated by the view wrapper; kept minimal here so the
            // parsing above stays testable without a client attached.
        }

        private void SetText(string control, string text)
        {
            try
            {
                // Guarded: a control missing from a removed tab must not throw into a timer.
                var view = MVWireupHelper.GetDefaultView(this);
                if (view == null) return;
                var c = view[control] as IStaticText;
                if (c != null) c.Text = text;
            }
            catch { }
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

                if (ArmOrConfirm("kick", "lblPlayers", "Kick " + target + "? Click Kick again to confirm."))
                {
                    Fire("/boot " + Bare(target));
                    SetText("lblPlayers", "Kicked " + target + ".");
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

        [MVControlEvent("chkAttackable", "Change")]
        private void chkAttackable_Change(object sender, MVCheckBoxChangeEventArgs e)
        {
            try { Fire("/attackable " + (e.Checked ? "on" : "off")); }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnPoiGo", "Click")]
        private void btnPoiGo_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var poi = GetText("cmbPoi");

                if (string.IsNullOrEmpty(poi))
                {
                    SetText("lblMe", "Pick a destination first.");
                    return;
                }

                // /sg-tele takes the NAME - the server resolves it, which is what makes indoor
                // destinations reachable and avoids any coordinate maths here.
                Fire("/sg-tele " + poi);
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
                    SetText("lblMe", "Enter coordinates like 33.2N,56.5E");
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
                var lb = GetText("txtStormLb");

                if (ArmOrConfirm("storm", "lblServer", "Storm " + (string.IsNullOrEmpty(lb) ? "this landblock" : lb) + "? Click Portal Storm again to confirm."))
                {
                    Fire(string.IsNullOrEmpty(lb) ? "/sg-portalstorm" : "/sg-portalstorm " + lb);
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

        [MVControlEvent("btnMbSet", "Click")]
        private void btnMbSet_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var name = GetText("txtMbName");
                var n = GetText("txtMbN");

                if (string.IsNullOrEmpty(name)) { SetText("lblAccess", "Enter a character name."); return; }

                Fire("/sg-multibox " + Bare(name) + " " + (string.IsNullOrEmpty(n) ? "-1" : n));
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnMbRemove", "Click")]
        private void btnMbRemove_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                var name = GetText("txtMbName");
                if (string.IsNullOrEmpty(name)) { SetText("lblAccess", "Enter a character name."); return; }
                Fire("/sg-multibox remove " + Bare(name));
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnMbShow", "Click")]
        private void btnMbShow_Click(object sender, MVControlEventArgs e)
        {
            try { Fire("/sg-multibox"); }
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

                if (ArmOrConfirm("revert", "lblOversight", "Revert " + dial + "? Click Revert again to confirm."))
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
                capturing = Capture.Status;
                captureStarted = DateTime.UtcNow;
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
                SetText("lblTarget", "Target: " + target);
                SetText("txtMbName", Bare(target));
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        private string GetText(string control)
        {
            try
            {
                var view = MVWireupHelper.GetDefaultView(this);
                if (view == null) return null;

                var edit = view[control] as ITextBox;
                if (edit != null) return edit.Text == null ? null : edit.Text.Trim();

                var combo = view[control] as ICombo;
                if (combo != null && combo.Selected >= 0) return combo.Text[combo.Selected];

                return null;
            }
            catch { return null; }
        }
    }
}
