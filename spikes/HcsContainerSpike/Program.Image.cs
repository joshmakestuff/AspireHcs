// Image acquisition (issue #30): `pull` resolves a Windows container base image
// on an anonymous OCI registry to its single layer blob and materializes the
// blob + metadata into the AspireHcs-owned store, digest-verified end to end.
// `import` (added by the same spike) turns that blob into a bootable
// windowsfilter-format layer directory.
//
// Scope guards are loud by design: multi-layer images (servercore) need the
// chain-import transport format and are refused with the follow-up named, and
// foreign/urls-bearing layers are refused rather than silently fetched from
// wherever a manifest points.

using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static partial class Program
{
    private const string DockerManifestListType = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string OciIndexType = "application/vnd.oci.image.index.v1+json";
    private static readonly string[] SupportedLayerTypes =
    [
        "application/vnd.docker.image.rootfs.diff.tar.gzip",
        "application/vnd.oci.image.layer.v1.tar+gzip",
    ];

    // ------------------------------------------------------------------ pull --

    private static int Pull(string[] args)
    {
        string imageArg = Opt(args, "--image")
            ?? throw new ArgumentException("--image <registry/repository:tag> is required");
        // Parse throws ArgumentException on a malformed ref, which Main converts
        // to usage — the same contract as every other mode's argument errors.
        OciImageReference image = OciImageReference.Parse(imageArg);
        string store = Path.TrimEndingDirectorySeparator(Opt(args, "--store") ?? DefaultStoreRoot());
        int budgetSeconds = OptInt(args, "--seconds", 600);

        Console.WriteLine($"[pull] image={image}");
        Console.WriteLine($"[pull] store={store}");

        return PullAsync(image, store, TimeSpan.FromSeconds(budgetSeconds)).GetAwaiter().GetResult();
    }

    private static async Task<int> PullAsync(OciImageReference image, string store, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        using var client = new OciRegistryClient();
        string stage = "FetchManifest";
        try
        {
            // 1. Manifest by tag (or digest), hash-checked by the client.
            (JsonDocumentBytes manifest, string manifestDigest) =
                await client.GetManifestAsync(image, image.Reference, cts.Token).ConfigureAwait(false);
            JsonNode root = ParseManifest(manifest, manifestDigest);
            Step("FetchManifest", default, $"{manifest.MediaType ?? "(no media type)"} digest={manifestDigest}");

            // 2. If it is an index/list, select exactly one windows/amd64 entry
            //    and fetch the platform manifest it names.
            string? manifestListDigest = null;
            string? platformOsVersion = null;
            if (IsIndex(manifest.MediaType, root))
            {
                stage = "SelectPlatform";
                manifestListDigest = manifestDigest;
                (string platformDigest, platformOsVersion) = SelectWindowsAmd64(root);
                Step("SelectPlatform(windows/amd64)", default, $"os.version={platformOsVersion} digest={platformDigest}");

                stage = "FetchPlatformManifest";
                (manifest, manifestDigest) =
                    await client.GetManifestAsync(image, platformDigest, cts.Token).ConfigureAwait(false);
                root = ParseManifest(manifest, manifestDigest);
                Step("FetchPlatformManifest", default, $"{manifest.MediaType ?? "(no media type)"} digest={manifestDigest}");
            }

            // 3. Exactly one supported, non-foreign layer.
            stage = "CheckLayerSupported";
            (string layerDigest, string layerMediaType, long layerSize) = RequireSingleSupportedLayer(root);
            Step("CheckLayerSupported", default, $"{layerMediaType} {layerSize / (1024 * 1024)} MB {layerDigest}");

            // 4. Config blob: os.version and the expected diffID.
            stage = "FetchConfig";
            string configDigest = RequireString(root["config"]?["digest"], "config.digest");
            byte[] configBytes = await client.GetSmallBlobAsync(image, configDigest, maxBytes: 4 << 20, cts.Token).ConfigureAwait(false);
            JsonNode config = JsonNode.Parse(configBytes) ?? throw new InvalidOperationException("config blob parsed to JSON null");
            string expectedDiffId = RequireDiffId(config);
            string? configOsVersion = (string?)config["os.version"];
            Step("FetchConfig", default, $"digest={configDigest} os.version={configOsVersion ?? "(absent)"} diffID={expectedDiffId}");

            // 5. The layer blob itself, streamed and digest-verified.
            stage = "DownloadLayer";
            string blobPath = Path.Combine(store, "blobs", "sha256", OciDigest.RequireSha256(layerDigest));
            string outcome = await client.DownloadBlobToFileAsync(image, layerDigest, blobPath, cts.Token).ConfigureAwait(false);
            Step("DownloadLayer", default, $"{outcome} → {blobPath}");

            // 6. Metadata: the import handshake. The layer digest is the
            //    authoritative pointer (import re-derives the blob path from its
            //    own --store); blobPath is recorded for humans and diagnostics.
            stage = "WriteMetadata";
            string metadataPath = MetadataPath(store, image);
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            var metadata = new JsonObject
            {
                ["image"] = image.ToString(),
                ["registry"] = image.Registry,
                ["repository"] = image.Repository,
                ["reference"] = image.Reference,
                ["manifestListDigest"] = manifestListDigest,
                ["manifestDigest"] = manifestDigest,
                ["configDigest"] = configDigest,
                ["osVersion"] = configOsVersion ?? platformOsVersion,
                ["layerDigest"] = layerDigest,
                ["layerMediaType"] = layerMediaType,
                ["layerCompressedSize"] = layerSize,
                ["expectedDiffId"] = expectedDiffId,
                ["blobPath"] = blobPath,
                ["pulledUtc"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(metadataPath, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Step("WriteMetadata", default, metadataPath);

            Console.WriteLine();
            Console.WriteLine($"Pulled. Next: HcsContainerSpike import --metadata {metadataPath}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException
                                       or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            string detail = ex is OperationCanceledException && cts.IsCancellationRequested
                ? $"budget of {budget.TotalSeconds:0} s exhausted (--seconds raises it)"
                : ex.Message;
            Step(stage, MapManagedFailure(ex), detail);
            return 2;
        }
    }

    private static JsonNode ParseManifest(JsonDocumentBytes manifest, string digest)
    {
        JsonNode root = JsonNode.Parse(manifest.Body)
            ?? throw new InvalidOperationException($"manifest {digest} parsed to JSON null");
        // Schema1 has no config/layers and its "digest" is a signature-stripped
        // fiction; refusing it beats misreading it. MCR only serves it when the
        // Accept headers let it, which ours do not — this is a guard, not a path.
        bool schema1 = manifest.MediaType?.Contains("manifest.v1", StringComparison.Ordinal) == true
            || root["fsLayers"] is not null
            || (int?)root["schemaVersion"] == 1;
        return schema1
            ? throw new InvalidOperationException("registry served a schema1 manifest — unsupported; check the Accept headers reached it")
            : root;
    }

    private static bool IsIndex(string? mediaType, JsonNode root) =>
        mediaType is DockerManifestListType or OciIndexType || root["manifests"] is JsonArray;

    private static (string Digest, string? OsVersion) SelectWindowsAmd64(JsonNode index)
    {
        if (index["manifests"] is not JsonArray entries)
        {
            throw new InvalidOperationException("manifest list has no 'manifests' array");
        }
        List<(string Digest, string? OsVersion)> windows = [];
        var seen = new List<string>();
        foreach (JsonNode? entry in entries)
        {
            string os = (string?)entry?["platform"]?["os"] ?? "(none)";
            string arch = (string?)entry?["platform"]?["architecture"] ?? "(none)";
            string? osVersion = (string?)entry?["platform"]?["os.version"];
            seen.Add($"{os}/{arch}{(osVersion is null ? "" : $" {osVersion}")}");
            if (os == "windows" && arch == "amd64")
            {
                windows.Add((RequireString(entry?["digest"], "platform manifest digest"), osVersion));
            }
        }
        // Exactly one, or fail loud with the inventory: guessing among multiple
        // windows/amd64 entries (multi-os.version tags exist) would silently pin
        // a build nobody chose.
        return windows.Count == 1
            ? windows[0]
            : throw new InvalidOperationException(
                $"expected exactly one windows/amd64 entry, found {windows.Count} (platforms: {string.Join(", ", seen)}); " +
                "pull by digest to disambiguate");
    }

    private static (string Digest, string MediaType, long Size) RequireSingleSupportedLayer(JsonNode manifest)
    {
        if (manifest["layers"] is not JsonArray layers || layers.Count == 0)
        {
            throw new InvalidOperationException("platform manifest has no layers");
        }
        if (layers.Count != 1)
        {
            throw new InvalidOperationException(
                $"image has {layers.Count} layers — multi-layer (chain) import is out of this spike's scope " +
                "(servercore is the known case; the follow-up is chain import via the legacy ImportLayer transport, tracked in #30)");
        }
        JsonNode layer = layers[0]!;
        string mediaType = RequireString(layer["mediaType"], "layer mediaType");
        string digest = RequireString(layer["digest"], "layer digest");
        long size = (long?)layer["size"] ?? -1;
        if (!SupportedLayerTypes.Contains(mediaType, StringComparer.Ordinal))
        {
            // Foreign/nondistributable layers point elsewhere via `urls`; MCR
            // retired that scheme (probed 2026-08-02), so hitting one means the
            // image is not what this spike thinks it is.
            throw new InvalidOperationException($"unsupported layer mediaType '{mediaType}' — expected one of: {string.Join(", ", SupportedLayerTypes)}");
        }
        if (layer["urls"] is JsonArray urls && urls.Count > 0)
        {
            throw new InvalidOperationException("layer carries 'urls' (foreign layer indirection) — unsupported");
        }
        return (digest, mediaType, size);
    }

    private static string RequireDiffId(JsonNode config)
    {
        if (config["rootfs"]?["diff_ids"] is not JsonArray diffIds || diffIds.Count == 0)
        {
            throw new InvalidOperationException("config has no rootfs.diff_ids");
        }
        return diffIds.Count == 1
            ? RequireString(diffIds[0], "rootfs.diff_ids[0]")
            : throw new InvalidOperationException($"config lists {diffIds.Count} diff_ids for a single-layer manifest — refusing the inconsistency");
    }

    private static string RequireString(JsonNode? node, string what) =>
        (string?)node ?? throw new InvalidOperationException($"manifest is missing {what}");

    /// <summary>Metadata file keyed by the image reference, filename-sanitized.
    /// Content-addressed naming would be wrong here: two refs can name one blob,
    /// and the ref is what a human asks for again.
    ///
    /// Sanitizing alone is NOT injective — `nanoserver:foo_bar` and
    /// `nanoserver_foo:bar` both collapse to the same characters — so a short
    /// hash of the exact reference is appended. Two different images can then
    /// never overwrite each other's metadata, while the name stays readable.</summary>
    private static string MetadataPath(string store, OciImageReference image)
    {
        string reference = image.ToString();
        string key = $"{image.Registry}_{image.Repository}_{image.Reference}";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            key = key.Replace(c, '_');
        }
        string discriminator = OciDigest.Sha256(System.Text.Encoding.UTF8.GetBytes(reference))["sha256:".Length..][..8];
        return Path.Combine(store, "images", $"{key}-{discriminator}.json");
    }

    /// <summary>Managed exceptions carry the real Win32/HTTP story in HResult
    /// only sometimes; default to the spike's locally-judged-failure sentinel
    /// when the HResult is the generic COR one.</summary>
    private static HRESULT MapManagedFailure(Exception ex) =>
        (uint)ex.HResult is >= 0x80070000 and <= 0x8007FFFF ? new HRESULT(ex.HResult) : ProbeFailed;
}
