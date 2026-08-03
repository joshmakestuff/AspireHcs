// Minimal OCI Distribution (registry v2) client for the image-acquisition spike:
// resolve a tag to a platform manifest, fetch the config blob, and download the
// layer blob with the digest verified while it streams. Anonymous registries
// only (MCR serves Windows base images without a token dance — probed
// 2026-08-02); a 401 is reported as unsupported rather than negotiated.
//
// Every byte consumed is digest-checked against what named it: a manifest
// fetched by digest must hash to that digest, a manifest fetched by tag is
// checked against the registry's Docker-Content-Digest header, and blobs are
// hashed as they stream. Nothing is trusted because a URL said so.

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HcsContainerSpike;

/// <summary>An image reference of the form <c>registry/repository:tag</c> or
/// <c>registry/repository@sha256:hex</c>. The registry host is mandatory — this
/// spike has no docker.io default to inherit surprises from.</summary>
internal sealed record OciImageReference(string Registry, string Repository, string Reference, bool ReferenceIsDigest)
{
    public static OciImageReference Parse(string image)
    {
        int slash = image.IndexOf('/');
        if (slash <= 0 || !image[..slash].Contains('.'))
        {
            throw new ArgumentException(
                $"--image must start with a registry host (e.g. mcr.microsoft.com/windows/nanoserver:ltsc2022), got '{image}'");
        }
        string registry = image[..slash];
        string rest = image[(slash + 1)..];

        int at = rest.IndexOf('@');
        if (at >= 0)
        {
            string repository = rest[..at];
            // Normalized to lowercase at the boundary. RequireSha256 accepts
            // either case, but every later comparison is ordinal against a
            // lowercase computed digest — so an uppercase digest that parsed
            // fine would fail only after the fetch, blaming the registry.
            string digest = "sha256:" + OciDigest.RequireSha256(rest[(at + 1)..]);
            return Validated(registry, repository, digest, referenceIsDigest: true);
        }

        int colon = rest.LastIndexOf(':');
        if (colon <= rest.LastIndexOf('/'))
        {
            throw new ArgumentException($"--image requires an explicit tag or digest, got '{image}'");
        }
        return Validated(registry, rest[..colon], rest[(colon + 1)..], referenceIsDigest: false);
    }

    private static OciImageReference Validated(string registry, string repository, string reference, bool referenceIsDigest)
    {
        // Every component is interpolated into a URL, so the grammar is enforced
        // HERE rather than trusting the value to be inert downstream: a '?', '#',
        // '@' or '..' reaching the request would change the authority, path,
        // query or fragment instead of naming an image. The rules mirror the OCI
        // distribution spec's own character classes, which is what the registry
        // actually enforces.
        if (repository.Length == 0 || reference.Length == 0)
        {
            throw new ArgumentException($"--image has an empty repository or reference ('{registry}', '{repository}', '{reference}')");
        }
        if (!IsValidRegistry(registry))
        {
            throw new ArgumentException($"--image registry '{registry}' is not a bare host[:port]");
        }
        if (!IsValidRepository(repository))
        {
            throw new ArgumentException(
                $"--image repository '{repository}' is not a valid OCI name (lowercase alphanumerics, separators . _ - , '/'-delimited, no '..')");
        }
        if (!referenceIsDigest && !IsValidTag(reference))
        {
            throw new ArgumentException($"--image tag '{reference}' is not a valid OCI tag");
        }
        return new OciImageReference(registry, repository, reference, referenceIsDigest);
    }

