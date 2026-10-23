using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FlipFlop")]
[assembly: AssemblyProduct("FlipFlop")]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4")]

static class Program
{
    static readonly string Ver = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
    const string RegKey = @"Software\FlipFlop", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static Mode mode = Mode.Smart;
    static NotifyIcon tray;
    static ToolStripMenuItem autoItem; // tray toggle; Settings goes through it so both stay in sync
    static Icon iconOn, iconOff;
    static Form openForm;               // the one settings/list window that may be open
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipFlop");
    // Installed from the Microsoft Store (MSIX): startup is the manifest's StartupTask, not the Run key.
    static readonly bool Packaged = IsPackaged();
    static readonly string WordsFile = Path.Combine(Dir, "words.txt"), ExcludedFile = Path.Combine(Dir, "excluded.txt");

    [STAThread]
    static int Main(string[] args)
    {
        // DPI-aware from the start, not only once UI Automation (auto mode) loads and silently turns it on, which left
        // the windows' fixed layouts at 100% under 225%-sized text. FitDpi() scales the forms instead.
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string err = Detector.Init();
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest(err);
        if (args.Length > 1 && args[0] == "--write-icon") { WriteIcon(args[1]); return 0; }
        if (args.Length > 3 && args[0] == "--write-png") { WritePng(args[1], int.Parse(args[2]), int.Parse(args[3])); return 0; }
        if (err != null) { MessageBox.Show(err, "FlipFlop"); return 1; }

        bool first;
        var mutex = new Mutex(true, "FlipFlop.SingleInstance", out first);
        if (!first && !(Array.IndexOf(args, "--restarted") >= 0 && TakeOver(mutex))) return 0;

        bool firstRun;
        using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
        {
            mode = (Mode)(int)k.GetValue("Mode", 0);
            Hooks.HotKey = (Keys)(int)k.GetValue("HotKey", 0);
            Hooks.Auto = (int)k.GetValue("Auto", 0) != 0;
            Injector.Logging = (int)k.GetValue("Log", 0) != 0;
            firstRun = (int)k.GetValue("Started", 0) == 0;
            k.SetValue("Started", 1);
        }
        // Starts with Windows by default. If the folder moved since, point the startup entry at this copy.
        if (!Packaged)
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            object run = k == null ? null : k.GetValue("FlipFlop");
            if (firstRun || (run != null && (string)run != RunValue())) SetStartup(true);
        }
        WatchFocus(Hooks.Auto);

        if (File.Exists(WordsFile)) Detector.SetUserWords(File.ReadAllLines(WordsFile));
        if (File.Exists(ExcludedFile)) SetExcluded(File.ReadAllLines(ExcludedFile));

        var ui = new Control();
        IntPtr h = ui.Handle; // create the handle so BeginInvoke works
        Hooks.OnHotkey = () => ui.BeginInvoke((Action)(() => Injector.Run(mode)));
        Hooks.Start();

