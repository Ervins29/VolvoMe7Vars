namespace Me7Vars;

/// <summary>Insertion-ordered counter; <see cref="MostCommon"/> breaks ties by first insertion.</summary>
public sealed class Counter<T> where T : notnull
{
    readonly Dictionary<T, int> _c = new();
    public void Add(T key) => _c[key] = _c.TryGetValue(key, out var v) ? v + 1 : 1;
    public int Total => _c.Values.Sum();
    public (T Key, int Count) MostCommon()
    {
        T best = default!; var n = -1;
        foreach (var (k, v) in _c) if (v > n) { best = k; n = v; }
        return (best, n);
    }
}

/// <summary>
/// Aligns a reference and a target image on masked C166 code (unique seed windows,
/// extended in both directions) and lets every aligned pair vote on data addresses.
/// </summary>
public static class Transfer
{
    public const int K = 8;   // seed window length in instructions

    static List<string> Keys(Image img)
    {
        var ins = img.Insns;
        var keys = new List<string>(Math.Max(0, ins.Count - K + 1));
        Span<char> buf = stackalloc char[K * 3];
        for (var i = 0; i + K <= ins.Count; i++)
        {
            for (var j = 0; j < K; j++)
            {
                var v = ins[i + j].Masked;
                buf[3 * j] = (char)(v & 0xFFFF);
                buf[3 * j + 1] = (char)((v >> 16) & 0xFFFF);
                buf[3 * j + 2] = (char)((v >> 32) & 0xFFFF);
            }
            keys.Add(new string(buf));
        }
        return keys;
    }

    static bool Informative(Image img, int i)
    {
        var anyRef = false;
        var distinct = new HashSet<ulong>();
        for (var j = i; j < i + K; j++)
        {
            if (img.Insns[j].Refs.Length > 0) anyRef = true;
            distinct.Add(img.Insns[j].Masked);
        }
        return anyRef && distinct.Count >= 4;
    }

    static Dictionary<string, int> CountKeys(List<string> keys)
    {
        var c = new Dictionary<string, int>(keys.Count);
        foreach (var k in keys) c[k] = c.TryGetValue(k, out var v) ? v + 1 : 1;
        return c;
    }

    /// <summary>Aligned (ref index, target index) pairs, in insertion order.</summary>
    public static List<(int Ri, int Ti)> Align(Image r, Image t)
    {
        var rk = Keys(r);
        var tk = Keys(t);
        var rc = CountKeys(rk);
        var tc = CountKeys(tk);
        var tpos = new Dictionary<string, int>();
        for (var i = 0; i < tk.Count; i++) if (tc[tk[i]] == 1) tpos[tk[i]] = i;

        var order = new List<(int, int)>();
        var seen = new HashSet<int>();
        var covered = new HashSet<int>();
        var R = r.Insns; var T = t.Insns;
        for (var i = 0; i < rk.Count; i++)
        {
            var k = rk[i];
            if (covered.Contains(i) || rc[k] != 1 || !tpos.TryGetValue(k, out var j) || !Informative(r, i)) continue;
            int a = i, b = j;
            while (a > 0 && b > 0 && R[a - 1].Masked == T[b - 1].Masked) { a--; b--; }
            int c = i + K, d = j + K;
            while (c < R.Count && d < T.Count && R[c].Masked == T[d].Masked) { c++; d++; }
            for (var x = 0; x < c - a; x++)
            {
                if (seen.Add(a + x)) order.Add((a + x, b + x));
                covered.Add(a + x);
            }
        }
        return order;
    }

    public sealed class Votes
    {
        public Dictionary<int, Counter<int>> Direct { get; } = new();
        public Dictionary<(int Addr, int Q), Counter<(int Addr, int Q)>> Bits { get; } = new();
    }

    public static Votes Vote(Image r, Image t, List<(int Ri, int Ti)> pairs)
    {
        var v = new Votes();
        foreach (var (ri, ti) in pairs)
        {
            var rr = r.Insns[ri].Refs; var tr = t.Insns[ti].Refs;
            if (rr.Length != tr.Length) continue;
            for (var i = 0; i < rr.Length; i++)
            {
                var (x, y) = (rr[i], tr[i]);
                if (x.Kind != y.Kind) continue;
                if (x.Kind == RefKind.Bit)
                {
                    if (!v.Bits.TryGetValue((x.Addr, x.Q), out var cb)) v.Bits[(x.Addr, x.Q)] = cb = new();
                    cb.Add((y.Addr, y.Q));
                }
                else
                {
                    if (!v.Direct.TryGetValue(x.Addr, out var cd)) v.Direct[x.Addr] = cd = new();
                    cd.Add(y.Addr);
                }
            }
        }
        return v;
    }
}
