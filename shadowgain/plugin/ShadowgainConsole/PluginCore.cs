using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;
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
    /// TIER GATING: tabs and buttons a rank cannot use are ABSENT, per the brief, not greyed out.
    ///
    /// Two problems had to be solved, and the first was misdiagnosed for a while. VVS genuinely
    /// cannot remove a page after the fact - INotebook exposes only ActiveTab and Change - but the
    /// conclusion drawn from that, that the view could only ever come from the [MVView] resource,
    /// was wrong. ViewSystemSelector already had CreateViewXML, and the wrapper source ships with
    /// this plugin, so a WireupStart overload taking raw XML was a few lines in code we own. The
    /// pages are stripped from the XML string BEFORE the window is built - see TieredXml.
    ///
    /// The second problem is knowing the rank at all: Decal never sees the server's AccessLevel.
    /// The old approach guessed, by firing a command only a tier could run and watching whether it
    /// was refused - fragile in both directions, and it printed usage text into the operator's
    /// chat while hitting the audit trail twice per login. Replaced by asking: /sg-whoami reports
    /// the caller's own level and nothing else.
    ///
    /// None of this is a security boundary. It decides what to DRAW. Every command the console
    /// fires is re-checked server-side, so a wrong answer costs buttons that get refused.
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
        /// <summary>What the logged-in account may actually do. Decided by the server, not guessed.</summary>
        private enum Tier { None, Advocate, Sentinel, Admin }
        private Tier tier = Tier.Advocate;

        // Whether the server actually answered. Distinguishes "you are an Advocate" from "this
        // server has no /sg-whoami", which must not be treated the same - see TieredXml.
        private bool tierKnown = false;

        private enum Capture { None, Roster, Status, Multibox, History, Whoami }
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

        // Deferred startup: see CharacterFilter_LoginComplete for why nothing happens inline.
        private bool pendingInit = false;
        private DateTime loginAt = DateTime.MinValue;
        private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);

        // -1 unknown, 0 off, 1 on. Unknown until the operator clicks, because the server does
        // not volunteer the current value and guessing "off" would make the first click a no-op.
        private int attackable = -1;

        private string statusPlayers = "?";
        private string statusUptime = "?";
        private string queriedDial = "";

        // `Name : AccountId`, the format /sg-roster shares with listplayers so one parser serves
        // both.
        private static readonly Regex RosterLine = new Regex(@"^(?<name>.+?)\s+:\s+(?<acct>\d+)\s*$");
        private static readonly Regex WhoamiLine = new Regex(@"AccessLevel:\s*(?<lvl>\w+)");
        private static readonly Regex StatusPlayers = new Regex(@"(?<n>\d+)\s+players online");
        private static readonly Regex StatusUptime = new Regex(@"Server Runtime:\s*(?<up>[^\r\n,]+)");

        // "  10.0.0.5 = unlimited" / "  10.0.0.5 = 2"
        private static readonly Regex MbOverride = new Regex(@"^(?<who>\S+)\s*=\s*(?<n>\S+)$");
        // "  2026-08-09 01:22  Chris: 5 -> 8"
        private static readonly Regex HistLine = new Regex(@"^(?<when>.+?)\s{2,}(?<who>[^:]+):\s*(?<before>.*?)\s*->\s*(?<after>.*)$");

        protected override void Startup()
        {
            try
            {
                Util.Trace("--- Startup: entered ---");
                Globals.Init("ShadowgainConsole", Host, Core);
                Util.Trace("Startup: Globals.Init done");

                // NOTE: the view is deliberately NOT created here. MVWireupHelper picks a view
                // system by scanning LOADED assemblies for VirindiViewService, so creating the
                // view during Startup is a race against VVS's own load order. Losing that race
                // does not throw - it silently falls back to Decal's view system, and the plugin
                // then runs perfectly while never drawing a window. Created from the timer a
                // few seconds AFTER LoginComplete instead - by which point VVS is certainly
                // loaded and, just as importantly, the client has finished entering the world.
                // See CharacterFilter_LoginComplete.
                // NOTHING ELSE HAPPENS HERE. Not the chat subscription, not the timer.
                //
                // The breadcrumb trace showed the client dying ~2s after this method ran and
                // BEFORE LoginComplete, which is why deferring work off the login callback did
                // not help - the crash was never in that window. In that 2s our managed code did
                // essentially nothing: the timer tick returned immediately with no view, and the
                // chat handler returned immediately with no capture running.
                //
                // So the hooks themselves are now suspects, and the cheapest way to clear them is
                // not to install them until the world is up. If the client still dies here with
                // an empty Startup, the plugin's runtime code is exonerated entirely and the
                // cause is in loading it at all.
                Util.Trace("Startup: done (no hooks installed yet)");
            }
            catch (Exception ex) { Util.Trace("Startup: EXCEPTION " + ex.Message); Util.LogError(ex); }
        }

        protected override void Shutdown()
        {
            try
            {
                pendingInit = false;

                if (timer != null) { timer.Stop(); timer.Tick -= Timer_Tick; timer = null; }
                Util.Trace("--- Shutdown ---");

                try { Core.ChatBoxMessage -= Core_ChatBoxMessage; }
                catch (Exception ex) { Util.LogError(ex); }

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
            // DO NOTHING HERE BUT ARM A TIMER. This handler runs while the client is still
            // entering the world, and every previous version did its real work inline: created
            // the VVS window, called Actions.AddChatText, and fired a command through
            // Actions.InvokeChatParser - all from inside the callback.
            //
            // That crashed the client on world entry. Not a managed exception - the try/catch
            // blocks never saw it and no error log was ever written. Windows recorded
            // 0xc0000005 (access violation) INSIDE acclient.exe, at the same code offset every
            // time. Touching the client's UI and chat parser from a login callback is simply
            // not safe; the well-worn Decal pattern is to defer to a timer and let the world
            // finish loading first.
            try
            {
                Util.Trace("LoginComplete: entered");

                if (timer == null)
                {
                    Core.ChatBoxMessage += Core_ChatBoxMessage;
                    Util.Trace("LoginComplete: chat hook installed");

                    timer = new Timer();
                    timer.Interval = 1000;
                    timer.Tick += Timer_Tick;
                    timer.Start();
                    Util.Trace("LoginComplete: timer started");
                }

                loginAt = DateTime.UtcNow;
                pendingInit = true;
                Util.Trace("LoginComplete: init armed");
            }
            catch (Exception ex) { Util.Trace("LoginComplete: EXCEPTION " + ex.Message); Util.LogError(ex); }
        }

        /// <summary>
        /// The real startup, run from the timer once the world has settled.
        /// </summary>
        private void DeferredInit()
        {
            try
            {
                Util.Trace("DeferredInit begin");

                // Ask the server what tier we are BEFORE building the window. The tabs a rank
                // cannot use have to be absent rather than greyed (the brief is explicit), and
                // VVS has no remove-page call - so the pages must never be in the XML that
                // builds the view. That means the answer is needed first.
                BeginCapture(Capture.Whoami);
                Fire("/sg-whoami");
            }
            catch (Exception ex) { Util.Trace("DeferredInit: EXCEPTION " + ex.Message); Util.LogError(ex); }
        }

        /// <summary>
        /// Build the window, with the pages and buttons this tier cannot use stripped out.
        /// </summary>
        private void BuildView()
        {
            try
            {
                if (!viewCreated)
                {
                    // Report which system was chosen. A silent fallback to DecalInject is the
                    // difference between a working console and an invisible one, so it is worth
                    // saying out loud rather than discovering it from an empty screen.
                    var vvs = ViewSystemSelector.IsPresent(Host, ViewSystemSelector.eViewSystem.VirindiViewService);

                    Util.Trace("  WireupStart (vvs=" + vvs + ", tier=" + tier + ")");
                    MVWireupHelper.WireupStart(this, Host, TieredXml());
                    viewCreated = true;
                    Util.Trace("  view created");

                    ApplyIcon();

                    Util.WriteToChat(vvs
                        ? "console ready (Virindi views)."
                        : "VirindiViewService not detected - the window will not appear. Is VVS enabled?");

                    Util.Trace("  chat greeting sent");

                    LoadPoi();
                    Util.Trace("  poi loaded");

                    // The POI list is gone at Advocate, so its hint line ("Pick a destination.")
                    // would be describing a control that is no longer on the tab. Repurpose it
                    // for the one travel method the tier does have.
                    if (tier == Tier.Advocate)
                        SetText("lblPoi", "Coordinates look like 33.2N,56.5E");
                }

                if (tier == Tier.None)
                {
                    // Fires nothing. serverstatus and sg-roster are both Advocate-tier, so at
                    // Player they would only generate refusals - and a refusal is still a command
                    // the server processed and, for anything above the read-only filter, audited.
                    SetText("lblStatus", "Nothing to see here.");
                    Util.Trace("view ready (Player - blank surface, no polling)");
                    return;
                }

                // serverstatus is Advocate-tier, so every tier that can open this console can
                // fill its own status strip. Without this it read "status pending" until someone
                // happened to click Refresh.
                pendingStatus = true;
                Refresh();
                Util.Trace("view ready");
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        // Held for the window's lifetime. A Bitmap built over a MemoryStream keeps referencing
        // that stream, and GDI+ will fault later if either is collected while the icon is still
        // on screen - so both are fields, not locals.
        private System.IO.MemoryStream iconStream;
        private System.Drawing.Bitmap iconBitmap;

        /// <summary>
        /// Put our own art in the title bar instead of a DAT icon id.
        ///
        /// The view XML still declares icon="8129" as a fallback, and 8129 is also what Virindi
        /// Item Tool wears - which is exactly why this exists. Two unrelated windows were
        /// indistinguishable on the VVS bar. VVS never required a DAT icon: ACImage takes a
        /// Bitmap, only this wrapper's IView was missing the overload.
        /// </summary>
        private void ApplyIcon()
        {
            try
            {
                var view = MVWireupHelper.GetDefaultView(this);

                if (view == null)
                    return;

                var asm = System.Reflection.Assembly.GetExecutingAssembly();

                using (var stream = asm.GetManifestResourceStream("ShadowgainConsole.icon.png"))
                {
                    if (stream == null)
                    {
                        Util.Trace("icon.png is not embedded - keeping the DAT icon");
                        return;
                    }

                    var buffer = new byte[stream.Length];
                    var read = 0;

                    while (read < buffer.Length)
                    {
                        var n = stream.Read(buffer, read, buffer.Length - read);

                        if (n <= 0)
                            break;

                        read += n;
                    }

                    iconStream = new System.IO.MemoryStream(buffer);
                    iconBitmap = new System.Drawing.Bitmap(iconStream);
                }

                view.SetIcon(iconBitmap);
                Util.Trace("  icon applied");
            }
            catch (Exception ex)
            {
                // A missing or bad icon is cosmetic. Never let it stop the console loading.
                Util.Trace("icon failed: " + ex.Message);
                Util.LogError(ex);
            }
        }

        /// <summary>Map the server's AccessLevel name onto the three surfaces the console draws.</summary>
        private static Tier ToTier(string accessLevel)
        {
            if (string.IsNullOrEmpty(accessLevel))
                return Tier.Advocate;

            switch (accessLevel.Trim().ToLowerInvariant())
            {
                case "admin":
                case "developer":
                    return Tier.Admin;

                case "envoy":
                case "sentinel":
                    return Tier.Sentinel;

                case "advocate":
                    return Tier.Advocate;

                // Player, and anything unrecognised, gets NOTHING - see TieredXml. A rank with no
                // staff powers has no business being shown staff plumbing, and an unfamiliar level
                // name is safer treated as "no privilege" than guessed upward.
                default:
                    return Tier.None;
            }
        }

        /// <summary>
        /// The view XML with everything this tier cannot use removed, or null to use the embedded
        /// resource unchanged.
        ///
        /// This is what makes "absent rather than greyed" possible. VVS cannot remove a page after
        /// the fact - INotebook exposes only ActiveTab and Change - so a page a tier must not see
        /// has to be missing from the XML the window is built from in the first place.
        ///
        /// Admin returns null deliberately: null means "load the embedded resource", which is the
        /// original, already-proven path. The tier that sees everything does not need to exercise
        /// the XML rewriting at all, so the most-used case carries the least new risk. A parse
        /// failure returns null too - a full console is a much better failure than no console.
        /// </summary>
        private string TieredXml()
        {
            // No answer means the server predates /sg-whoami, NOT that the operator is a
            // low tier. Degrading to the smallest surface on a timeout would hand an Admin a
            // crippled console purely because of deploy ordering - the plugin ships to the
            // client independently of the server. Unknown therefore falls back to the previous
            // behaviour: draw everything, and let the server refuse what the rank lacks.
            if (!tierKnown || tier == Tier.Admin)
                return null;

            try
            {
                string xml;
                var asm = System.Reflection.Assembly.GetExecutingAssembly();

                using (var stream = asm.GetManifestResourceStream("ShadowgainConsole.mainView.xml"))
                {
                    if (stream == null)
                        return null;

                    using (var reader = new System.IO.StreamReader(stream))
                        xml = reader.ReadToEnd();
                }

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                // Player 0: strip the lot. No tabs, no roster, no echo, no Refresh - the window
                // opens empty and fires no commands at all.
                //
                // Not merely cosmetic. Every control left in place is one the operator can click,
                // and every click is a command the server then has to refuse. Drawing a surface
                // nobody can drive produced exactly that at Advocate (064) - a tab whose only verb
                // depended on a roster the rank could not populate.
                if (tier == Tier.None)
                {
                    RemoveNodes(doc, "control", "name", new string[]
                    {
                        "nbMain", "lstRoster", "lblRoster", "lblEcho", "btnRefresh"
                    });

                    return doc.OuterXml;
                }

                // Server / Access / Oversight are Admin-only.
                RemoveNodes(doc, "page", "label", new string[] { "Server", "Access", "Oversight" });

                // ADVOCATE: drawn from what the tier can actually DO, not from the mock.
                //
                // Checked every command the console fires against its required level. An Advocate
                // can run `attackable`, `tele` (coords), `sg-roster` and `serverstatus` - and
                // nothing else. `teleto`, `telereturn` and `sg-tele` are all Sentinel, so Go to,
                // Return me and POI travel would every one of them be drawn and then refused.
                //
                // 052's mock had Advocate keeping "Go to". That was written before anyone checked
                // teleto's tier. Following it would ship a Players tab whose only verb does not
                // work, which is precisely the fault 064 fixed for the roster - so the Players
                // page goes entirely, and what is left is honest: see who is online, toggle
                // whether monsters attack you, and travel by coordinates.
                if (tier == Tier.Advocate)
                {
                    RemoveNodes(doc, "page", "label", new string[] { "Players" });

                    RemoveNodes(doc, "control", "name", new string[]
                    {
                        // POI travel is /sg-tele - Sentinel.
                        "lblPoiHead", "txtPoiFilter", "lstPoi", "btnPoiGo",
                        // telereturn is Sentinel, on both tabs.
                        "btnReturn", "btnReturnMe"
                    });

                    // Close the hole the POI list left, so the tab does not read as broken.
                    SetTop(doc, "txtCoord", 34);
                    SetTop(doc, "btnCoordGo", 33);
                    SetTop(doc, "lblPoi", 62);
                }

                return doc.OuterXml;
            }
            catch (Exception ex)
            {
                Util.Trace("TieredXml failed, using the full view: " + ex.Message);
                Util.LogError(ex);
                return null;
            }
        }

        /// <summary>
        /// Move a control vertically in the XML before the window is built.
        ///
        /// Removing a control leaves its space behind - VVS has no layout engine, every position
        /// is absolute. Stripping the POI list out of the Me tab left the coordinate box stranded
        /// 200px down the page with nothing above it, which reads as a broken window rather than
        /// a smaller one.
        /// </summary>
        private static void SetTop(XmlDocument doc, string control, int top)
        {
            foreach (XmlNode node in doc.GetElementsByTagName("control"))
            {
                var name = node.Attributes == null ? null : node.Attributes["name"];

                if (name != null && name.Value == control && node is XmlElement element)
                {
                    element.SetAttribute("top", top.ToString());
                    return;
                }
            }
        }

        private static void RemoveNodes(XmlDocument doc, string element, string attribute, string[] values)
        {
            var doomed = new List<XmlNode>();

            foreach (XmlNode node in doc.GetElementsByTagName(element))
            {
                var attr = node.Attributes == null ? null : node.Attributes[attribute];

                if (attr == null)
                    continue;

                foreach (var value in values)
                {
                    // Trimmed: page labels are padded with spaces so MetaView sizes the tab
                    // sensibly, so "  Server  " has to match "Server".
                    if (string.Equals(attr.Value.Trim(), value, StringComparison.OrdinalIgnoreCase))
                    {
                        doomed.Add(node);
                        break;
                    }
                }
            }

            // Collected first, removed second. GetElementsByTagName returns a LIVE NodeList, and
            // removing while walking it skips whatever shifts into the vacated slot.
            foreach (var node in doomed)
            {
                if (node.ParentNode != null)
                    node.ParentNode.RemoveChild(node);
            }
        }

        // ------------------------------------------------------------------ chat capture

        private void Core_ChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                // ORDER MATTERS. `capturing` is a managed field and costs nothing to read;
                // e.Text marshals a string out of the client's memory. Reading it first meant
                // this plugin reached into native memory on EVERY chat line the client produced,
                // including the burst at world entry, purely to discard the result - the console
                // is idle almost all the time. Cheap check first.
                if (capturing == Capture.None)
                    return;

                if (e == null || e.Text == null)
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
                        else if (LooksLikeCharacterName(line))
                            roster.Add(line);
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
                    else if (capturing == Capture.Whoami)
                    {
                        var m = WhoamiLine.Match(line);

                        if (m.Success)
                        {
                            tier = ToTier(m.Groups["lvl"].Value);
                            tierKnown = true;
                            Util.Trace("whoami: " + m.Groups["lvl"].Value + " -> " + tier);
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

            if (was == Capture.Whoami)
            {
                // Reached either on the reply or on the 3s timeout. A timeout leaves tierKnown
                // false, which TieredXml treats as "draw everything" - the server still refuses
                // anything the rank lacks, so the worst case is buttons that do not work rather
                // than a console missing tabs the operator has earned.
                BuildView();
                return;
            }

            if (was == Capture.Roster)
            {
                RedrawRoster();

                if (pendingStatus)
                {
                    pendingStatus = false;
                    BeginCapture(Capture.Status);
                    Fire("/serverstatus");
                }

                return;
            }

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

        // Set when a status poll should follow the roster, so the two captures take turns
        // instead of racing.
        private bool pendingStatus = false;

        // Whether a destructive command has actually been SENT, as opposed to merely armed.
        // Cancel means two completely different things either side of that line. Conflating them
        // is what made the shutdown Cancel print "Cancelled." while the server shut down anyway -
        // that button is gone now (075), but the distinction still governs Portal Storm.
        private bool stormFired = false;

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
                // Deferred startup. SettleTime is generous on purpose: this runs once per
                // login, the console is not urgent, and the failure it avoids closes the
                // client outright.
                if (pendingInit)
                {
                    if (DateTime.UtcNow - loginAt < SettleTime)
                        return;

                    pendingInit = false;
                    DeferredInit();
                    return;
                }

                // Capture expiry is checked BEFORE the view guard on purpose: the whoami reply
                // is itself a capture, and it is what builds the view. Guarding first would mean
                // a server that never answers leaves the console permanently blank.
                if (capturing != Capture.None && DateTime.UtcNow - captureStarted > CaptureWindow)
                    FinishCapture();

                // Nothing below may run before the window exists - every path here reaches
                // either the client's chat parser or a VVS control.
                if (!viewCreated)
                    return;

                // Expire an arm the operator walked away from.
                if (armed != null && DateTime.UtcNow - armedAt > ArmWindow)
                    Disarm("Confirmation expired.");

                // Auto-refresh ONLY while the window is open, and slowly.
                //
                // The first version polled every 30s regardless, which flooded #audit with
                // sg-roster - the exact thing a comment two files away warns against. Two fixes:
                // sg-roster is now in the audit's read-only filter server-side, and the poll no
                // longer runs at all for a console nobody is looking at.
                if (!ViewIsOpen())
                    return;

                if (tier == Tier.None)
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
                Util.Trace("fire: " + command);
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

        /// <summary>
        /// Is this bare line plausibly a character name?
        ///
        /// Below Sentinel /sg-roster omits the account id, so a roster line is just the name and
        /// the strict "Name : id" pattern stops matching. Accepting whatever is left over would be
        /// worse than useless: the capture window is three seconds of ALL chat, so anything
        /// arriving mid-window would become a roster row - and then a teleport target the operator
        /// could click. This is the guard on that.
        ///
        /// Deliberately conservative. A rejected real name costs one missing row until the next
        /// refresh; an accepted piece of chat puts a fictional player in a moderator's list.
        /// </summary>
        private static bool LooksLikeCharacterName(string line)
        {
            if (line.Length < 2 || line.Length > 34)
                return false;

            // The account-id form is handled by the strict pattern; a colon here means this is
            // some other message entirely.
            if (line.IndexOf(':') >= 0)
                return false;

            var letters = 0;

            foreach (var c in line)
            {
                if (char.IsLetter(c)) { letters++; continue; }

                if (c == ' ' || c == '\'' || c == '-' || char.IsDigit(c))
                    continue;

                // Leading decoration - the progression marker, an admin '+' - arrives before any
                // letter and is legitimate. The same character AFTER the name has begun is
                // punctuation, which means this is a sentence, not a name.
                if (letters == 0)
                    continue;

                return false;
            }

            return letters >= 2;
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

                Util.Trace("dial-history: parsed " + histRows.Count + " row(s)");

                SetText("lblOversight", histRows.Count > 0
                    ? histRows.Count + " change(s). Revert undoes the newest."
                    : "No changes parsed for '" + queriedDial + "'.");
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

        [MVControlEvent("btnReturnMe", "Click")]
        private void btnReturnMe_Click(object sender, MVControlEventArgs e)
        {
            // The Players tab copy. Same action as the Me tab's - the moment you want it is
            // right after Go to, and hopping tabs to reach it is the nit Chris raised.
            btnReturn_Click(sender, e);
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
                    stormFired = true;
                    SetText("lblServer", "Portal storm sent - it cannot be recalled.");
                }
            }
            catch (Exception ex) { Util.LogError(ex); }
        }

        [MVControlEvent("btnStormNo", "Click")]
        private void btnStormNo_Click(object sender, MVControlEventArgs e)
        {
            try
            {
                // A storm in flight genuinely cannot be recalled - it is an ActionChain already
                // queued on the server, and there is no command to stop it. So say that, rather
                // than printing "Cancelled." and letting the operator believe otherwise.
                if (stormFired)
                {
                    stormFired = false;
                    armed = null;
                    SetText("lblServer", "Too late - the storm is already running.");
                    return;
                }

                Disarm("Cancelled - no storm had been sent.");
            }
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

                // Traced because the first in-client attempt produced an empty list and there was
                // no way to tell WHICH step failed - an empty box, a query that never went out, or
                // a reply the parser dropped. The regex was verified against the server's real
                // output and matches, so the next failure needs to name its own cause.
                Util.Trace("dial-history: box=[" + (dial ?? "<null>") + "]");

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
                // Chained, NOT fired together. Both replies are asynchronous, so setting the
                // status capture here used to overwrite the roster capture before the roster
                // reply had arrived - the roster lines were then fed to the status parser and
                // discarded, and the panel never refreshed. The bug hid because the startup
                // path calls Refresh() on its own.
                pendingStatus = true;
                Refresh();
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
