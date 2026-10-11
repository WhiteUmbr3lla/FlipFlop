using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

enum Mode { Smart, SelectionOnly, LastWord, Whole }

// Hebrew <-> English layout conversion and "where did the wrong-layout typing start" detection.
static class Detector
{
    public static IntPtr EnHkl, HeHkl;
    // (vk | shift<<8) -> char, per layout. Hooks use these to know what was typed.
    public static readonly Dictionary<int, char> EnKeys = new Dictionary<int, char>(), HeKeys = new Dictionary<int, char>();
    static readonly Dictionary<char, char> enToHe = new Dictionary<char, char>(), heToEn = new Dictionary<char, char>();
    static readonly Dictionary<string, double> enWords = new Dictionary<string, double>(), heWords = new Dictionary<string, double>();
    static HashSet<string> userWords = new HashSet<string>(); // names, slang... always count as real words
    const double Bad = -100, Mine = 25;

    public static void SetUserWords(IEnumerable<string> words)
    {
        var set = new HashSet<string>();
        foreach (string w in words) if (w.Trim().Length > 0) set.Add(w.Trim().ToLowerInvariant());
        userWords = set;
    }

    // Returns an error message, or null when ready.
    public static string Init()
    {
        var list = new IntPtr[32];
        int n = Native.GetKeyboardLayoutList(list.Length, list);
        for (int i = 0; i < n; i++)
        {
            long lang = (long)list[i] & 0x3FF; // primary language id
            if (lang == 0x09 && EnHkl == IntPtr.Zero) EnHkl = list[i];
            if (lang == 0x0D && HeHkl == IntPtr.Zero) HeHkl = list[i];
        }
        if (EnHkl == IntPtr.Zero || HeHkl == IntPtr.Zero)
            return "FlipFlop needs both an English and a Hebrew keyboard layout installed (Settings > Time & language > Language).";

        Fill(EnHkl, EnKeys);
        Fill(HeHkl, HeKeys);
        // Unshifted keys first so they win when a char appears twice (Hebrew layout's Shift+letter gives Latin capitals).
        for (int shift = 0; shift < 2; shift++)
            for (int vk = 0x20; vk < 0xFF; vk++)
            {
                char e, h;
                int k = vk | (shift << 8);
                if (vk >= 0x60 && vk <= 0x6F) continue; // numpad is the same in both; its "." would shadow the real "." -> "ץ"
                if (!EnKeys.TryGetValue(k, out e) || !HeKeys.TryGetValue(k, out h)) continue;
                if (!enToHe.ContainsKey(e)) enToHe[e] = h;
                if (!heToEn.ContainsKey(h)) heToEn[h] = e;
            }
        Load("en.txt", enWords);
        Load("he.txt", heWords);
        return null;
    }

    static void Fill(IntPtr hkl, Dictionary<int, char> map)
    {
        var state = new byte[256];
        var buf = new char[8];
        for (int shift = 0; shift < 2; shift++)
        {
            state[0x10] = (byte)(shift == 1 ? 0x80 : 0);
            for (int vk = 0x20; vk < 0xFF; vk++)
            {
                uint sc = Native.MapVirtualKeyEx((uint)vk, 0, hkl);
                // flag 4: don't touch the keyboard's dead-key state
                if (Native.ToUnicodeEx((uint)vk, sc, state, buf, buf.Length, 4, hkl) == 1 && buf[0] >= ' ' && buf[0] != 0x7F)
                    map[vk | (shift << 8)] = buf[0];
            }
        }
    }

