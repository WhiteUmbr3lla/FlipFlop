using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Runs on the UI (STA) thread. Never Thread.Sleep here: when another app copies, Windows asks the current clipboard
// owner (often us) to let go, and if we don't answer, that app hangs holding the clipboard. Wait() keeps answering.
static class Injector
{
    public static readonly IntPtr Tag = (IntPtr)0x464C4950; // "FLIP": marks our own SendInput so the hook skips it

    static bool busy;

    public static void Run(Mode mode)
    {
        if (busy) return; // Wait() pumps messages, so a second hotkey could arrive mid-run
        busy = true;
        try { RunCore(mode); }
        catch (Exception ex) { Log("failed: " + ex); }
        finally { busy = false; }
    }

    static void RunCore(Mode mode)
    {
        // custom hotkey: wait until its modifiers are released, or our Backspaces become Ctrl+Backspace
        for (int i = 0; i < 50 && (Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12)); i++) Wait(20);

        Undo undo; string typed;
        lock (Hooks.Lock) { undo = Hooks.LastUndo; typed = Hooks.Buffer.ToString(); }

        Log("hotkey in " + Hooks.LastApp + ": typed=" + typed.Length + " undo=" + (undo != null) + " mode=" + mode);
        if (undo != null) // second press: put it back
        {
            DataObject clip = undo.Paste ? SaveClip() : null;
            Apply(undo.Delete, undo.Text, undo.Paste);
            if (undo.Paste) RestoreClip(clip);
            SwitchLayout(undo.ToHe);
            lock (Hooks.Lock)
            {
                Hooks.LastUndo = null;
                Hooks.Buffer.Length = 0;
                if (undo.Buffer != null) Hooks.Buffer.Append(undo.Buffer);
                if (undo.Auto) Hooks.AutoSuppressed = true; // it was meant that way: don't re-fix it as they keep typing
            }
            return;
        }

        string conv; bool toHe;
        if (typed.Trim().Length > 0 && mode != Mode.SelectionOnly)
        {
            int start = Detector.Fix(typed, mode, out conv, out toHe);
            string old = typed.Substring(start);
            Apply(old.Length, conv, false);
            SwitchLayout(toHe);
            lock (Hooks.Lock)
            {
                Hooks.Buffer.Length = 0;
                Hooks.Buffer.Append(typed.Substring(0, start) + conv);
                Hooks.LastUndo = new Undo { Delete = conv.Length, Text = old, ToHe = !toHe, Buffer = typed };
            }
            return;
        }

