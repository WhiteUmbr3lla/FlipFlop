using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// End-to-end test of a running FlipFlop (default settings: double Shift, Smart mode).
// With --auto: auto mode cases instead (FlipFlop must run with "Auto-fix while typing" on).
// With --excluded: checks FlipFlop leaves this app alone (e2e.exe must be in excluded.txt, auto mode on).
// Opens a test window, types into it like a keyboard, taps Shift twice, checks the text and the layout.
// Hands off keyboard and mouse while it runs. Results go to e2e.log next to the exe.
static class E2E
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern short VkKeyScanEx(char c, IntPtr hkl);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    static Form form;
    static TextBoxBase box;
    static InputLanguage en, he;
    static int fails, passes;
    static StreamWriter log;
    static bool autoMode, excludedMode;

    [STAThread]
    static int Main(string[] args)
    {
        autoMode = args.Length > 0 && args[0] == "--auto";
        excludedMode = args.Length > 0 && args[0] == "--excluded";
        log = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "e2e.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        foreach (InputLanguage l in InputLanguage.InstalledInputLanguages)
        {
            if (l.Culture.TwoLetterISOLanguageName == "en" && en == null) en = l;
            if (l.Culture.TwoLetterISOLanguageName == "he" && he == null) he = l;
        }
        form = new Form { Text = "FlipFlop e2e - hands off the keyboard", Width = 700, Height = 160, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        form.Shown += (s, e) => new Thread(Run) { IsBackground = true }.Start();
        Application.Run(form);
        log.WriteLine(fails == 0 ? "all " + passes + " passed" : fails + " failed, " + passes + " passed");
        return fails;
    }

    static void Ui(Action a) { form.Invoke(a); }
    static T Ui<T>(Func<T> f) { return (T)form.Invoke(f); }

    static void Run()
    {
        try
        {
            if (autoMode) { MakeBox(false, false); log.WriteLine("--- auto mode"); AutoCases(); }
            else if (excludedMode)
            {
                MakeBox(false, false);
                log.WriteLine("--- excluded app");
                Start("", en); Type("akuo"); DoubleShift(); Check("hotkey does nothing", "akuo", "en");
                Type(" "); Thread.Sleep(200); Check("auto mode does nothing", "akuo ", "en");
            }
            else
                foreach (bool rich in new[] { false, true })
                {
                    MakeBox(rich, false);
                    log.WriteLine("--- " + (rich ? "RichTextBox (rich edit, like WordPad)" : "TextBox (plain edit, like Notepad)"));
                    Cases();
                }
            if (!autoMode && !excludedMode) WindowCases();
        }
        catch (Exception ex) { log.WriteLine("CRASH " + ex); fails++; }
        Ui(() => form.Close());
    }

    static void MakeBox(bool rich, bool password)
    {
        Ui(() =>
        {
            form.Controls.Clear();
            box = rich ? (TextBoxBase)new RichTextBox() : new TextBox { Multiline = !password, UseSystemPasswordChar = password };
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Segoe UI", 14);
            form.Controls.Add(box);
        });
    }

    static void AutoCases()
    {
        Start("", en); Type("akuo "); Thread.Sleep(200);
        Check("wrong word fixed on Space", "שלום ", "he");
        Type("nv"); Check("keeps typing in Hebrew", "שלום מה", null);

        Start("", en); Type("hello world "); Check("correct English untouched", "hello world ", "en");
        Start("", he); Type("akuo nv "); Check("correct Hebrew untouched", "שלום מה ", "he");
        Start("", en); Type("Szczepanski "); Check("unknown name untouched", "Szczepanski ", "en");

        Start("", en); Type("to ha kl "); Thread.Sleep(200);
        Check("ambiguous words before it fixed with the run", "אם יש לך ", "he");

        Start("", he); Type("hello "); Thread.Sleep(200);
        Check("English on the Hebrew layout", "hello ", "en");

        Start("", en); Type("akuo "); Thread.Sleep(200);
        DoubleShift(); Check("double Shift right after undoes it", "akuo ", "en");
        Type("nv "); Check("then auto stays off for the rest of the run", "akuo nv ", "en");

        MakeBox(false, true);
        Start("", en); Type("akuo "); Thread.Sleep(200);
        Check("never in a password field", "akuo ", "en");
    }

    // Borderless full screen (like most games): FlipFlop must stay out. Maximized: business as usual.
    static void WindowCases()
    {
        MakeBox(false, false);
        log.WriteLine("--- window states");
        Ui(() => { form.FormBorderStyle = FormBorderStyle.None; form.Bounds = Screen.FromControl(form).Bounds; });
        Start("", en); Type("akuo"); DoubleShift(); Check("borderless full screen: hotkey does nothing", "akuo", "en");

        Ui(() => { form.FormBorderStyle = FormBorderStyle.Sizable; form.WindowState = FormWindowState.Maximized; });
        Start("", en); Type("akuo"); DoubleShift(); Check("maximized window: still works", "שלום", "he");

        Ui(() => { form.WindowState = FormWindowState.Normal; form.Size = new Size(700, 160);
            var wa = Screen.FromControl(form).WorkingArea;
            form.Location = new Point(wa.Left + (wa.Width - form.Width) / 2, wa.Top + (wa.Height - form.Height) / 2); });
    }

    static void Cases()
    {
        Start("", en); Type("akuo nv bang");
        Check("typed Hebrew on the English layout", "akuo nv bang", null);
        DoubleShift(); Check("fixed", "שלום מה נשמע", "he");
        DoubleShift(); Check("second press undoes", "akuo nv bang", "en");
        DoubleShift(); Check("third press fixes again", "שלום מה נשמע", "he");
        Type(" vh"); Check("keeps typing in the new layout", "שלום מה נשמע הי", null);

        Start("", he); Type("hello world");
        DoubleShift(); Check("English typed on the Hebrew layout", "hello world", "en");

        Start("", en); Type("hello nv bang");
        DoubleShift(); Check("only the wrong part", "hello מה נשמע", "he");

        Start("", he); Type("akuo how are you");
        DoubleShift(); Check("correct Hebrew, then English on Hebrew layout", "שלום how are you", "en");

        Start("", en); Type("akuox"); Key(0x08); Type(" nv");
        DoubleShift(); Check("backspace while typing", "שלום מה", "he");

        Start("", en); Type("Akuo");
        DoubleShift(); Check("capital letter", "שלום", "he");

        Start("", en); Type("hello khcuch. nv bang");
        DoubleShift(); Check("unknown name joins the run", "hello ליבוביץ מה נשמע", "he");

        Start("", en); Type("to ha kl zni");
        DoubleShift(); Check("ambiguous first word (to = אם)", "אם יש לך זמן", "he");

        Start("", en); Type("sdfsdfsf");
        DoubleShift(); Check("pure gibberish still flips", "דגכדגכדכ", "he");

        Start("", en); Type("akuo"); ClickEnd(); Type(" nv");
        DoubleShift(); Check("mouse click starts a new run", "akuo מה", "he");

        Start("", en); Type("AB"); Key(0xA0); Thread.Sleep(700); Key(0xA0); Thread.Sleep(700);
        Check("slow Shift taps do nothing", "AB", "en");

        Start("akuo nv bang", en); Clip("CLIP-1"); Ui(() => box.SelectAll());
        DoubleShift(1500); Check("selection", "שלום מה נשמע", "he"); CheckClip("CLIP-1");
        DoubleShift(1500); Check("selection undo", "akuo nv bang", "en"); CheckClip("CLIP-1");

        Start("hello akuo world", en); Ui(() => box.Select(6, 4));
        DoubleShift(1500); Check("part of a line selected", "hello שלום world", "he");

        Start("sdfsdfsf", en); Ui(() => box.SelectAll());
        DoubleShift(1500); Check("selected gibberish", "דגכדגכדכ", "he");

        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "e2e.cs");
        Start("akuo", en); Ui(() => { var l = new System.Collections.Specialized.StringCollection(); l.Add(file); Clipboard.SetFileDropList(l); });
        Ui(() => box.SelectAll());
        DoubleShift(1500); Check("selection while a file is copied", "שלום", "he");
        string gotFile = Ui(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList()[0] : "<no file>");
        Report("  copied file restored", gotFile == file, gotFile, file);

        Start("akuo", en); Ui(() => Clipboard.SetImage(new Bitmap(7, 5)));
        Ui(() => box.SelectAll());
        DoubleShift(1500); Check("selection while an image is copied", "שלום", "he");
        string gotImg = Ui(() => { var i = Clipboard.GetImage(); return i == null ? "<no image>" : i.Width + "x" + i.Height; });
        Report("  copied image restored", gotImg == "7x5", gotImg, "7x5");

        Start("123 456", en);
        DoubleShift(1500); Type("x");
        Check("nothing to convert on the line: it isn't left selected", "123 456x", null);

        Start("hello akuo", en); Clip("CLIP-2");
        DoubleShift(1500); Check("nothing typed or selected: current line", "hello שלום", "he"); CheckClip("CLIP-2");
    }

    // Fresh text, caret at the end, layout set; the click also clears FlipFlop's memory.
    static void Start(string text, InputLanguage lang)
    {
        Ui(() =>
        {
            box.Text = text;
            InputLanguage.CurrentInputLanguage = lang;
            form.Activate();
            box.Focus();
        });
        if (GetForegroundWindow() != Ui(() => form.Handle))
        {
            Key(0x12); // Windows only lets a process take focus right after input
            Ui(() => SetForegroundWindow(form.Handle));
        }
        Thread.Sleep(150);
        ClickEnd();
    }

    static void ClickEnd()
    {
        Thread.Sleep(550); // two clicks within 500 ms in one spot would be a double-click (selects a word)
        Ui(() => Cursor.Position = box.PointToScreen(new Point(box.ClientSize.Width - 8, 14)));
        Send(Mouse(2), Mouse(4)); // left down, up
        Thread.Sleep(150);
    }

    static void Clip(string s) { Ui(() => Clipboard.SetText(s)); }

    static void CheckClip(string expected)
    {
        string got = Ui(() => Clipboard.ContainsText() ? Clipboard.GetText() : "<empty>");
        Report("  clipboard restored", got == expected, got, expected);
    }

    // Types `physical` as the keys of a US keyboard, whatever the current layout turns them into.
    static void Type(string physical)
    {
        foreach (char c in physical)
        {
            short r = VkKeyScanEx(c, en.Handle);
            int vk = r & 0xFF;
            bool shift = (r & 0x100) != 0;
            if (shift) Send(KeyIn(0xA0, true));
            Send(KeyIn(vk, true), KeyIn(vk, false));
            if (shift) Send(KeyIn(0xA0, false));
            Thread.Sleep(15);
        }
        Thread.Sleep(100);
    }

    static void Key(int vk) { Send(KeyIn(vk, true), KeyIn(vk, false)); Thread.Sleep(30); }

    static void DoubleShift(int wait = 600)
    {
        Key(0xA0); Thread.Sleep(60); Key(0xA0);
        Thread.Sleep(wait);
    }

    static void Check(string name, string expected, string lang)
    {
        string got = Ui(() => box.Text);
        string gotLang = Ui(() => InputLanguage.CurrentInputLanguage.Culture.TwoLetterISOLanguageName);
        bool ok = got == expected && (lang == null || gotLang == lang);
        Report(name, ok, got + " [" + gotLang + "]", expected + (lang == null ? "" : " [" + lang + "]"));
    }

    static void Report(string name, bool ok, string got, string expected)
    {
        if (ok) passes++; else fails++;
        log.WriteLine((ok ? "ok   " : "FAIL ") + name + ": " + got + (ok ? "" : "   (expected " + expected + ")"));
    }

    static INPUT KeyIn(int vk, bool down)
    {
        return new INPUT { type = 1, u = new U { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = down ? 0u : 2u } } };
    }

    static INPUT Mouse(uint flags) { return new INPUT { type = 0, u = new U { mi = new MOUSEINPUT { dwFlags = flags } } }; }

    static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
}
