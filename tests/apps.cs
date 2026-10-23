using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

// Real-app test of a running FlipFlop (double Shift, Smart mode, "Auto-fix while typing" OFF): Notepad, Word, Excel
// and a Chrome text box. Each app gets two cases:
//   A: typed on the English layout like a person, then double Shift (FlipFlop's typed path);
//   B: text already in the line (entered as Unicode, which FlipFlop doesn't record), then double Shift (line path).
// The stored text must be exactly the Hebrew sentence and the keyboard must end on Hebrew. Word also saves
// word-A.png / word-B.png: the stored letters can be right while Word displays them wrong, so look at those too.
// It opens its own windows and types only while its own window is in front; anything else stops it before typing.
// Notepad must not be open already (its tabs are left alone). Word, Excel and Chrome are skipped if not installed.
// Hands off keyboard and mouse while it runs (about 2 minutes). Results go to apps.log next to the exe.
//   csc /codepage:65001 /out:tests\apps.exe /r:Microsoft.CSharp.dll /r:System.Drawing.dll
//       /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll
//       /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll
//       /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll tests\apps.cs
//   tests\apps.exe [notepad] [word] [excel] [chrome]      (no arguments: all four)
static class Apps
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, [Out] IntPtr[] list);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cb, flags; public IntPtr active, focus, capture, menu, move, caret; public RECT rc; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }

    const string Keys = "akuo nv bang tbh rumv kxpr kl navu";
    const string Want = "שלום מה נשמע אני רוצה לספר לך משהו";
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static StreamWriter log;
    static int fails, passes;
    static IntPtr target; // the only window the test may type into

    static void Say(string s) { Console.WriteLine(s); log.WriteLine(s); }

    static INPUT VK(ushort vk, bool up) { return new INPUT { type = 1, u = new U { ki = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = up ? 2u : 0u } } }; }
    static INPUT Uni(char c, bool up) { return new INPUT { type = 1, u = new U { ki = new KEYBDINPUT { scan = c, flags = 4u | (up ? 2u : 0u) } } }; }

    static void Send(params INPUT[] i)
    {
        if (target == IntPtr.Zero || GetForegroundWindow() != target) throw new Exception("test window not in front, stopped before typing");
        SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(INPUT)));
    }
    static void Tap(ushort vk) { Send(VK(vk, false), VK(vk, true)); }
    static void ClearAll() { Send(VK(0x11, false), VK(0x41, false), VK(0x41, true), VK(0x11, true)); Tap(0x2E); Thread.Sleep(400); }

    static void TypeKeys(string s) { foreach (char c in s) { Tap((ushort)(c == ' ' ? 0x20 : char.ToUpper(c))); Thread.Sleep(30); } }
    static void TypeUnicode(string s) { foreach (char c in s) { Send(Uni(c, false), Uni(c, true)); Thread.Sleep(25); } }
    static void DoubleShift() { Thread.Sleep(300); Tap(0xA0); Thread.Sleep(90); Tap(0xA0); Thread.Sleep(2500); }

    static int Lang()
    {
        uint pid;
        return (int)((long)GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out pid)) & 0x3FF);
    }

    // Like FlipFlop's SwitchLayout: ask the focused control for the English layout.
    static void English()
    {
        IntPtr fg = GetForegroundWindow();
        uint pid;
        var info = new GUITHREADINFO { cb = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        IntPtr focus = GetGUIThreadInfo(GetWindowThreadProcessId(fg, out pid), ref info) && info.focus != IntPtr.Zero ? info.focus : fg;
        var list = new IntPtr[32];
        int n = GetKeyboardLayoutList(32, list);
        PostMessage(focus, 0x50, IntPtr.Zero, list.Take(n).FirstOrDefault(k => ((long)k & 0x3FF) == 0x09));
        Thread.Sleep(400);
    }

    static void Fore(IntPtr h)
    {
        target = h;
        for (int i = 0; i < 5 && GetForegroundWindow() != h; i++)
        {
            uint pid;
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
            AttachThreadInput(me, fg, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            AttachThreadInput(me, fg, false);
            Thread.Sleep(300);
        }
        Thread.Sleep(500);
        if (GetForegroundWindow() != h) throw new Exception("could not bring the test window to the front; nothing typed");
    }

    static void Check(string app, string cs, string got, bool knownLimit = false)
    {
        got = (got ?? "(null)").Trim('\r', '\n', ' ', '\a');
        bool ok = got == Want && Lang() == 0x0D;
        string what = app.PadRight(8) + cs + (ok ? "" : ": got \"" + got + "\"" + (Lang() == 0x0D ? "" : " [keyboard not on Hebrew]"));
        if (ok) { passes++; Say("ok    " + what); }
        else if (knownLimit) Say("known " + what + "   (known limit, see README)");
        else { fails++; Say("FAIL  " + what); }
    }

    static IntPtr FindWindow(Func<string, string, bool> match)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(128); GetClassName(h, cls, 128);
            var title = new StringBuilder(512); GetWindowText(h, title, 512);
            if (match(cls.ToString(), title.ToString())) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr WaitWindow(Func<string, string, bool> match)
    {
        for (int i = 0; i < 150; i++) { IntPtr h = FindWindow(match); if (h != IntPtr.Zero) return h; Thread.Sleep(100); }
        return IntPtr.Zero;
    }

    static void Shot(IntPtr h, string name)
    {
        RECT r; GetWindowRect(h, out r);
        using (var b = new Bitmap(r.R - r.L, r.B - r.T))
        {
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.L, r.T, 0, 0, b.Size);
            b.Save(Path.Combine(Dir, name), ImageFormat.Png);
        }
    }

    static dynamic Office(string progId)
    {
        Type t = Type.GetTypeFromProgID(progId);
        if (t == null) { Say("skip  " + progId + ": not installed"); return null; }
        return Activator.CreateInstance(t);
    }

    static void Notepad()
    {
        if (FindWindow((c, t) => c == "Notepad") != IntPtr.Zero) { Say("skip  Notepad: already open, close it first"); return; }
        Process.Start("notepad.exe");
        IntPtr np = WaitWindow((c, t) => c == "Notepad");
        Func<string> text = () =>
        {
            var doc = AutomationElement.FromHandle(np).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            return ((TextPattern)doc.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
        };
        try
        {
            Thread.Sleep(1200); Fore(np); English();
            TypeKeys(Keys); DoubleShift(); Check("Notepad", "A typed", text()); ClearAll(); English();
            TypeUnicode(Keys); DoubleShift(); Check("Notepad", "B line ", text());
        }
        finally { try { Fore(np); ClearAll(); } catch { } target = IntPtr.Zero; PostMessage(np, 0x10, IntPtr.Zero, IntPtr.Zero); }
    }

    static void Word()
    {
        dynamic app = Office("Word.Application");
        if (app == null) return;
        dynamic doc = null;
        try
        {
            app.Visible = true;
            doc = app.Documents.Add();
            doc.Content.Font.Size = 20;
            app.ActiveWindow.View.Zoom.Percentage = 100;
            app.WindowState = 0;
            Thread.Sleep(1500);
            IntPtr h = new IntPtr((int)app.ActiveWindow.Hwnd);
            MoveWindow(h, 80, 60, 1400, 700, true);
            Fore(h); English();
            TypeKeys(Keys); DoubleShift(); Check("Word", "A typed", (string)doc.Content.Text); Shot(h, "word-A.png"); ClearAll(); English();
            TypeUnicode(Keys); DoubleShift(); Check("Word", "B line ", (string)doc.Content.Text); Shot(h, "word-B.png");
        }
        finally
        {
            target = IntPtr.Zero;
            try { if (doc != null) doc.Close(0); if (app.Documents.Count == 0) app.Quit(0); } catch { }
        }
    }

    static void Excel()
    {
        dynamic app = Office("Excel.Application");
        if (app == null) return;
        dynamic wb = null;
        try
        {
            app.Visible = true;
            wb = app.Workbooks.Add();
            dynamic ws = wb.ActiveSheet;
            Thread.Sleep(1500);
            Fore(new IntPtr((int)app.Hwnd));
            ws.Range("A1").Select(); Thread.Sleep(300); English();
            TypeKeys(Keys); DoubleShift(); Tap(0x0D); Thread.Sleep(500);
            Check("Excel", "A typed", (string)ws.Range("A1").Text);
            ws.Range("A2").Select(); Thread.Sleep(300); English();
            TypeUnicode(Keys); DoubleShift(); Tap(0x0D); Thread.Sleep(500);
            Check("Excel", "B line ", (string)ws.Range("A2").Text, knownLimit: true); // Excel copies the whole cell
        }
        finally
        {
            try { Tap(0x1B); Thread.Sleep(300); } catch { }
            target = IntPtr.Zero;
            try { if (wb != null) wb.Close(false); if (app.Workbooks.Count == 0) app.Quit(); } catch { }
        }
    }

    // A local page whose title mirrors its text box, so the text can be read from the window title.
    static void Chrome()
    {
        string exe = null;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
            if (k != null) exe = k.GetValue(null) as string;
        if (exe == null || !File.Exists(exe)) { Say("skip  Chrome: not installed"); return; }
        string page = Path.Combine(Dir, "apps-chrome.html");
        File.WriteAllText(page, "<!doctype html><meta charset=utf-8><title>FFT:</title><textarea id=t autofocus style='width:90%;height:200px;font-size:20px'></textarea>" +
                                "<script>t.oninput=()=>document.title='FFT:'+t.value</script>", new UTF8Encoding(false));
        Process.Start(exe, "--new-window \"file:///" + page.Replace('\\', '/') + "\"");
        IntPtr h = WaitWindow((c, t) => t.StartsWith("FFT:"));
        if (h == IntPtr.Zero) throw new Exception("Chrome window not found");
        Func<string> text = () =>
        {
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            string t = sb.ToString();
            int i = t.LastIndexOf(" - ");
            return (i > 0 ? t.Substring(0, i) : t).Substring(4);
        };
        try
        {
            Thread.Sleep(1500); Fore(h); English();
            TypeKeys(Keys); DoubleShift(); Check("Chrome", "A typed", text()); ClearAll(); English();
            TypeUnicode(Keys); DoubleShift(); Check("Chrome", "B line ", text());
        }
        finally { try { Fore(h); ClearAll(); } catch { } target = IntPtr.Zero; PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware();
        Console.OutputEncoding = new UTF8Encoding(false);
        log = new StreamWriter(Path.Combine(Dir, "apps.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        string[] apps = args.Length > 0 ? args : new[] { "notepad", "word", "excel", "chrome" };
        foreach (string app in apps)
        {
            try
            {
                if (app == "notepad") Notepad();
                else if (app == "word") Word();
                else if (app == "excel") Excel();
                else if (app == "chrome") Chrome();
                else Say("skip  " + app + ": unknown app");
            }
            catch (Exception ex) { fails++; Say("FAIL  " + app + ": " + ex.Message); }
            Thread.Sleep(800);
            var ff = Process.GetProcessesByName("FlipFlop").FirstOrDefault();
            if (ff == null || !ff.Responding) { fails++; Say("FAIL  FlipFlop " + (ff == null ? "is not running (crashed?)" : "stopped responding")); break; }
        }
        Say(fails == 0 ? "all " + passes + " passed" : fails + " failed, " + passes + " passed");
        return fails;
    }
}
