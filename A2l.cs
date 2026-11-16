using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Me7Vars;

/// <summary>One A2L MEASUREMENT (RAM variable).</summary>
public sealed record Measurement(
    string Name, string Desc, string Type, int Addr, int Bitmask,
    double Factor, double Offset, string Unit, int Size, bool Signed);

/// <summary>Minimal ASAP2 reader: COMPU_METHOD (linear RAT_FUNC), MEASUREMENT, CHARACTERISTIC.</summary>
public static class A2l
{
    static readonly Regex Token = new("\"[^\"]*\"|\\S+", RegexOptions.Compiled);
    static readonly Regex Coeffs = new(@"COEFFS\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)", RegexOptions.Compiled);
    static readonly Regex McMess = new(@"MCMESS\s+(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);
    static readonly Regex EcuAddr = new(@"ECU_ADDRESS\s+(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);
    static readonly Regex BitMask = new(@"BIT_MASK\s+(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);

    static IEnumerable<string> Blocks(string text, string kind)
    {
        var re = new Regex($@"/begin {kind}\s(.*?)/end {kind}", RegexOptions.Singleline);
        foreach (Match m in re.Matches(text)) yield return m.Groups[1].Value;
    }

    static List<string> Tokens(string s) => Token.Matches(s).Select(m => m.Value).ToList();

    static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    static int Hex(string s) => Convert.ToInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16);

    public static (Dictionary<string, Measurement> Meas, Dictionary<string, int> Chars) Parse(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)).Replace("\r", "");

        var cms = new Dictionary<string, (double F, double O, string Unit)>();
        foreach (var b in Blocks(text, "COMPU_METHOD"))
        {
            var tk = Tokens(b);
            double f = 1.0, o = 0.0;
            var m = Coeffs.Match(b);
            if (m.Success)
            {
                var c = Enumerable.Range(1, 6).Select(i => D(m.Groups[i].Value)).ToArray();
                // phys -> int: (a x^2 + b x + c) / (d x^2 + e x + f); linear => x = (int*f - c) / b
                if (c[0] == 0 && c[3] == 0 && c[4] == 0 && c[1] != 0)
                {
                    f = c[5] / c[1];
                    o = -c[2] / c[1];
                }
            }
            var unit = tk.Count > 4 ? tk[4].Trim('"') : "";
            cms[tk[0]] = (f, o, unit);
        }

        var meas = new Dictionary<string, Measurement>();
        foreach (var b in Blocks(text, "MEASUREMENT"))
        {
            var tk = Tokens(b);
            string name = tk[0], desc = tk[1].Trim('"'), dt = tk[2], cm = tk[3];
            var m = McMess.Match(b);
            if (!m.Success) m = EcuAddr.Match(b);
            if (!m.Success) continue;
            var bm = BitMask.Match(b);
            var (f, o, u) = cms.TryGetValue(cm, out var cv) ? cv : (1.0, 0.0, "");
            var size = dt.Contains("BYTE") ? 1 : dt.Contains("LONG") ? 4 : 2;
            meas[name] = new Measurement(name, desc, dt, Hex(m.Groups[1].Value),
                bm.Success ? Hex(bm.Groups[1].Value) : 0, f, o, u, size, dt.StartsWith('S'));
        }

        var chars = new Dictionary<string, int>();
        foreach (var b in Blocks(text, "CHARACTERISTIC"))
        {
            var tk = Tokens(b);
            if (tk.Count > 3)
            {
                try { chars[tk[0]] = Hex(tk[3]); } catch (FormatException) { }
            }
        }
        return (meas, chars);
    }
}
