// `inspect` (issue #30): read a pulled layer blob as a tar stream and report
// what the image ACTUALLY contains — entry types, which Windows metadata the
// PAX records carry, and the special shapes the import loop must handle
// (symlinks, junctions, hard links, alternate data streams, EAs).
//
// This exists because the import port is written against hcsshim's handling of
// the general case, while the claims we make about THIS image (e.g. "unelevated
// extraction should die at the first symlink") must be grounded in what the
// image really has. Reading is unprivileged, so the inventory is available
// before any privileged step is attempted.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace HcsContainerSpike;

internal static partial class Program
{
    private static int Inspect(string[] args)
    {
        string metadataPath = Opt(args, "--metadata")
            ?? throw new ArgumentException("--metadata <image metadata json from pull> is required");
        int sampleLimit = OptInt(args, "--samples", 8);

        JsonNode metadata = ReadImageMetadata(metadataPath, out string blobPath);
        Console.WriteLine($"[inspect] image={(string?)metadata["image"]}");
        Console.WriteLine($"[inspect] blob={blobPath}");

        var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var paxKeyCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<string> symlinks = [];
        List<string> junctions = [];
        List<string> hardLinks = [];
        List<string> alternateStreams = [];
        List<string> whiteouts = [];
        long entries = 0;
        long regularBytes = 0;
        bool hasUtilityVm = false;

        using FileStream compressed = File.OpenRead(blobPath);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry(copyData: false) is TarEntry entry)
        {
            entries++;
            string type = entry.EntryType.ToString();
            typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                regularBytes += entry.Length;
            }

            string name = entry.Name;
            if (name.Replace('\\', '/').Equals("UtilityVM/Files", StringComparison.OrdinalIgnoreCase))
            {
                hasUtilityVm = true;
            }

            IReadOnlyDictionary<string, string> pax =
                entry is PaxTarEntry pax1 ? pax1.ExtendedAttributes : new Dictionary<string, string>();
            foreach (string key in pax.Keys)
            {
                paxKeyCounts[key] = paxKeyCounts.GetValueOrDefault(key) + 1;
            }

            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                // A junction and a symlink are both TypeSymlink in the tar; only
                // the MSWINDOWS.mountpoint key tells them apart.
                (pax.ContainsKey("MSWINDOWS.mountpoint") ? junctions : symlinks)
                    .Add($"{name} -> {entry.LinkName}");
            }
            else if (entry.EntryType == TarEntryType.HardLink)
            {
                hardLinks.Add($"{name} -> {entry.LinkName}");
            }
            if (name.Contains(':'))
            {
                alternateStreams.Add(name);
            }
            if (Path.GetFileName(name.Replace('\\', '/')).StartsWith(".wh.", StringComparison.Ordinal))
            {
                whiteouts.Add(name);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--- Inventory: {entries} entries, {regularBytes / (1024 * 1024)} MB of regular-file content ---");
        foreach ((string type, int count) in typeCounts)
        {
            Console.WriteLine($"  {type,-20} {count}");
        }
        Console.WriteLine("--- PAX record keys ---");
        foreach ((string key, int count) in paxKeyCounts)
        {
            Console.WriteLine($"  {key,-32} {count}");
        }
        PrintSample("symlinks (need SeCreateSymbolicLinkPrivilege or Developer Mode)", symlinks, sampleLimit);
        PrintSample("junctions / mount points (unprivileged)", junctions, sampleLimit);
        PrintSample("hard links", hardLinks, sampleLimit);
        PrintSample("alternate data streams", alternateStreams, sampleLimit);
        PrintSample("whiteouts (a base layer must have NONE)", whiteouts, sampleLimit);

        Console.WriteLine();
        Console.WriteLine($"UtilityVM/Files present: {hasUtilityVm} " +
                          $"(decides whether import finalizes with ProcessUtilityImage)");

        // The one shape that would make this image un-importable as a base layer.
        Step("InspectWhiteoutFree", whiteouts.Count == 0 ? default : ProbeFailed,
            whiteouts.Count == 0 ? "no .wh. entries — valid base layer" : $"{whiteouts.Count} whiteout entries");
        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }

    private static void PrintSample(string label, List<string> items, int limit)
    {
        Console.WriteLine($"--- {label}: {items.Count} ---");
        foreach (string item in items.Take(limit))
        {
            Console.WriteLine($"  {item}");
        }
        if (items.Count > limit)
        {
            Console.WriteLine($"  … and {items.Count - limit} more");
        }
    }

    /// <summary>Reads a pull-written metadata file and re-derives the blob path
    /// from the store the CALLER named, so an elevated run cannot silently read
    /// a different profile's store than the one it was pointed at.</summary>
    private static JsonNode ReadImageMetadata(string metadataPath, out string blobPath)
    {
        JsonNode metadata = JsonNode.Parse(File.ReadAllText(metadataPath))
            ?? throw new InvalidOperationException($"{metadataPath} parsed to JSON null");
        string layerDigest = (string?)metadata["layerDigest"]
            ?? throw new InvalidOperationException($"{metadataPath} has no layerDigest");
        // The metadata sits at <store>\images\<ref>.json; its blob is always
        // <store>\blobs\sha256\<hex>. Deriving it from the metadata's own
        // location keeps a moved/copied store self-consistent, where the
        // recorded absolute blobPath would point at the original machine.
        string store = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(metadataPath)))
            ?? throw new InvalidOperationException($"cannot derive store root from {metadataPath}");
        blobPath = Path.Combine(store, "blobs", "sha256", OciDigest.RequireSha256(layerDigest));
        return !File.Exists(blobPath)
            ? throw new InvalidOperationException($"layer blob missing at {blobPath} — rerun `pull` for this image")
            : metadata;
    }
}
