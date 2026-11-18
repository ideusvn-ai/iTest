namespace ITest.Core;

public enum ImeMode { Telex, Vni }

/// <summary>
/// Bộ gõ tiếng Việt TELEX / VNI xử lý từng phím: nhận từ đứng trước con trỏ và phím vừa gõ,
/// trả về từ mới (hoặc null nếu phím không phải phím dấu/phím biến đổi).
/// </summary>
public static class VietIme
{
    private static readonly Dictionary<char, string> Table = new()
    {
        ['a'] = "aáàảãạ", ['ă'] = "ăắằẳẵặ", ['â'] = "âấầẩẫậ", ['e'] = "eéèẻẽẹ", ['ê'] = "êếềểễệ",
        ['i'] = "iíìỉĩị", ['o'] = "oóòỏõọ", ['ô'] = "ôốồổỗộ", ['ơ'] = "ơớờởỡợ", ['u'] = "uúùủũụ",
        ['ư'] = "ưứừửữự", ['y'] = "yýỳỷỹỵ"
    };
    private static readonly Dictionary<char, (char b, int t)> Reverse = BuildReverse();

    private static Dictionary<char, (char, int)> BuildReverse()
    {
        var r = new Dictionary<char, (char, int)>();
        foreach (var (b, s) in Table)
            for (int t = 0; t < s.Length; t++) r[s[t]] = (b, t);
        return r;
    }

    private sealed class Ch { public char B; public int T; public bool Up; public bool V; }

    public static bool IsWordChar(char c) => char.IsLetter(c);

    private static List<Ch> Parse(string word) => word.Select(c =>
    {
        var low = char.ToLowerInvariant(c);
        return Reverse.TryGetValue(low, out var r)
            ? new Ch { B = r.b, T = r.t, Up = c != low, V = true }
            : new Ch { B = low, T = 0, Up = c != low, V = false };
    }).ToList();

    private static string Build(List<Ch> a) => string.Concat(a.Select(x =>
    {
        var c = x.V ? Table[x.B][x.T] : x.B;
        return x.Up ? char.ToUpperInvariant(c) : c;
    }));