        var menu = new ContextMenuStrip();
        var enabled = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (s, e) => { Hooks.Enabled = enabled.Checked; tray.Icon = enabled.Checked ? iconOn : iconOff; };
        menu.Items.Add(enabled);
        autoItem = new ToolStripMenuItem("Auto-fix while typing") { Checked = Hooks.Auto, CheckOnClick = true };
        autoItem.CheckedChanged += (s, e) =>
        {
            Hooks.Auto = autoItem.Checked;
            WatchFocus(autoItem.Checked);
            using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("Auto", autoItem.Checked ? 1 : 0);
        };
        menu.Items.Add(autoItem);
        var dontUse = new ToolStripMenuItem { CheckOnClick = true };
        dontUse.Click += (s, e) => ToggleExcluded(Hooks.LastApp, dontUse.Checked);
        menu.Items.Add(dontUse);
        menu.Opening += (s, e) =>
        {
            string app = Hooks.LastApp;
            dontUse.Visible = app != null;
            if (app == null) return;
            dontUse.Text = "Don't use in " + app;
            dontUse.Checked = Hooks.Excluded.Contains(app);
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (s, e) => ShowSettings());
        menu.Items.Add("My words...", null, (s, e) => ShowList("my words",
            "One word per line, spelled the right way (names, slang...).\nFlipFlop treats them as real words in either language.",
            WordsFile, Detector.SetUserWords));
        menu.Items.Add("Excluded apps...", null, (s, e) => ShowList("excluded apps",
            "One app per line, like code.exe. FlipFlop stays out of them.\nFull-screen games are skipped automatically.",
            ExcludedFile, SetExcluded));
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            menu.Items.Add("Restart as admin (to fix admin windows)", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restarted") { Verb = "runas", UseShellExecute = true }); }
                catch { return; } // UAC declined: keep running as we are
                mutex.ReleaseMutex(); // the new one is waiting for it
                Exit();
            });
        menu.Items.Add("Exit", null, (s, e) => Exit());

        iconOn = MakeIcon(true);
        iconOff = MakeIcon(false);
        tray = new NotifyIcon { Icon = iconOn, Text = TrayText(), ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (s, e) => ShowSettings();
        if (firstRun)
            tray.ShowBalloonTip(10000, "FlipFlop is running",
                "Typed in the wrong language? Tap Shift twice.\nFlipFlop starts with Windows; you can change that in Settings.", ToolTipIcon.Info);
        Application.Run();
        GC.KeepAlive(mutex);
        return 0;
    }

    static void Exit() { tray.Visible = false; Application.Exit(); }

    static string TrayText() { return "FlipFlop " + Ver + " - " + HotKeyText(Hooks.HotKey); }

    // "Restart as admin" hands over: the new instance waits for the old one to let go.
    static bool TakeOver(Mutex m)
    {
        try { return m.WaitOne(5000); }
        catch (AbandonedMutexException) { return true; }
    }

    static bool IsPackaged()
    {
        int len = 0;
        try { return Native.GetCurrentPackageFullName(ref len, null) != 15700; } // APPMODEL_ERROR_NO_PACKAGE
        catch (EntryPointNotFoundException) { return false; }
    }

    static string RunValue() { return "\"" + Application.ExecutablePath + "\""; }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("FlipFlop", RunValue());
            else k.DeleteValue("FlipFlop", false);
        }
    }

    static void SetExcluded(IEnumerable<string> apps)
    {
        var set = new HashSet<string>();
        foreach (string a in apps)
        {
            string n = a.Trim().ToLowerInvariant();
            if (n.Length > 0) set.Add(n.EndsWith(".exe") ? n : n + ".exe");
        }
        Hooks.Excluded = set;
        Hooks.Recheck();
    }

    static void ToggleExcluded(string app, bool on)
    {
        var set = new HashSet<string>(Hooks.Excluded);
        if (on) set.Add(app); else set.Remove(app);
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(ExcludedFile, set);
        SetExcluded(set);
    }

    // Opening a window while one is already open (double-clicking the tray icon again) just brings it forward.
    static bool AlreadyOpen()
    {
        if (openForm == null) return false;
        openForm.Activate();
        return true;
    }

    // A hotkey is swallowed everywhere, so it can't be a key people type with.
    static bool UsableHotkey(Keys k)
    {
        Keys code = k & Keys.KeyCode;
        return (k & (Keys.Control | Keys.Alt)) != 0 || (code >= Keys.F1 && code <= Keys.F24) || code == Keys.Pause || code == Keys.Scroll;
    }

    // Forms are laid out in 96-DPI pixels; fonts already follow the screen, so scale positions and sizes to match.
    static void FitDpi(Form f)
    {
        float k = DpiScale();
        if (k != 1) f.Scale(new SizeF(k, k));
    }

    static float DpiScale() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }

    // Auto mode must never touch a password field: plenty of people use a Hebrew word typed on the English layout as
    // their password. UI Automation reports the focused field's IsPassword, browsers included.
    static AutomationFocusChangedEventHandler focusWatcher;
    static void WatchFocus(bool on)
    {
        if (on == (focusWatcher != null)) return;
        AutomationFocusChangedEventHandler h = focusWatcher;
        if (on)
        {
            focusWatcher = h = (s, e) => Hooks.InPassword = IsPassword(s as AutomationElement);
            Hooks.InPassword = true; // until we know
        }
        else { focusWatcher = null; Hooks.InPassword = false; }
        // UI Automation wants (un)subscribing off the UI thread
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (on) { Automation.AddAutomationFocusChangedEventHandler(h); Hooks.InPassword = IsPassword(AutomationElement.FocusedElement); }
                else Automation.RemoveAutomationFocusChangedEventHandler(h);
            }
            catch (Exception ex) { Injector.Log("focus watcher failed: " + ex.Message); }
        });
    }

    static bool IsPassword(AutomationElement el)
    {
        try { return el != null && el.Current.IsPassword; }
        catch { return true; } // can't tell: play safe
    }

    static Icon MakeIcon(bool on)
    {
        using (var b = DrawLogo(SystemInformation.SmallIconSize.Width, on)) return Icon.FromHandle(b.GetHicon());
    }

    // Two flip-flops; the right one is flipped over (heel up, showing its tread). Drawn in code so every size is crisp.
    public static Bitmap DrawLogo(int size, bool on)
    {
        var bmp = new Bitmap(size, size);
        Color top = on ? Color.FromArgb(255, 183, 3) : Color.Silver;          // sunny yellow
        Color blue = on ? Color.FromArgb(0, 92, 184) : Color.DimGray;          // Israel-flag blue
        Color tread = on ? Color.FromArgb(90, 160, 230) : Color.DarkGray;
        float px = 1f / size;                                                  // one pixel in drawing units
        using (var g = Graphics.FromImage(bmp))
        using (var sole = new GraphicsPath())
        using (var outline = new Pen(Color.FromArgb(170, 0, 0, 0), Math.Max(.02f, px)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size, size);
            sole.AddClosedCurve(new[] {
                new PointF(0, -.47f), new PointF(.14f, -.42f), new PointF(.19f, -.25f), new PointF(.15f, -.04f), new PointF(.12f, .15f),
                new PointF(.14f, .31f), new PointF(.10f, .43f), new PointF(0, .47f), new PointF(-.10f, .43f), new PointF(-.14f, .31f),
                new PointF(-.12f, .15f), new PointF(-.15f, -.04f), new PointF(-.19f, -.25f), new PointF(-.14f, -.42f) }, .5f);

            var state = g.Save(); // upright: yellow footbed, blue V strap
            g.TranslateTransform(.29f, .5f); g.RotateTransform(-12); g.ScaleTransform(.98f, .98f);
            using (var b = new SolidBrush(top)) g.FillPath(b, sole);
            g.DrawPath(outline, sole);
            using (var p = new Pen(blue, Math.Max(.085f, 1.6f * px)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(-.165f, .02f), new PointF(0, -.28f), new PointF(.165f, .02f) });
            g.Restore(state);

            state = g.Save(); // flipped: upside down, blue underside with tread
            g.TranslateTransform(.71f, .5f); g.RotateTransform(192); g.ScaleTransform(.98f, .98f);
            using (var b = new SolidBrush(blue)) g.FillPath(b, sole);
            g.DrawPath(outline, sole);
            if (size >= 32)
                using (var p = new Pen(tread, Math.Max(.025f, px)))
                    for (float y = -.32f; y < .4f; y += .12f) g.DrawLine(p, -.09f, y, .09f, y);
            g.Restore(state);
        }
        return bmp;
    }

    // Store logos (store\make-msix.ps1): the logo centered on a transparent w x h canvas.
    static void WritePng(string path, int w, int h)
    {
        int s = Math.Min(w, h);
        using (var logo = DrawLogo(s, true))
        using (var b = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(b)) g.DrawImage(logo, (w - s) / 2, (h - s) / 2, s, s);
            b.Save(path, ImageFormat.Png);
        }
    }

    // Multi-size .ico with PNG frames, used by build.cmd for the exe icon.
    static void WriteIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 256 };
        var pngs = new List<byte[]>();
        foreach (int sz in sizes)
            using (var b = DrawLogo(sz, true))
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)sizes[i]); w.Write((byte)sizes[i]); w.Write((short)0); // 256 wraps to 0, as the format wants
                w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    // A one-item-per-line list editor: My words, Excluded apps.
    static void ShowList(string title, string hintText, string file, Action<string[]> apply)
    {
        if (AlreadyOpen()) return;
        var f = new Form { Text = "FlipFlop - " + title, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(340, 380),
                           MinimizeBox = false, TopMost = true, Icon = tray.Icon, Font = SystemFonts.MessageBoxFont };
        var hint = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 0), Text = hintText };
        var box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true,
                                Font = new Font("Segoe UI", 11), Text = File.Exists(file) ? File.ReadAllText(file) : "" };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK };
        bar.Controls.Add(cancel); bar.Controls.Add(save);
        f.Controls.Add(box); f.Controls.Add(hint); f.Controls.Add(bar);
        f.CancelButton = cancel;
        FitDpi(f);
        openForm = f;
        if (f.ShowDialog() == DialogResult.OK)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(file, box.Text);
            apply(box.Lines);
        }
        openForm = null;
        f.Dispose();
    }

    static string HotKeyText(Keys k) { return k == Keys.None ? "double Shift" : new KeysConverter().ConvertToString(k); }

    // Settings: a sidebar (Hotkey / Typing / General / About) and one page at a time. Pages are top-down flow panels so
    // they follow the font; fixed sizes go through S() for display scaling.
    static void ShowSettings()
    {
        if (AlreadyOpen()) return;
        bool wasEnabled = Hooks.Enabled; // the tray's Enabled toggle
        Hooks.Enabled = false; // don't fire while capturing a new hotkey
        Keys hot = Hooks.HotKey;
        float dpi = DpiScale();
        Func<int, int> S = px => (int)(px * dpi);
        bool hc = SystemInformation.HighContrast;
        Color sideBg = hc ? SystemColors.Control : Color.FromArgb(243, 243, 243), selBg = hc ? SystemColors.Highlight : Color.FromArgb(229, 229, 229);
        var heading = new Font("Segoe UI Semibold", 14f);
        var bold = new Font("Segoe UI Semibold", 9f);

        var f = new Form { Text = "FlipFlop settings", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                           StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(S(560), S(380)), TopMost = true, Icon = tray.Icon,
                           Font = SystemFonts.MessageBoxFont, BackColor = SystemColors.Window };
        var content = new Panel { Dock = DockStyle.Fill };
        var side = new Panel { Dock = DockStyle.Left, Width = S(160), BackColor = sideBg };
        f.Controls.Add(content); f.Controls.Add(side); // Fill first, so the edges dock around it

        Func<string, FlowLayoutPanel> page = title =>
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false,
                                          Padding = new Padding(S(24), S(18), S(24), 0) };
            p.Controls.Add(new Label { Text = title, Font = heading, AutoSize = true, Margin = new Padding(0, 0, 0, S(14)) });
            return p;
        };
        Func<string, Label> section = text => new Label { Text = text, Font = bold, AutoSize = true, Margin = new Padding(0, S(6), 0, S(6)) };
        Func<string, int, Label> hint = (text, indent) => new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText,
                                          MaximumSize = new Size(S(330) - indent, 0), Margin = new Padding(indent, S(2), 0, S(14)) };
        Func<Control[], FlowLayoutPanel> row = cs =>
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            r.Controls.AddRange(cs);
            return r;
        };
        Func<string, Button> button = text => new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(88), S(28)), UseVisualStyleBackColor = true };

        // Hotkey
        var hotkeyPage = page("Hotkey");
        hotkeyPage.Controls.Add(section("Shortcut"));
        var hotBox = new TextBox { ReadOnly = true, Width = S(170), Text = HotKeyText(hot), BackColor = SystemColors.Window, Margin = new Padding(0, S(3), S(8), 0) };
        string hotTip = "Click the box, then press a key combo.";
        var hotHint = hint(hotTip, 0);
        hotBox.KeyDown += (s, e) =>
        {
            e.SuppressKeyPress = true;
            Keys code = e.KeyCode;
            if (code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return;
            bool usable = UsableHotkey(e.KeyData);
            hotHint.Text = usable ? hotTip : "Use Ctrl/Alt + a key, or an F-key.";
            hotHint.ForeColor = usable ? SystemColors.GrayText : Color.Firebrick;
            if (!usable) return;
            hot = e.KeyData;
            hotBox.Text = HotKeyText(hot);
        };
        var dbl = button("Double Shift");
        dbl.Click += (s, e) => { hot = Keys.None; hotBox.Text = HotKeyText(hot); };
        hotkeyPage.Controls.Add(row(new Control[] { hotBox, dbl }));
        hotkeyPage.Controls.Add(hotHint);
        hotkeyPage.Controls.Add(section("What the hotkey fixes"));
        var modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(300), Margin = new Padding(0, 0, 0, S(14)) };
        modeBox.Items.AddRange(new object[] { "Smart (detect where it went wrong)", "Selected text only", "Last word only", "Everything since last click" });
        modeBox.SelectedIndex = (int)mode;
        hotkeyPage.Controls.Add(modeBox);

        // Typing
        var typingPage = page("Typing");
        var autoBox = new CheckBox { Text = "Auto-fix while typing", AutoSize = true, Checked = Hooks.Auto, Margin = new Padding(0) };
        typingPage.Controls.Add(autoBox);
        typingPage.Controls.Add(hint("Fixes a word as soon as you press Space, when it's clearly in the wrong layout. " +
                                     "Names, typos and words that exist in both languages are left to the hotkey.", S(18)));
        typingPage.Controls.Add(hint("Names or slang it doesn't know? Add them from the tray menu: My words...", 0));

        // General
        var generalPage = page("General");
        var startup = new CheckBox { Text = "Start with Windows", AutoSize = true, Margin = new Padding(0, 0, 0, S(14)) };
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked = k != null && k.GetValue("FlipFlop") != null;
        if (Packaged)
        {
            var l = Link("Start with Windows: set in Windows Settings", "ms-settings:startupapps", Point.Empty);
            l.Margin = new Padding(0, 0, 0, S(14));
            generalPage.Controls.Add(l);
        }
        else generalPage.Controls.Add(startup);
        var logBox = new CheckBox { Text = "Write a troubleshooting log", AutoSize = true, Checked = Injector.Logging, Margin = new Padding(0, S(6), S(12), 0) };
        var clearLog = button("Clear log");
        clearLog.Click += (s, e) => { try { File.Delete(Injector.LogFile); clearLog.Text = "Cleared"; } catch { clearLog.Text = "Failed"; } };
        generalPage.Controls.Add(row(new Control[] { logBox, clearLog }));
        generalPage.Controls.Add(hint("Records which app and which method each fix used, never what you type. Saved in %TEMP%\\FlipFlop.log.", S(18)));

        // About
        var aboutPage = page("About");
        var bigLogo = new PictureBox { Image = DrawLogo(S(48), true), Size = new Size(S(48), S(48)), Margin = new Padding(0, 0, S(12), 0) };
        var name = new Label { Text = "FlipFlop " + Ver + "\nFixes text typed in the wrong keyboard layout (Hebrew ↔ English).", AutoSize = true,
                               MaximumSize = new Size(S(260), 0), Margin = new Padding(0, S(4), 0, S(18)) };
        aboutPage.Controls.Add(row(new Control[] { bigLogo, name }));
        aboutPage.Controls.Add(section("Never saves or sends what you type."));
        var privacy = Link("Privacy policy", "https://github.com/WhiteUmbr3lla/FlipFlop/blob/main/PRIVACY.md", Point.Empty);
        var github = Link("FlipFlop on GitHub", "https://github.com/WhiteUmbr3lla/FlipFlop", Point.Empty);
        privacy.Margin = github.Margin = new Padding(0, S(2), 0, S(6));
        aboutPage.Controls.Add(privacy); aboutPage.Controls.Add(github);
        aboutPage.Controls.Add(hint("Word lists: FrequencyWords by Hermit Dave (CC-BY-SA 4.0).", 0));

        var pages = new[] { hotkeyPage, typingPage, generalPage, aboutPage };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(52), FlowDirection = FlowDirection.RightToLeft, BackColor = sideBg,
                                        Padding = new Padding(S(12), S(12), S(12), 0) };
        var cancel = button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        var ok = button("OK"); ok.DialogResult = DialogResult.OK;
        bar.Controls.Add(cancel); bar.Controls.Add(ok);
        foreach (var p in pages) content.Controls.Add(p);
        content.Controls.Add(bar);
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.ActiveControl = ok; // not the hotkey box: stray typing as it opens would change the hotkey

        // Sidebar: logo, page buttons with an accent bar on the current one, version at the bottom.
        side.Controls.Add(new PictureBox { Image = DrawLogo(S(28), true), Bounds = new Rectangle(S(16), S(16), S(28), S(28)) });
        side.Controls.Add(new Label { Text = "FlipFlop", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(S(50), S(19)) });
        side.Controls.Add(new Label { Text = "Version " + Ver, ForeColor = SystemColors.GrayText, Dock = DockStyle.Bottom, Height = S(36),
                                      Padding = new Padding(S(14), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft });
        var accent = new Panel { BackColor = SystemColors.Highlight, Size = new Size(S(3), S(18)) };
        side.Controls.Add(accent);
        var navs = new Button[pages.Length];
        Action<int> show = i =>
        {
            for (int j = 0; j < pages.Length; j++)
            {
                pages[j].Visible = j == i;
                navs[j].BackColor = j == i ? selBg : sideBg;
                navs[j].ForeColor = j == i && hc ? SystemColors.HighlightText : SystemColors.ControlText;
            }
            accent.Location = new Point(navs[i].Left + S(2), navs[i].Top + (navs[i].Height - accent.Height) / 2);
        };
        string[] titles = { "Hotkey", "Typing", "General", "About" };
        for (int i = 0; i < navs.Length; i++)
        {
            int n = i;
            var b = navs[i] = new Button { Text = titles[i], FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand,
                                           Bounds = new Rectangle(S(6), S(64) + i * S(38), S(148), S(36)), Padding = new Padding(S(12), 0, 0, 0) };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hc ? SystemColors.Highlight : Color.FromArgb(234, 234, 234);
            b.Click += (s, e) => show(n);
            side.Controls.Add(b);
        }
        accent.BringToFront();
        show(0);

        openForm = f;
        if (f.ShowDialog() == DialogResult.OK)
        {
            mode = (Mode)modeBox.SelectedIndex;
            autoItem.Checked = autoBox.Checked; // applies and saves it, same as the tray toggle
            Hooks.HotKey = hot;
            Injector.Logging = logBox.Checked;
            using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) { k.SetValue("Mode", (int)mode); k.SetValue("HotKey", (int)hot); k.SetValue("Log", logBox.Checked ? 1 : 0); }
            if (!Packaged) SetStartup(startup.Checked);
            tray.Text = TrayText();
        }
        openForm = null;
        f.Dispose();
        heading.Dispose(); bold.Dispose();
        Hooks.Enabled = wasEnabled;
    }

    static LinkLabel Link(string text, string target, Point at)
    {
        var l = new LinkLabel { Text = text, Location = at, AutoSize = true };
        l.LinkClicked += (s, e) => { try { Process.Start(target); } catch { } };
        return l;
    }

    static int SelfTest(string err)
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        if (err != null) { Console.WriteLine(err); return 1; }
        int fails = 0;
        Action<string, Mode, string> check = (input, m, expected) =>
        {
            string conv; bool toHe;
            int start = Detector.Fix(input, m, out conv, out toHe);
            string got = input.Substring(0, start) + conv;
            bool ok = got == expected;
            if (!ok) fails++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + input + "  ->  " + got + (ok ? "" : "   (expected " + expected + ")"));
        };
        check("akuo", Mode.Smart, "שלום");
        check("יקךךם", Mode.Smart, "hello");
        check("akuo nv bang", Mode.Smart, "שלום מה נשמע");
        check("שלום מה נשמע akuo", Mode.Smart, "שלום מה נשמע שלום");
        check("hello vk", Mode.Smart, "hello הל");
        check("יקךךם 'םרךג", Mode.Smart, "hello world");
        check("I said tbh rumv", Mode.Smart, "I said אני רוצה");
        check("akuo,", Mode.Smart, "שלוםת");
        check("hello world", Mode.Smart, "hello 'םרךג"); // nothing wrong: flips the last word
        check("ani akuo", Mode.LastWord, "ani שלום");
        check("akuo nv", Mode.Whole, "שלום מה");
        check("hello khcuch. nv bang", Mode.Smart, "hello ליבוביץ מה נשמע");      // unknown name joins the wrong run
        check("Szczepanski nv bang", Mode.Smart, "דזבזקפשמדלן מה נשמע");         // ...even when it shouldn't,
        Detector.SetUserWords(new[] { "Szczepanski" });
        check("Szczepanski nv bang", Mode.Smart, "Szczepanski מה נשמע");         // ...until it's in "my words"
        Detector.SetUserWords(new string[0]);

        // Real-life sentences typed entirely in the wrong layout, and after a correct start.
        string[] he = { "מה קורה אחי", "אני בדרך הביתה", "תודה רבה על העזרה", "אפשר לדבר מחר בבוקר", "איפה אתה עכשיו",
                        "בוא נדבר אחרי הישיבה", "שלחתי לך את הקובץ", "אני לא בטוח שזה עובד", "יש לך דקה", "נתראה בערב",
                        "מה השעה", "תתקשר אליי כשאתה יכול", "הפגישה נדחתה למחר", "כן בדיוק", "אין בעיה", "מה נשמע?", "בסדר, תודה.",
                        "גם אני רוצה לבוא", "אם יש לך זמן", "אני הולך עם חברים", "בוא נלך", "עוד לא יודע", "רק רגע" };
        string[] en = { "what are you doing tonight", "i will call you later", "can you send me the file", "thanks for the help",
                        "see you tomorrow morning", "the meeting is at five", "where are you now", "this is not working",
                        "let me check and get back to you", "good morning everyone", "how was your weekend", "ok, sounds good.",
                        "so what do you think", "no problem at all", "go to the store", "i have to do it" };
        check("i want to " + Detector.Convert("מה נשמע", true), Mode.Smart, "i want to מה נשמע"); // "to" stays English
        check("שלום " + Detector.Convert("go to the store", false), Mode.Smart, "שלום go to the store");
        foreach (string s in he) check(Detector.Convert(s, true), Mode.Smart, s);
        foreach (string s in en) check(Detector.Convert(s, false), Mode.Smart, s);
        // After real English words, a run starting with אם/גם ("to"/"do") is a genuine toss-up: "i want to" + Hebrew
        // is just as plausible. Those stay English; select the text to force them.
        foreach (string s in he)
            if (!s.StartsWith("אם ") && !s.StartsWith("גם ")) check("ok so " + Detector.Convert(s, true), Mode.Smart, "ok so " + s);
        foreach (string s in en) check("אז ככה " + Detector.Convert(s, false), Mode.Smart, "אז ככה " + s);

        // Auto mode. Single words first: fire only when clearly wrong.
        Action<string, bool> auto = (w, expected) =>
        {
            bool ok = Detector.AutoCandidate(w) == expected;
            if (!ok) fails++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + "auto " + (expected ? "fires" : "quiet") + ": " + w);
        };
        foreach (string w in new[] { "akuo", "kl", "יקךךם", "Akuo" }) auto(w, true);
        foreach (string w in new[] { "to", "ha", "cut", "do", "hello", "שלום", "שלוםם", "Szczepanski", "a", "123" }) auto(w, false);

        // Then whole sentences, typed word by word like a person would: in the wrong layout until auto mode fires,
        // in the right one after (the layout switched). Must end up correct, and must never fire on correct typing.
        Func<string, string, bool, bool, string> sim = (prefix, intended, isHe, startWrong) =>
        {
            string buf = prefix;
            bool wrong = startWrong;
            foreach (string w in intended.Split(' '))
            {
                buf += (wrong ? Detector.Convert(w, isHe) : w) + " ";
                string typed = buf.Substring(0, buf.Length - 1);
                if (!Detector.AutoCandidate(typed.Substring(typed.LastIndexOf(' ') + 1))) continue;
                string conv; bool toHe;
                int st = Detector.Fix(typed, Mode.Smart, out conv, out toHe);
                buf = typed.Substring(0, st) + conv + " ";
                wrong = false;
            }
            return buf.TrimEnd();
        };
        Action<string, string, string> expect = (name, got, expected) =>
        {
            bool ok = got == expected;
            if (!ok) fails++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + name + ": " + got + (ok ? "" : "   (expected " + expected + ")"));
        };
        foreach (string s in he) expect("auto fixes", sim("", s, true, true), s);
        foreach (string s in en) expect("auto fixes", sim("", s, false, true), s);
        foreach (string s in he) expect("auto quiet", sim("", s, true, false), s);
        foreach (string s in en) expect("auto quiet", sim("", s, false, false), s);
        foreach (string s in he)
            if (!s.StartsWith("אם ") && !s.StartsWith("גם ")) expect("auto after English", sim("ok so ", s, true, true), "ok so " + s);
        foreach (string s in en) expect("auto after Hebrew", sim("אז ככה ", s, false, true), "אז ככה " + s);
        // Hotkeys that would swallow a typing key everywhere are refused.
        foreach (Keys k in new[] { Keys.A, Keys.Shift | Keys.A, Keys.D5, Keys.Space, Keys.Escape })
            expect("hotkey refused", UsableHotkey(k) ? "allowed" : "refused", "refused");
        foreach (Keys k in new[] { Keys.Control | Keys.A, Keys.Alt | Keys.Q, Keys.F9, Keys.Pause })
            expect("hotkey allowed", UsableHotkey(k) ? "allowed" : "refused", "allowed");
        Console.WriteLine(fails == 0 ? "all passed" : fails + " failed");
        return fails;
    }
}
