using System.Text.RegularExpressions;

namespace Me7Vars;

public enum Conf { Low = 0, Inferred = 1, Medium = 2, High = 3 }

public sealed class Found
{
    public required string Name { get; init; }
    public required int Addr { get; init; }
    public required int Bitmask { get; init; }
    public required Conf Conf { get; set; }
    public required string Votes { get; init; }
    public required int RefAddr { get; init; }
    public required Measurement Meas { get; init; }
    public string Note { get; set; } = "";
}

public sealed record Stats(int Aligned, int RefInsns, int TgtInsns);

/// <summary>Turns alignment votes into one result per A2L measurement, with confidence.</summary>
public static class Finder
{
    public static string Region(int a) =>
        a >= 0xF000 && a < 0x10000 ? "iram" :
        a >= 0x300000 && a < 0x310000 ? "xram" :
        a >= 0x380000 && a < 0x390000 ? "xram2" : "other";

    static int? BitIndex(int mask) =>
        mask != 0 && (mask & (mask - 1)) == 0 ? System.Numerics.BitOperations.Log2((uint)mask) : null;

    public static (List<Found> Found, Stats Stats) FindVariables(
        Image r, Image t, IReadOnlyDictionary<string, Measurement> meas, int maxGap = 0x100, int maxDelta = 0x800)
    {
        var pairs = Transfer.Align(r, t);
        var votes = Transfer.Vote(r, t, pairs);

        var direct = new Dictionary<int, (int T, int N, int Tot)>();
        foreach (var (k, c) in votes.Direct)
        {
            var (best, n) = c.MostCommon();
            if (Region(k) == Region(best) && Region(best) != "other") direct[k] = (best, n, c.Total);
        }
        var bits = new Dictionary<(int, int), (int TW, int TQ, int N, int Tot)>();
        foreach (var (k, c) in votes.Bits)
        {
            var (best, n) = c.MostCommon();
            bits[k] = (best.Addr, best.Q, n, c.Total);
        }

        // shifts of trustworthy direct mappings, for neighbour checks and inference
        var strong = direct.Where(kv => kv.Value.N >= 2 && (double)kv.Value.N / kv.Value.Tot >= 0.9)
                           .Select(kv => (A: kv.Key, D: kv.Value.T - kv.Key))
                           .OrderBy(x => x.A).ThenBy(x => x.D).ToList();
        var sAddr = strong.Select(x => x.A).ToList();

        ((int A, int D)? Prev, (int A, int D)? Next) Neighbours(int a)
        {
            var i = sAddr.BinarySearch(a);
            if (i < 0) i = ~i;
            (int, int)? prev = i > 0 ? strong[i - 1] : null;
            (int, int)? next = i < strong.Count && strong[i].A != a ? strong[i]
                             : (i + 1 < strong.Count ? strong[i + 1] : null);
            return (prev, next);
        }

        int? Infer(int a)
        {
            var (p, n) = Neighbours(a);
            if (p is { } pv && n is { } nv && pv.D == nv.D && a - pv.A <= maxGap && nv.A - a <= maxGap &&
                (a >> 16) == ((a + pv.D) >> 16))
                return a + pv.D;
            return null;
        }

        var outList = new List<Found>();
        foreach (var m in meas.Values)
        {
            int a = m.Addr, mask = m.Bitmask;
            Found? res = null;
            var q = BitIndex(mask);
            if (mask != 0 && a >= 0xFD00 && a < 0xFE00 && q is int qi && bits.TryGetValue((a, qi), out var hit))
            {
                var ratio = (double)hit.N / hit.Tot;
                var conf = hit.N >= 2 && ratio >= 0.9 ? Conf.High : ratio >= 0.75 ? Conf.Medium : Conf.Low;
                res = new Found { Name = m.Name, Addr = hit.TW, Bitmask = hit.TQ >= 0 ? 1 << hit.TQ : mask, Conf = conf,
                                  Votes = $"{hit.N}/{hit.Tot}", RefAddr = a, Meas = m };
            }
            if (res is null && direct.TryGetValue(a, out var dv))
            {
                var ratio = (double)dv.N / dv.Tot;
                var conf = dv.N >= 3 && ratio >= 0.9 ? Conf.High : ratio >= 0.75 ? Conf.Medium : Conf.Low;
                if (conf != Conf.High)
                {
                    // promote when the shift agrees with a close neighbour
                    var (p, n) = Neighbours(a);
                    var d = dv.T - a;
                    if ((p is { } pv && pv.D == d && a - pv.A <= maxGap) || (n is { } nv && nv.D == d && nv.A - a <= maxGap))
                        conf = conf == Conf.Medium ? Conf.High : Conf.Medium;
                }
                if (Math.Abs(dv.T - a) > maxDelta) conf = Conf.Low;
                res = new Found { Name = m.Name, Addr = dv.T, Bitmask = mask, Conf = conf,
                                  Votes = $"{dv.N}/{dv.Tot}", RefAddr = a, Meas = m };
            }
            if (res is null && Infer(a) is int ta)
                res = new Found { Name = m.Name, Addr = ta, Bitmask = mask, Conf = Conf.Inferred,
                                  Votes = "0", RefAddr = a, Meas = m };
            if (res is not null) outList.Add(res);
        }

        // array elements (x_1_A, x_2_A ...) inherit the confidence of a directly found sibling
        var byName = outList.ToDictionary(f => f.Name);
        var arr = new Regex(@"^(.*)_(\d+)_A$");
        foreach (var f in outList)
        {
            var mm = arr.Match(f.Name);
            if (!mm.Success || (f.Conf != Conf.Inferred && f.Conf != Conf.Low)) continue;
            for (var k = 0; k < 64; k++)
            {
                if (byName.TryGetValue($"{mm.Groups[1].Value}_{k}_A", out var sib) && !ReferenceEquals(sib, f) &&
                    sib.Conf is Conf.High or Conf.Medium && sib.Addr - sib.RefAddr == f.Addr - f.RefAddr)
                {
                    f.Conf = sib.Conf;
                    f.Note = "array element of " + sib.Name;
                    break;
                }
            }
        }

        // flag collisions: different variables landing on the same target bytes (unless they overlap in the reference too)
        var owner = new Dictionary<(int, int), Found>();
        foreach (var f in outList.OrderBy(f => -(int)f.Conf))
        {
            bool RefOverlap(Found o) => o.RefAddr < f.RefAddr + f.Meas.Size && f.RefAddr < o.RefAddr + o.Meas.Size;
            var keys = Enumerable.Range(f.Addr, f.Meas.Size).Select(x => (x, f.Bitmask)).ToList();
            var clash = keys.Where(k => owner.TryGetValue(k, out var o) && o.RefAddr != f.RefAddr && !RefOverlap(o))
                            .Select(k => owner[k]).ToList();
            if (clash.Count > 0 && (int)f.Conf <= (int)clash[0].Conf)
            {
                f.Conf = Conf.Low;
                f.Note = "collides with " + clash[0].Name;
            }
            foreach (var k in keys) owner.TryAdd(k, f);
        }
        return (outList, new Stats(pairs.Count, r.Insns.Count, t.Insns.Count));
    }
}