    private static List<int> VowelIdx(List<Ch> a)
    {
        var idx = new List<int>();
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].V) continue;
            if (i == 1 && a[i].B == 'u' && a[0].B == 'q') continue;
            if (i == 1 && a[i].B == 'i' && a[0].B == 'g' && a.Count > 2 && a[2].V) continue;
            idx.Add(i);
        }
        return idx;
    }

    private static int TonePos(List<Ch> a)
    {
        var v = VowelIdx(a);
        if (v.Count == 0) return -1;
        var marked = v.Where(i => "ăâêôơư".IndexOf(a[i].B) >= 0).ToList();
        if (marked.Count > 0) return marked[^1];
        if (v.Count == 1) return v[0];
        if (v.Count >= 3) return v[1];
        var pair = $"{a[v[0]].B}{a[v[1]].B}";
        if (v[1] < a.Count - 1) return v[1];
        return pair is "oa" or "oe" or "uy" ? v[1] : v[0];
    }

    private static int CurTone(List<Ch> a) => a.Where(x => x.V).Select(x => x.T).DefaultIfEmpty(0).Max();

    private static void ClearTone(List<Ch> a) { foreach (var x in a) if (x.V) x.T = 0; }

    private static List<Ch> Retone(List<Ch> a)
    {
        int t = CurTone(a);
        ClearTone(a);
        int p = TonePos(a);
        if (p >= 0) a[p].T = t;
        return a;
    }

    public static string? ApplyKey(string word, char key, ImeMode mode)
    {
        if (word.Length == 0) return null;
        char k = char.ToLowerInvariant(key);
        var a = Parse(word);
        var v = VowelIdx(a);

        int tone = -1;
        if (mode == ImeMode.Telex)
            tone = k switch { 's' => 1, 'f' => 2, 'r' => 3, 'x' => 4, 'j' => 5, 'z' => 0, _ => -1 };
        else
            tone = k switch { '1' => 1, '2' => 2, '3' => 3, '4' => 4, '5' => 5, '0' => 0, _ => -1 };

        if (tone >= 0)
        {
            if (v.Count == 0) return null;
            int cur = CurTone(a);
            if (tone == 0) { if (cur == 0) return null; ClearTone(a); return Build(a); }
            if (cur == tone) { ClearTone(a); return Build(a) + key; }
            ClearTone(a);
            a[TonePos(a)].T = tone;
            return Build(a);
        }

        int FindLast(params char[] bs)
        {
            for (int j = v.Count - 1; j >= 0; j--) if (Array.IndexOf(bs, a[v[j]].B) >= 0) return v[j];
            return -1;
        }
        string SetB(int i, char b) { a[i].B = b; return Build(Retone(a)); }

        if ((mode == ImeMode.Telex && k == 'd') || (mode == ImeMode.Vni && k == '9'))
        {
            if (a[0].B == 'd') { a[0].B = 'đ'; return Build(a); }
            if (mode == ImeMode.Telex && a[0].B == 'đ') { a[0].B = 'd'; return Build(a) + key; }
            return null;
        }

        if (mode == ImeMode.Telex && (k == 'a' || k == 'e' || k == 'o'))
        {
            char hat = k == 'a' ? 'â' : k == 'e' ? 'ê' : 'ô';
            int i = k == 'a' ? FindLast('a', 'â', 'ă') : k == 'o' ? FindLast('o', 'ô', 'ơ') : FindLast('e', 'ê');
            if (i < 0) return null;
            if (a[i].B == hat) { a[i].B = k; return Build(Retone(a)) + key; }
            return SetB(i, hat);
        }

        if ((mode == ImeMode.Telex && k == 'w') || (mode == ImeMode.Vni && (k == '7' || k == '8')))
        {
            if (k != '8')
            {
                for (int n = 0; n < v.Count - 1; n++)
                {
                    int j = v[n];
                    if (a[j].B == 'u' && v[n + 1] == j + 1 && (a[j + 1].B == 'o' || a[j + 1].B == 'ơ'))
                    {
                        a[j].B = 'ư'; a[j + 1].B = 'ơ';
                        return Build(Retone(a));
                    }
                }
                int io = FindLast('o', 'u');
                if (io >= 0) return SetB(io, a[io].B == 'o' ? 'ơ' : 'ư');
            }
            if (k != '7')
            {
                int ia = FindLast('a');
                if (ia >= 0) return SetB(ia, 'ă');
            }
            if (mode == ImeMode.Telex)
            {
                int back = FindLast('ư', 'ơ', 'ă');
                if (back >= 0)
                {
                    a[back].B = a[back].B == 'ư' ? 'u' : a[back].B == 'ơ' ? 'o' : 'a';
                    return Build(Retone(a)) + key;
                }
            }
            return null;
        }

        if (mode == ImeMode.Vni && k == '6')
        {
            int i = FindLast('a', 'e', 'o');
            if (i >= 0) return SetB(i, a[i].B == 'a' ? 'â' : a[i].B == 'e' ? 'ê' : 'ô');
        }
        return null;
    }

    /// <summary>
    /// Xử lý một ký tự gõ vào tại vị trí <paramref name="caret"/> của <paramref name="text"/>.
    /// Trả về true và văn bản/con trỏ mới nếu bộ gõ đã biến đổi từ.
    /// </summary>
    public static bool Process(string text, int caret, char key, ImeMode mode, out string newText, out int newCaret)
    {
        newText = text; newCaret = caret;
        int start = caret;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var word = text.Substring(start, caret - start);
        var res = ApplyKey(word, key, mode);
        if (res == null) return false;
        newText = text.Substring(0, start) + res + text.Substring(caret);
        newCaret = start + res.Length;
        return true;
    }
}