        // Nothing typed since the last click: selection, else the current line. Never in a terminal, where Ctrl+C kills things.
        if (IsTerminal()) return;
        DataObject saved = SaveClip();
        uint seq = Native.GetClipboardSequenceNumber();
        try
        {
            string sel = Copy(), result = null;
            bool selectedLine = false;
            Log("selection: " + (sel == null ? "none" : sel.Length + " chars"));
            int start = 0;
            if (sel != null) start = Detector.Fix(sel, Mode.Whole, out conv, out toHe); // explicit selection: all of it
            else if (mode != Mode.SelectionOnly)
            {
                Send(Key(0x10, true), Key(0x24, true), Key(0x24, false), Key(0x10, false)); // Shift+Home
                selectedLine = true;
                sel = Copy();
                Log("line: " + (sel == null ? "none" : sel.Length + " chars"));
                if (sel == null) return;
                start = Detector.Fix(sel, mode, out conv, out toHe);
            }
            else return;
            result = sel.Substring(0, start) + conv;
            if (result == sel)
            {
                // Nothing to convert: drop the Shift+Home selection, or the next keystroke would wipe the line. Retyping
                // the same text puts the caret back where it was in every app (Right arrow differs between apps).
                if (selectedLine) Apply(0, sel, false);
                return;
            }
            // Typing over the selection replaces it like a paste would, minus the race with clipboard history that
            // can make a paste silently do nothing. Only multi-line text is pasted: a typed line break could be
            // Enter = "send" in a chat app.
            bool multi = sel.IndexOf('\n') >= 0;
            Apply(0, result, multi);
            SwitchLayout(toHe);
            lock (Hooks.Lock) Hooks.LastUndo = new Undo { Delete = Len(result), Text = sel, Paste = multi, ToHe = !toHe };
        }
        catch (Exception ex) { Log("clipboard path failed: " + ex); }
        finally
        {
            if (Native.GetClipboardSequenceNumber() != seq) RestoreClip(saved); // untouched if nothing was selected
        }
    }

    // %TEMP%\FlipFlop.log: which path each hotkey press took, and errors. No typed text goes in here. Off unless turned on in Settings.
    public static volatile bool Logging;
    public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "FlipFlop.log");
    public static void Log(string msg)
    {
        if (!Logging) return;
        try
        {
            string p = LogFile;
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 1 << 20) File.WriteAllText(p, ""); // start over past 1 MB
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
        }
        catch { }
    }

    static int Len(string s) { return s.Replace("\r\n", "\n").Length; } // one Backspace per line break

    // Formats the snapshot leaves out: OLE embedding (live storage objects, not data) and metafiles. With Word's copy
    // on the clipboard, reading or putting these back crashes inside ole32 (an access violation try/catch can't stop).
    // Pasting after a fix still gives the same text, formatting and bitmap pictures, just not a live embedded object.
    static readonly HashSet<string> Unsafe = new HashSet<string> { "Embed Source", "Link Source", "Object Descriptor",
        "Link Source Descriptor", "Embedded Object", "Native", "OwnerLink", "ObjectLink", "Ole Private Data",
        "EnhancedMetafile", "MetaFilePict" };

    // Snapshot every format on the clipboard (copied files incl. cut/copy, images, rich text, text) so we can put it all back.
    static DataObject SaveClip()
    {
        try
        {
            IDataObject live = Clipboard.GetDataObject();
            if (live == null) return null;
            var copy = new DataObject();
            foreach (string f in live.GetFormats(false))
                if (!Unsafe.Contains(f))
                    try { object d = live.GetData(f, false); if (d != null) copy.SetData(f, false, d); }
                catch { } // a few app-private formats can't be read back; the rest still survive
            return copy.GetFormats().Length > 0 ? copy : null;
        }
        catch (Exception ex) { Log("clipboard snapshot failed: " + ex.Message); return null; }
    }

    static void RestoreClip(DataObject saved)
    {
        Wait(150); // let the target app read the clipboard first
        try { if (saved != null) Clipboard.SetDataObject(saved, true); else Clipboard.Clear(); }
        catch (Exception ex)
        {
            Log("clipboard restore failed: " + ex.Message);
            try { if (saved != null && saved.ContainsText()) Clipboard.SetText(saved.GetText()); } catch { } // at least the text
        }
    }

    static string ClipText() { try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; } }

    // Ctrl+C and return the copied text, or null when nothing was selected (the clipboard didn't change).
    static string Copy()
    {
        uint seq = Native.GetClipboardSequenceNumber();
        Send(Key(0x11, true), Key(0x43, true), Key(0x43, false), Key(0x11, false));
        for (int i = 0; i < 25 && Native.GetClipboardSequenceNumber() == seq; i++) Wait(20);
        if (Native.GetClipboardSequenceNumber() == seq) return null;
        Wait(20); // the app may still be filling it in
        string t = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        return string.IsNullOrEmpty(t) ? null : t;
    }

    static void Wait(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (sw.ElapsedMilliseconds < ms);
    }

    static void Paste(string text)
    {
        Clipboard.SetText(text);
        for (int i = 0; i < 10 && ClipText() != text; i++) Wait(20); // make sure Ctrl+V can't paste the old contents
        // Clipboard history (Win+V) opens the clipboard right after every change; an app whose paste hits that
        // moment silently pastes nothing. Let it settle, then wait until nobody holds it.
        Wait(50);
        for (int i = 0; i < 30 && Native.GetOpenClipboardWindow() != IntPtr.Zero; i++) Wait(10);
        Send(Key(0x11, true), Key(0x56, true), Key(0x56, false), Key(0x11, false));
    }

    // Delete `delete` chars before the cursor, then type or paste `text`. A typed (Unicode) keystroke doesn't carry its
    // character in the message: an app that falls behind reads whichever one Windows holds when it catches up, so
    // Windows 11 Notepad, which stalls after each space, turned a burst into "שלום וווווו". Typing goes one character
    // per SendInput with a pause (settle), and so do the Backspaces, which it also drops from a burst. The hook thread
    // can't wait (settle false) and sends it all at once.
    // ponytail: auto-fix keeps the burst, so a multi-word auto-fix can still garble in Notepad; defer its typing to the
    // UI thread (holding the user's next keys meanwhile) if that shows up in practice.
    public static void Apply(int delete, string text, bool paste, bool settle = true)
    {
        var inputs = new List<Native.INPUT>();
        Action step = () => { if (settle) { Send(inputs.ToArray()); inputs.Clear(); Wait(5); } };
        for (int i = 0; i < delete; i++) { inputs.Add(Key(0x08, true)); inputs.Add(Key(0x08, false)); step(); }
        if (!paste)
            foreach (char c in text) { inputs.Add(Unicode(c, true)); inputs.Add(Unicode(c, false)); step(); }
        if (inputs.Count > 0) Send(inputs.ToArray());
        if (paste && text.Length > 0) { Paste(text); Wait(150); }
    }

    public static void SwitchLayout(bool toHe, bool settle = true)
    {
        // Posted messages jump ahead of queued keystrokes. Let the retyped text land first, or editors that follow
        // the text's language (WordPad, rich edit boxes) switch right back. The hook thread can't wait (settle false).
        if (settle) Wait(80);
        IntPtr fg = Native.GetForegroundWindow();
        uint pid;
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
        IntPtr target = Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(fg, out pid), ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        Native.PostMessage(target, 0x50, IntPtr.Zero, toHe ? Detector.HeHkl : Detector.EnHkl); // WM_INPUTLANGCHANGEREQUEST
    }

    public static bool IsTerminal()
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(Native.GetForegroundWindow(), sb, sb.Capacity);
        string c = sb.ToString();
        return c == "ConsoleWindowClass" || c == "CASCADIA_HOSTING_WINDOW_CLASS" || c == "mintty";
    }

    static Native.INPUT Key(int vk, bool down)
    {
        uint flags = down ? 0u : 2u; // KEYEVENTF_KEYUP
        if (vk >= 0x21 && vk <= 0x2E) flags |= 1; // nav keys are extended, else Shift+Home acts like numpad 7
        return new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT {
            wVk = (ushort)vk, wScan = (ushort)Native.MapVirtualKeyEx((uint)vk, 0, IntPtr.Zero), dwFlags = flags, extra = Tag } } };
    }

    static Native.INPUT Unicode(char c, bool down)
    {
        return new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT {
            wScan = c, dwFlags = 4u | (down ? 0u : 2u), extra = Tag } } }; // KEYEVENTF_UNICODE
    }

    static void Send(params Native.INPUT[] inputs)
    {
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }
}