    private static bool IsValidRegistry(string registry)
    {
        string host = registry;
        int colon = registry.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!ushort.TryParse(registry[(colon + 1)..], out _))
            {
                return false;
            }
            host = registry[..colon];
        }
        return host.Length > 0
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            && !host.StartsWith('.') && !host.EndsWith('.') && !host.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsValidRepository(string repository)
    {
        if (repository.StartsWith('/') || repository.EndsWith('/') || repository.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (string component in repository.Split('/'))
        {
            if (component.Length == 0
                || component == ".." || component == "."
                || !char.IsAsciiLetterOrDigit(component[0])
                || !char.IsAsciiLetterOrDigit(component[^1])
                || !component.All(c => (char.IsAsciiLetterOrDigit(c) && !char.IsAsciiLetterUpper(c)) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidTag(string tag) =>
        tag.Length <= 128
        && (char.IsAsciiLetterOrDigit(tag[0]) || tag[0] == '_')
        && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    public override string ToString() => $"{Registry}/{Repository}{(ReferenceIsDigest ? '@' : ':')}{Reference}";
}

/// <summary>sha256 digest helpers. Only sha256 is accepted — MCR publishes
/// nothing else, and quietly accepting an unverifiable algorithm would turn
/// "digest-verified" into a label.</summary>
internal static class OciDigest
{
    /// <summary>Validates <c>sha256:&lt;64 hex&gt;</c> and returns the lowercase hex part.</summary>
    public static string RequireSha256(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.Ordinal)
            || digest.Length != prefix.Length + 64
            || !digest.Skip(prefix.Length).All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException($"unsupported or malformed digest '{digest}' — this spike verifies sha256 only");
        }
        return digest[prefix.Length..].ToLowerInvariant();
    }

    public static string Sha256(ReadOnlySpan<byte> data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
}

internal sealed class OciRegistryClient : IDisposable
{
    private const string ManifestAccept =
        "application/vnd.docker.distribution.manifest.list.v2+json, " +
        "application/vnd.oci.image.index.v1+json, " +
        "application/vnd.docker.distribution.manifest.v2+json, " +
        "application/vnd.oci.image.manifest.v1+json";

    private readonly HttpClient _http;

    public OciRegistryClient()
    {
        // Decompression must stay off: a layer blob IS gzip data, and transparent
        // decompression would hand back bytes that no longer hash to the digest.
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            // Per-call budgets come from the caller's CancellationToken; the
            // client-wide timeout would otherwise cap large blob downloads at 100 s.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AspireHcs-HcsContainerSpike/0.1");
    }

    /// <summary>Fetches a manifest (list/index or single) by tag or digest.
    /// Verifies the body hash against the digest when fetching by digest, and
    /// against the Docker-Content-Digest header when fetching by tag.</summary>
    public async Task<(JsonDocumentBytes Manifest, string BodyDigest)> GetManifestAsync(
        OciImageReference image, string reference, CancellationToken ct)
    {
        bool byDigest = reference.StartsWith("sha256:", StringComparison.Ordinal);
        using HttpResponseMessage response = await SendWithOneRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://{image.Registry}/v2/{image.Repository}/manifests/{reference}");
                request.Headers.TryAddWithoutValidation("Accept", ManifestAccept);
                return request;
            },
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"manifest '{reference}'", ct).ConfigureAwait(false);

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string bodyDigest = OciDigest.Sha256(body);

        if (byDigest && !string.Equals(bodyDigest, reference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"manifest fetched by digest does not hash to it: asked {reference}, body is {bodyDigest}");
        }
        if (!byDigest)
        {
            // Fails CLOSED when the header is missing or not sha256. Skipping the
            // check in that case would leave a tag fetch with no binding at all
            // while still being described as digest-verified — the header is
            // present on every MCR response probed (2026-08-02), so its absence
            // is an anomaly worth stopping on rather than shrugging past.
            string? header = response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? advertised)
                ? advertised.FirstOrDefault()
                : null;
            if (header is null || !header.StartsWith("sha256:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"registry did not advertise a sha256 Docker-Content-Digest for tag '{reference}' " +
                    $"(got '{header ?? "(absent)"}') — a tag fetch cannot be bound to a digest without it");
            }
            if (!string.Equals(bodyDigest, header, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"manifest body hashes to {bodyDigest} but the registry advertised Docker-Content-Digest {header}");
            }
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        return (new JsonDocumentBytes(body, mediaType), bodyDigest);
    }

    /// <summary>Fetches a small blob (the image config) fully into memory,
    /// verifying its digest.</summary>
    public async Task<byte[]> GetSmallBlobAsync(OciImageReference image, string digest, int maxBytes, CancellationToken ct)
    {
        OciDigest.RequireSha256(digest);
        // ResponseHeadersRead, NOT ResponseContentRead: buffering the whole body
        // before returning would allocate the very thing the bound below claims
        // to prevent. (Round-1 review added the bound; round 2 caught that the
        // completion option made it decorative.)
        using HttpResponseMessage response = await SendWithOneRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://{image.Registry}/v2/{image.Repository}/blobs/{digest}"),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"blob {digest}", ct).ConfigureAwait(false);

        // Checked BEFORE reading, or the "bound" would only report an allocation
        // that already happened — a limit that cannot prevent what it names.
        if (response.Content.Headers.ContentLength is long advertisedLength && advertisedLength > maxBytes)
        {
            throw new InvalidOperationException(
                $"blob {digest} advertises {advertisedLength} bytes — over the {maxBytes}-byte in-memory bound");
        }
        byte[] body = await ReadBoundedAsync(response, maxBytes, digest, ct).ConfigureAwait(false);
        string actual = OciDigest.Sha256(body);
        return string.Equals(actual, digest, StringComparison.Ordinal)
            ? body
            : throw new InvalidOperationException($"blob digest mismatch: asked {digest}, body is {actual}");
    }

    /// <summary>Streams a blob to <paramref name="destinationPath"/>, hashing as
    /// it goes; the file appears under its final name only after the digest
    /// verified (temp file + rename, same directory so the move is atomic-ish).
    /// An existing destination is re-hashed and kept when it matches — the store
    /// is content-addressed, so a matching file IS the blob.</summary>
    public async Task<string> DownloadBlobToFileAsync(
        OciImageReference image, string digest, string destinationPath, CancellationToken ct)
    {
        OciDigest.RequireSha256(digest); // malformed digests fail before any network I/O

        if (File.Exists(destinationPath))
        {
            string existing = await Sha256OfFileAsync(destinationPath, ct).ConfigureAwait(false);
            if (string.Equals(existing, digest, StringComparison.Ordinal))
            {
                return "cached (existing file re-hashed and verified)";
            }
            // Content-addressed name with the wrong content: a torn earlier write.
            // Keeping it would poison every later consumer, so it goes.
            File.Delete(destinationPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string tempPath = destinationPath + ".partial-" + Environment.ProcessId;
        try
        {
            using HttpResponseMessage response = await SendWithOneRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://{image.Registry}/v2/{image.Repository}/blobs/{digest}"),
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, $"blob {digest}", ct).ConfigureAwait(false);

            long written = 0;
            long lastReport = 0;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (written - lastReport >= 32 << 20)
                    {
                        Console.WriteLine($"[pull]   … {written >> 20} MB");
                        lastReport = written;
                    }
                }

                string actual = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, digest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"blob digest mismatch after {written} bytes: asked {digest}, stream is {actual}");
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return $"downloaded {written} bytes, digest verified";
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>, stopping as soon as
    /// the limit is exceeded — a registry that under-reports (or omits)
    /// Content-Length must not be able to make this allocate without bound.</summary>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response, int maxBytes, string digest, CancellationToken ct)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException($"blob {digest} exceeds the {maxBytes}-byte in-memory bound");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>One retry on transport-level failures and 5xx. A second failure
    /// propagates — a spike that retries forever hides the datum.</summary>
    private async Task<HttpResponseMessage> SendWithOneRetryAsync(
        Func<HttpRequestMessage> requestFactory, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage first = await SendOnceAsync(requestFactory, completion, ct).ConfigureAwait(false);
            if ((int)first.StatusCode < 500)
            {
                return first;
            }
            Console.WriteLine($"[pull] transient HTTP {(int)first.StatusCode}; retrying once");
            first.Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[pull] transient {ex.GetType().Name}: {ex.Message}; retrying once");
        }
        return await SendOnceAsync(requestFactory, completion, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory, HttpCompletionOption completion, CancellationToken ct)
    {
        using HttpRequestMessage request = requestFactory();
        return await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"registry answered 401 for {what} — this spike speaks anonymous pull only (MCR needs no token); " +
                "authenticated registries are out of scope");
        }
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"registry answered HTTP {(int)response.StatusCode} for {what}: {Truncate200(body)}");
    }

    private static string Truncate200(string text)
    {
        string flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Raw manifest bytes plus the Content-Type the registry served them
/// under. The bytes are the unit of digest verification; parsing happens after,
/// on the same bytes.</summary>
internal sealed record JsonDocumentBytes(byte[] Body, string? MediaType);