    static void Load(string name, Dictionary<string, double> d)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var r = new StreamReader(s, Encoding.UTF8))
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                int sp = line.LastIndexOf(' ');
                long f;
                if (sp <= 0 || !long.TryParse(line.Substring(sp + 1), out f)) continue;
                string w = line.Substring(0, sp);
                if (!d.ContainsKey(w)) d[w] = Math.Log(f + 1);
            }
        }
    }

    static bool IsHe(char c) { return c >= 'א' && c <= 'ת'; }
    static bool IsEn(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
    static bool IsWordChar(char c) { return IsHe(c) || IsEn(c) || char.IsDigit(c); }

    // +1 = written in Hebrew (convert he->en), -1 = Latin (en->he), 0 = no letters.
    static int Dir(string w)
    {
        foreach (char c in w) { if (IsHe(c)) return 1; if (IsEn(c)) return -1; }
        return 0;
    }

    // Char-for-char, so converted text always has the same length as the input.
    public static string Convert(string s, bool fromHe)
    {
        var map = fromHe ? heToEn : enToHe;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char m;
            sb.Append(map.TryGetValue(fromHe ? c : char.ToLowerInvariant(c), out m) ? m : c);
        }
        return sb.ToString();
    }

    // How much does w look like a real word: log frequency, 0 = unknown, Bad = impossible.
    static double Score(string w)
    {
        if (w.Length == 0) return 0;
        bool he = false, en = false;
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            if (IsHe(c))
            {
                he = true;
                // final letters (ך ם ן ף ץ) never appear mid-word
                if ("ךםןףץ".IndexOf(c) >= 0 && i + 1 < w.Length && IsHe(w[i + 1])) return Bad;
            }
            else if (IsEn(c)) en = true;
            else if (!char.IsDigit(c) && "'\"-".IndexOf(c) < 0) return Bad; // e.g. "/" or "," inside a word
        }
        if (he && en) return Bad;
        double v;
        if (en)
        {
            string l = w.ToLowerInvariant();
            if (userWords.Contains(l)) return Mine;
            if (enWords.TryGetValue(l, out v)) return v;
            int a = l.IndexOf('\''); // the list has no contractions: what's -> what
            if (a > 0 && enWords.TryGetValue(l.Substring(0, a), out v)) return v;
        }
        if (he)
            // try with up to two one-letter prefixes stripped (ו ה ב ל מ ש כ)
            for (int p = 0; p <= 2 && w.Length - p >= 2; p++)
            {
                if (p > 0 && "והבלמשכ".IndexOf(w[p - 1]) < 0) break;
                if (userWords.Contains(w.Substring(p))) return Mine - p;
                if (heWords.TryGetValue(w.Substring(p), out v)) return v - p;
            }
        return 0;
    }

    // Strip leading/trailing non-letters. With `other` (same length), strip where either side isn't a letter,
    // so "akuo," vs "שלוםת" compares "akuo" vs "שלום".
    static string Trim(string w, string other)
    {
        int a = 0, b = w.Length;
        while (a < b && !(IsWordChar(w[a]) && (other == null || IsWordChar(other[a])))) a++;
        while (b > a && !(IsWordChar(w[b - 1]) && (other == null || IsWordChar(other[b - 1])))) b--;
        return w.Substring(a, b - a);
    }

    static double Best(string w, string other) { return Math.Max(Score(Trim(w, null)), Score(Trim(w, other))); }

    // Auto mode: is this just-finished word clearly typed in the wrong layout? Strict on purpose: words that are real
    // both ways (to/אם, cut/בוא), unknown words (names) and typos never fire. The hotkey covers those.
    const double AutoKnown = 5, AutoMargin = 4;
    public static bool AutoCandidate(string w)
    {
        int d = Dir(w), letters = 0;
        foreach (char ch in w) if (IsHe(ch) || IsEn(ch)) letters++;
        if (d == 0 || letters < 2) return false;
        string c = Convert(w, d > 0);
        double conv = Best(c, w);
        return conv >= AutoKnown && conv - Best(w, c) >= AutoMargin;
    }

    // How much better w reads converted than as typed. > 0 = probably typed in the wrong layout.
    static double Gain(string w)
    {
        int d = Dir(w);
        if (d == 0) return 0;
        string c = Convert(w, d > 0);
        return Best(c, w) - Best(w, c);
    }

    // Finds where the wrong-layout run starts. Returns that index; `converted` is the replacement for text[start..],
    // `toHe` says which layout the result is in.
    public static int Fix(string text, Mode mode, out string converted, out bool toHe)
    {
        var spans = new List<int[]>(); // [start, end) of each whitespace-separated word
        for (int i = 0; i < text.Length; )
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            spans.Add(new[] { s, i });
        }

        int start = text.Length, last = -1;
        for (int k = spans.Count - 1; k >= 0 && last < 0; k--)
            if (Dir(Word(text, spans[k])) != 0) last = k;
        if (last < 0) last = spans.Count - 1;

        if (mode == Mode.Whole || mode == Mode.SelectionOnly) start = 0;
        else if (mode == Mode.LastWord) { if (last >= 0) start = spans[last][0]; }
        else
        {
            // Pick the start that maximizes the total gain of everything after it. Judging the run as a whole matters:
            // אם/גם/בוא typed in English are "to"/"do"/"cut", real words, but the wrong words around them outweigh that.
            // Near-ties go to the longer run, so names and true toss-ups (cut/בוא) next to the run join it.
            // Converting everything gets a small bonus: no language switch mid-text, so "to ha kl zni" -> "אם יש לך זמן".
            const double Tie = 1, WholeBonus = 3.5;
            var sums = new double[spans.Count];
            double sum = 0, best = 0;
            for (int k = spans.Count - 1; k >= 0; k--)
            {
                sum += Gain(Word(text, spans[k]));
                sums[k] = sum + (k == 0 ? WholeBonus : 0);
                best = Math.Max(best, sums[k]);
            }
            for (int k = 0; k < spans.Count && best > 0; k++)
                if (sums[k] > 0 && sums[k] >= best - Tie) { start = spans[k][0]; break; }
            // nothing looked wrong but the user pressed the key anyway: flip the last word
            if (start == text.Length && last >= 0) start = spans[last][0];
        }

        // Convert the tail word by word, each by its own script; letterless words follow the majority.
        string tail = text.Substring(start);
        int he = 0, en = 0;
        foreach (char c in tail) { if (IsHe(c)) he++; else if (IsEn(c)) en++; }
        bool majorityHe = he > en;
        var sb = new StringBuilder(tail.Length);
        toHe = !majorityHe;
        for (int i = 0; i < tail.Length; )
        {
            if (char.IsWhiteSpace(tail[i])) { sb.Append(tail[i++]); continue; }
            int s = i;
            while (i < tail.Length && !char.IsWhiteSpace(tail[i])) i++;
            string w = tail.Substring(s, i - s);
            int d = Dir(w);
            bool fromHe = d == 0 ? majorityHe : d > 0;
            sb.Append(Convert(w, fromHe));
            if (d != 0) toHe = !fromHe;
        }
        converted = sb.ToString();
        return start;
    }

    static string Word(string text, int[] span) { return text.Substring(span[0], span[1] - span[0]); }
}
