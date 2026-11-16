namespace Me7Vars;

/// <summary>A reference flash plus the A2L that describes it.</summary>
public sealed record Reference(string Name, string BinPath, string A2lPath);

/// <summary>Result of aligning the target against one reference.</summary>
public sealed record ReferenceRun(
    Reference Ref, Dictionary<string, Measurement> Meas, Image Image, List<Found> Found, Stats Stats)
{
    /// <summary>Share of the reference code that lined up with the target.</summary>
    public double Score => Stats.RefInsns == 0 ? 0 : (double)Stats.Aligned / Stats.RefInsns;
}

/// <summary>
/// Finds reference pairs on disk. A reference is an .a2l file with a .bin next to it:
/// same file name (1xEbvsde.a2l + 1xEbvsde.bin), or the only .bin in that folder.
/// The reference is named after its folder, e.g. references/359462/ -> "359462".
/// </summary>
public static class References
{
    /// <summary>Folders searched when --ref-dir is not given, in order.</summary>
    public static IEnumerable<string> DefaultDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "references"),
                     Path.Combine(Directory.GetCurrentDirectory(), "references"),
                 })
        {
            var full = Path.GetFullPath(d);
            if (Directory.Exists(full) && seen.Add(full)) yield return full;
        }
    }

    public static List<Reference> Discover(IEnumerable<string> dirs)
    {
        var list = new List<Reference>();
        var seenBins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            foreach (var a2l in Directory.EnumerateFiles(dir, "*.a2l", SearchOption.AllDirectories)
                                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetDirectoryName(a2l)!;
                var bin = FindBin(folder, Path.GetFileNameWithoutExtension(a2l));
                if (bin is null)
                {
                    Console.Error.WriteLine($"warning: no .bin next to {a2l}, skipped");
                    continue;
                }
                if (!seenBins.Add(Path.GetFullPath(bin))) continue;
                var name = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)
                               .Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(a2l)
                    : Path.GetFileName(folder);
                list.Add(new Reference(name, bin, a2l));
            }
        }
        return list;
    }

    static string? FindBin(string folder, string stem)
    {
        var bins = Directory.EnumerateFiles(folder)
                            .Where(p => Path.GetExtension(p).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                            .ToList();
        return bins.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase))
               ?? (bins.Count == 1 ? bins[0] : null);
    }

    public static ReferenceRun Run(Reference r, Image target)
    {
        var (meas, _) = A2l.Parse(r.A2lPath);
        var img = new Image(BinIo.Load(r.BinPath));
        var (found, stats) = Finder.FindVariables(img, target, meas);
        return new ReferenceRun(r, meas, img, found, stats);
    }
}

public static class BinIo
{
    /// <summary>Reads a flash image, padding anything shorter than 1 MB with 0xFF.</summary>
    public static byte[] Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length >= 0x100000) return d;
        var p = new byte[0x100000];
        Array.Fill(p, (byte)0xFF);
        d.CopyTo(p, 0);
        return p;
    }
}
