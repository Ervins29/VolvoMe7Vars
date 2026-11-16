using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Me7Vars;

// me7vars - find RAM variables in a Volvo (Bosch ME7.0) flash by transferring them from a
// reference flash + A2L, and write a .ecu variables file for VolvoTools VolvoLogger (--variables).

const string Usage = """
    usage:
      me7vars target.bin [-o target.ecu] [--all] [--min-conf high|medium|inferred|low] [--names nmot_w,rl_w,...]

    writes target.ecu for VolvoTools:  VolvoLogger -f P2 --variables target.ecu -o log.csv
      (default) a ~40 variable logging set     --names a,b,c  only these     --all  catalogue of everything

    references (bin + A2L pairs) are picked up automatically from a "references" folder
    next to the program or in the current folder; the one whose code matches best is used.
      --ref NAME              use only this reference (folder or file name, see --list-refs)
      --ref-dir DIR           look for references in DIR instead
      --ref-bin F --ref-a2l F use this exact pair
      --list-refs             show the references found and exit
    """;

string? target = null, refBin = null, refA2l = null, outPath = null, names = null, refName = null, refDir = null;
var all = false;
var listRefs = false;
var minConf = Conf.Medium;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--ref-bin": refBin = Next(); break;
        case "--ref-a2l": refA2l = Next(); break;
        case "--ref": refName = Next(); break;
        case "--ref-dir": refDir = Next(); break;
        case "--list-refs": listRefs = true; break;
        case "-o" or "--out": outPath = Next(); break;
        case "--names": names = Next(); break;
        case "--all": all = true; break;
        case "--min-conf":
            minConf = Next() switch
            {
                "high" => Conf.High, "medium" => Conf.Medium, "inferred" => Conf.Inferred, "low" => Conf.Low,
                var v => throw new ArgumentException($"bad --min-conf {v}")
            };
            break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default:
            if (args[i].StartsWith('-')) { Console.Error.WriteLine($"unknown option {args[i]}\n{Usage}"); return 2; }
            target = args[i];
            break;
    }
}

