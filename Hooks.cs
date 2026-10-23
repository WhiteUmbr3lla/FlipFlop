using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Undo { public int Delete; public string Text; public bool Paste, ToHe, Auto; public string Buffer; }

// Global keyboard/mouse hooks on their own thread, so the UI thread can block on the clipboard without stalling input.
static class Hooks
{
    public static readonly object Lock = new object();
    public static readonly StringBuilder Buffer = new StringBuilder(); // what was typed since the last click/navigation. RAM only.
    public static Undo LastUndo;                                        // cleared on any real input
    public static Keys HotKey = Keys.None;                              // Keys.None = double-tap Shift
    public static volatile bool Enabled = true;
    public static Action OnHotkey;                                      // called on the hook thread, must return fast
    public static volatile bool Auto;                                   // auto-fix on Space (setting)
    public static volatile bool InPassword;                             // focused field is a password (UI Automation)
    public static bool AutoSuppressed;                                  // user undid an auto-fix: leave this run alone
    public static volatile HashSet<string> Excluded = new HashSet<string>(); // "code.exe"...: FlipFlop stays out
    public static volatile string LastApp;                              // app last typed in, for the tray menu

    static readonly Native.HookProc kbProc = KbProc, msProc = MsProc;   // keep delegates alive
    static IntPtr lastFg;
    static bool windowIgnored, ignoredFg;                               // excluded app / full-screen game
    static int shiftDownAt, lastTapAt, swallowVk;
    static bool shiftDirty;

    public static void Start()
    {
        var t = new Thread(() =>
        {
            IntPtr mod = Native.GetModuleHandle(null);
            Native.SetWindowsHookEx(13, kbProc, mod, 0); // WH_KEYBOARD_LL
            Native.SetWindowsHookEx(14, msProc, mod, 0); // WH_MOUSE_LL
            Application.Run();
        });
        t.IsBackground = true;
        t.Start();
    }

    static IntPtr KbProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Enabled)
        {
            var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            bool down = wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104;
            // skip only our own SendInput; other injected input (touch keyboard, test drivers) is real typing
            if (k.extra != Injector.Tag && OnKey((int)k.vkCode, down)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static IntPtr MsProc(int code, IntPtr wParam, IntPtr lParam)
    {
        int m = (int)wParam;
        if (code >= 0 && (m == 0x201 || m == 0x204 || m == 0x207)) // any button down: cursor probably moved
            lock (Lock) { Reset(); LastUndo = null; lastTapAt = 0; }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static bool IsShift(int vk) { return vk == 0x10 || vk == 0xA0 || vk == 0xA1; }
    static bool IsMod(int vk) { return IsShift(vk) || (vk >= 0x11 && vk <= 0x12) || (vk >= 0xA2 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C; }
    static bool OtherModsDown() { return Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5B) || Native.Down(0x5C); }

    static Keys Mods()
    {
        Keys m = Keys.None;
        if (Native.Down(0x11)) m |= Keys.Control;
        if (Native.Down(0x12)) m |= Keys.Alt;
        if (Native.Down(0x10)) m |= Keys.Shift;
        return m;
    }

    // Returns true to swallow the key.
    static bool OnKey(int vk, bool down)
    {
        int now = Environment.TickCount;
        if (down)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastFg) { lastFg = fg; lock (Lock) Reset(); windowIgnored = Ignored(fg); }
            // re-checked on every key: games can go full screen in place (Alt+Enter)
            bool ignore = windowIgnored || CoversMonitor(fg);
            if (ignore != ignoredFg) Injector.Log((ignore ? "pausing in " : "active in ") + (LastApp ?? "?"));
            ignoredFg = ignore;
        }
        if (ignoredFg) { shiftDownAt = 0; lastTapAt = 0; return false; }
        if (!down)
        {
            if (vk == swallowVk) { swallowVk = 0; return true; }
            if (IsShift(vk) && HotKey == Keys.None && shiftDownAt != 0)
            {
                bool tap = !shiftDirty && now - shiftDownAt < 300;
                shiftDownAt = 0;
                if (tap && lastTapAt != 0 && now - lastTapAt < 400) { lastTapAt = 0; OnHotkey(); }
                else lastTapAt = tap ? now : 0;
            }
            return false;
        }

        if (IsShift(vk))
        {
            // Alt+Shift / Ctrl+Shift are layout switches, not taps
            if (shiftDownAt == 0) { shiftDownAt = now; shiftDirty = OtherModsDown(); }
            return false;
        }
        shiftDirty = true;
        lastTapAt = 0;

        if (HotKey != Keys.None && vk == (int)(HotKey & Keys.KeyCode) && Mods() == (HotKey & Keys.Modifiers))
        {
            if (swallowVk != vk) { swallowVk = vk; OnHotkey(); } // ignore auto-repeat
            return true;
        }
        if (IsMod(vk) || vk == 0x14) return false; // modifiers alone and CapsLock don't touch the buffer
        lock (Lock)
        {
            Record(vk);
            return vk == 0x20 && AutoFix();
        }
    }

    // Next keystroke re-checks the window, e.g. after the exclusion list changed.
    public static void Recheck() { lastFg = IntPtr.Zero; }

    // Borderless full screen (most modern games, also F11 browsers and videos): the window covers its whole monitor.
    // Maximized windows don't count (with an auto-hiding taskbar they cover it too), and neither does the desktop.
    static bool CoversMonitor(IntPtr fg)
    {
        if (fg == IntPtr.Zero || Native.IsZoomed(fg)) return false;
        var cls = new StringBuilder(32);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() == "Progman" || cls.ToString() == "WorkerW") return false;
        Native.RECT w;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
        if (!Native.GetWindowRect(fg, out w) || !Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi)) return false;
        return w.Left <= mi.Monitor.Left && w.Top <= mi.Monitor.Top && w.Right >= mi.Monitor.Right && w.Bottom >= mi.Monitor.Bottom;
    }

    // Checked once per window: is it an excluded app, or does Windows report a full-screen game/presentation?
    static bool Ignored(IntPtr fg)
    {
        string app = null;
        try
        {
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            app = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant() + ".exe";
            LastApp = app;
        }
        catch { } // protected process: we just don't know its name
        int state; // 2 busy (full-screen app), 3 Direct3D full-screen, 4 presentation mode
        if (Native.SHQueryUserNotificationState(out state) == 0 && state >= 2 && state <= 4) return true;
        return app != null && Excluded.Contains(app);
    }

    // A new run starts: different window, click, navigation...
    static void Reset() { Buffer.Length = 0; AutoSuppressed = false; }

    // Auto mode, on Space: if the word just finished is clearly in the wrong layout, fix the run and swallow the Space.
    // We type the Space ourselves after the fix, from inside the hook, so nothing typed next can land in between.
    static bool AutoFix()
    {
        if (!Auto || AutoSuppressed || InPassword || Buffer.Length < 2 || Buffer[Buffer.Length - 1] != ' ' || Injector.IsTerminal()) return false;
        string typed = Buffer.ToString(0, Buffer.Length - 1);
        int ws = typed.LastIndexOf(' ') + 1;
        if (!Detector.AutoCandidate(typed.Substring(ws))) return false;
        string conv; bool toHe;
        int start = Detector.Fix(typed, Mode.Smart, out conv, out toHe);
        string old = typed.Substring(start);
        Injector.Apply(old.Length, conv + " ", false, false);
        Injector.SwitchLayout(toHe, false);
        Buffer.Length = 0;
        Buffer.Append(typed.Substring(0, start) + conv + " ");
        LastUndo = new Undo { Delete = conv.Length + 1, Text = old + " ", ToHe = !toHe, Buffer = typed + " ", Auto = true };
        Injector.Log("auto-fix in " + LastApp + ": " + old.Length + " chars");
        return true;
    }

    static void Record(int vk)
    {
        LastUndo = null;
        IntPtr fg = lastFg; // OnKey just checked it
        if (OtherModsDown()) { Reset(); return; } // Ctrl+V, Ctrl+Backspace, AltGr...: can't track
        if (vk == 0x08) { if (Buffer.Length > 0) Buffer.Length--; return; }

        uint pid;
        long lang = (long)Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(fg, out pid)) & 0x3FF;
        var map = lang == 0x09 ? Detector.EnKeys : lang == 0x0D ? Detector.HeKeys : null;
        char c;
        // ponytail: CapsLock ignored; it only changes Latin case, which conversion drops anyway
        if (map != null && map.TryGetValue(vk | (Native.Down(0x10) ? 0x100 : 0), out c)) Buffer.Append(c);
        else Reset(); // Enter, Tab, arrows, Home/End, Delete, Esc, other layouts...
    }
}