// --- references -------------------------------------------------------------------------------
List<Reference> refs;
if (refBin is not null || refA2l is not null)
{
    if (refBin is null || refA2l is null) { Console.Error.WriteLine("--ref-bin and --ref-a2l go together"); return 2; }
    refs = new() { new Reference(Path.GetFileNameWithoutExtension(refBin), refBin, refA2l) };
}
else
{
    var dirs = refDir is not null ? new List<string> { refDir } : References.DefaultDirs().ToList();
    if (refDir is not null && !Directory.Exists(refDir)) { Console.Error.WriteLine($"no such folder: {refDir}"); return 2; }
    refs = References.Discover(dirs);
    if (refs.Count == 0)
    {
        Console.Error.WriteLine("no references found. Put a bin + A2L pair in a \"references\" folder, e.g.\n" +
                                "  references/359462/1xEbvsde.bin\n  references/359462/1xEbvsde.a2l\n" +
                                $"searched: {(dirs.Count > 0 ? string.Join(", ", dirs) : Path.Combine(AppContext.BaseDirectory, "references"))}");
        return 2;
    }
    if (refName is not null)
    {
        refs = refs.Where(r => r.Name.Equals(refName, StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileNameWithoutExtension(r.BinPath).Equals(refName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (refs.Count == 0) { Console.Error.WriteLine($"reference '{refName}' not found (see --list-refs)"); return 2; }
    }
}
if (listRefs)
{
    foreach (var r in refs) Console.WriteLine($"{r.Name,-16} {r.BinPath}\n{"",-16} {r.A2lPath}");
    return 0;
}
if (target is null) { Console.Error.WriteLine(Usage); return 2; }

var T = new Image(BinIo.Load(target));
var runs = new List<ReferenceRun>();
foreach (var r in refs)
{
    var run = References.Run(r, T);
    runs.Add(run);
    if (refs.Count > 1)
        Console.WriteLine($"  reference {r.Name,-16} code aligned {(100 * run.Score).ToString("F1", CultureInfo.InvariantCulture),5}%  " +
                          $"high={run.Found.Count(f => f.Conf == Conf.High)}");
}
var best = runs.OrderByDescending(x => x.Score).First();
var (meas, R, found, st) = (best.Meas, best.Image, best.Found, best.Stats);
Console.WriteLine($"reference: {best.Ref.Name} ({Path.GetFileName(best.Ref.BinPath)} + {Path.GetFileName(best.Ref.A2lPath)})");
if (!R.Dpp.SequenceEqual(T.Dpp))
    Console.Error.WriteLine($"warning: DPP setup differs ref=[{string.Join(", ", R.Dpp)}] target=[{string.Join(", ", T.Dpp)}]");
Console.WriteLine($"code aligned: {st.Aligned}/{st.RefInsns} instructions ({(100.0 * st.Aligned / st.RefInsns).ToString("F1", CultureInfo.InvariantCulture)}%)");
int Cnt(Conf c) => found.Count(f => f.Conf == c);
Console.WriteLine($"variables found: {found.Count}/{meas.Count}  high={Cnt(Conf.High)}  medium={Cnt(Conf.Medium)}  " +
                  $"inferred={Cnt(Conf.Inferred)}  low={Cnt(Conf.Low)}");
if (best.Score < 0.3)
    Console.Error.WriteLine("warning: less than 30% of the code lined up - this target is probably not close enough to any reference");

outPath ??= Path.ChangeExtension(target, ".ecu");
var basePath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath));
WriteReport(basePath + "_report.csv", found);

var byName = found.ToDictionary(f => f.Name);
var comments = new Dictionary<string, string>();
List<Found> rows;
if (all)
{
    rows = found.Where(f => f.Conf >= minConf)
                .OrderBy(f => f.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
}
else
{
    var sel = names is not null
        ? names.Split(',').Select(n => (n.Trim(), "")).ToList()
        : Favorites.List;
    rows = new();
    foreach (var (n, c) in sel)
    {
        if (!byName.TryGetValue(n, out var f)) { Console.WriteLine($"  not found: {n}"); continue; }
        if (f.Conf < minConf)
        {
            Console.WriteLine($"  skipped (confidence {f.Conf.ToString().ToLowerInvariant()}): {n} -> {FmtAddr(f.Addr)}");
            continue;
        }
        comments[n] = c;
        rows.Add(f);
    }
}
WriteEcu(outPath, rows, comments);
Console.WriteLine($"wrote {outPath} ({rows.Count} variables) and {basePath}_report.csv");
if (rows.Count > 64)
    Console.WriteLine("note: VolvoLogger registers every line with the ECU - use this file as a catalogue and log a smaller set (--names)");
return 0;

// ---------------------------------------------------------------------------------------------

static string FmtAddr(int a) => a < 0x10000 ? $"0x{a:X4}" : $"0x{a:X6}";

static string FmtNum(double x) =>
    x == Math.Truncate(x) ? ((long)x).ToString(CultureInfo.InvariantCulture)
                          : x.ToString("G9", CultureInfo.InvariantCulture).Replace('E', 'e');

static string PyFloat(double x)      // Python str(float)
{
    var s = x.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
    return s.Contains('.') || s.Contains('e') || s.Contains('N') || s.Contains('I') ? s : s + ".0";
}

static string EcuName(string n) => Regex.Replace(n, "_A$", "");

static void WriteEcu(string path, List<Found> rows, Dictionary<string, string> comments)
{
    string[] hdr = { "Name", "Address", "Size", "Bitmask", "Unit", "Signed", "I", "Factor", "Offset", "Comment" };
    int[] w = { 16, 9, 5, 8, 11, 7, 2, 12, 8 };
    string Line(IList<string> v) => string.Join(", ", v.Select((s, i) => i < 9 ? s.PadRight(w[i]) : s));
    var lines = new List<string> { Line(hdr) };
    foreach (var f in rows)
    {
        var m = f.Meas;
        var unit = (string.IsNullOrEmpty(m.Unit) ? "-" : m.Unit).Replace(',', ' ');
        var com = comments.TryGetValue(m.Name, out var c) && c.Length > 0 ? c : m.Desc;
        com = Regex.Replace(com, "[,;\r\n]+", " ").Trim();
        lines.Add(Line(new[]
        {
            EcuName(m.Name), FmtAddr(f.Addr).PadLeft(8), m.Size.ToString().PadLeft(4),
            $"0x{f.Bitmask:X4}".PadLeft(7), unit, (m.Signed ? "1" : "0").PadLeft(6), "0",
            FmtNum(m.Factor).PadLeft(11), FmtNum(m.Offset).PadLeft(7), com
        }));
    }
    File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
}

static void WriteReport(string path, List<Found> found)
{
    static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    static string Delta(int d) => (d < 0 ? "-" : "+") + "0x" + Math.Abs(d).ToString("x");
    var sb = new StringBuilder();
    sb.Append("name,target_addr,size,bitmask,confidence,votes,ref_addr,delta,unit,factor,offset,signed,description,note\r\n");
    foreach (var f in found.OrderBy(f => f.Addr).ThenBy(f => f.Name, StringComparer.Ordinal))
    {
        var m = f.Meas;
        string[] v =
        {
            m.Name, FmtAddr(f.Addr), m.Size.ToString(), $"0x{f.Bitmask:X4}", f.Conf.ToString().ToLowerInvariant(), f.Votes,
            FmtAddr(f.RefAddr), Delta(f.Addr - f.RefAddr), m.Unit, PyFloat(m.Factor), PyFloat(m.Offset),
            m.Signed ? "1" : "0", m.Desc, f.Note
        };
        sb.Append(string.Join(",", v.Select(Csv))).Append("\r\n");
    }
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
}

static class Favorites
{
    /// <summary>A2L name and a short comment, like the sample .ecu.</summary>
    public static readonly List<(string Name, string Comment)> List = new List<(string, string)>
    {
        ("wped_w", "Pedal Position"), ("nmot_w", "Engine Speed"), ("vfzg_w", "Vehicle Speed"),
        ("ps_w", "Manifold Pressure"), ("pvdkds_w", "Actual Boost Pressure"), ("plsol_w", "Desired Boost Pressure"),
        ("pu_w", "Ambient Pressure"),
        ("lamsoni_w", "Lambda Actual Value"), ("lamsons_w", "Lambda Target Value"), ("lambts_w", "Lambda BTS Value"),
        ("lamfa_w", "Lambda LAMFA Value"), ("lditv_w", "Boost PID I-Result"), ("ldtvm", "Boost Controller Duty Cycle"),
        ("rl_w", "Actual Load"), ("rlsol_w", "Requested Load"), ("rlmax_w", "Max Load"),
        ("miopt_w", "Optimum Indicated Torque"), ("mifa_w", "Desired Engine Torque"), ("mibas_w", "Base Torque"),
        ("zwout", "Ignition Angle Output"), ("zwist", "Actual Ignition Angle"), ("zwbas", "Base Ignition Angle"),
        ("mshfm_w", "Mass Air Flow"), ("ml_w", "Air Mass Flow (filtered)"), ("uhfm_w", "MAF Voltage"),
        ("tevfa_w", "Injection Time (FKKVS axis)"), ("te_w", "Effective Injection Time"),
        ("fr_w", "Lambda Control (Fuel Trim)"), ("wdkba", "Throttle Angle"),
        ("tabgm_w", "Modelled EGT"), ("tans", "Intake Air Temperature"), ("tmot", "Coolant Temperature"),
        ("ub", "Battery Voltage"), ("wkrma", "Average Knock Retard"),
    }.Concat(Enumerable.Range(0, 5).Select(i => ($"dwkrz_{i}_A", $"Cylinder {i + 1} Knock Retard"))).ToList();
}
