using System.Buffers;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace AspireHcs.Hosting;

/// <summary>
/// A resource's resolved environment, with the provenance the loopback redirect needs: which
/// values carry endpoints of other resources (from <c>WithReference</c>, which compiles down to
/// environment callbacks), and which values came from providers whose contents could not be
/// classified. A plain string set through <c>WithEnvironment</c> appears in neither — it is the
/// user's literal, and nothing downstream may touch it.
/// </summary>
internal sealed record ResolvedGuestEnvironment(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<GuestEndpointOccurrence> Occurrences,
    IReadOnlySet<string> OpaqueNames);

/// <summary>How an endpoint materializes inside one environment value.</summary>
internal enum EndpointOccurrenceKind
{
    /// <summary>The value is the endpoint's host and nothing else (a split <c>_HOST</c> half).</summary>
    HostOnly,

    /// <summary>The value is the endpoint's port and nothing else (a split <c>_PORT</c> half).</summary>
    PortOnly,

    /// <summary>The endpoint appears as <c>host:port</c> inside a larger value (URL, connection string).</summary>
    Embedded,
}

/// <summary>One endpoint of another resource, as it appears in one environment value.</summary>
/// <param name="Name">The environment variable the endpoint appears in.</param>
/// <param name="Kind">How the endpoint materializes there.</param>
/// <param name="Host">The endpoint's host from the host's perspective — <c>localhost</c> for a DCP proxy.</param>
/// <param name="Port">The endpoint's port from the host's perspective — the DCP proxy port.</param>
internal sealed record GuestEndpointOccurrence(string Name, EndpointOccurrenceKind Kind, string Host, int Port);

/// <summary>
/// Resolves a resource's <c>WithEnvironment</c> annotations — <c>WithReference</c> compiles down
/// to these — into name/value pairs. Containers pass them to hcsctl as <c>--env</c>; VMs write
/// them to <c>/etc/aspire.env</c> in the guest.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty value never reaches the guest.</b> HCS and Win32 treat <c>FOO=</c> as a deletion:
/// the variable is <em>absent</em> inside the container, not present-and-empty. hcsctl rejects an
/// empty value; this class rejects it before hcsctl is invoked.
/// </para>
/// <para>
/// Empty values are common in Aspire: an unresolved parameter or a not-yet-allocated endpoint
/// reference can produce one.
/// </para>
/// </remarks>
internal static class GuestEnvironment
{
    /// <summary>
    /// Runs the resource's environment callbacks and resolves every value to a string, recording
    /// per value which endpoints of other resources it carries.
    /// </summary>
    /// <remarks>
    /// Provenance is read from the callback's value <em>objects</em>, before they are asked for
    /// strings: an <see cref="EndpointReference"/> or an expression over one is a reference to
    /// another resource, a plain string is the user's literal, and only the former may later be
    /// rewritten to a guest-reachable address. A provider that is neither — and declares no
    /// references to walk — lands in <see cref="ResolvedGuestEnvironment.OpaqueNames"/>, where
    /// the redirect falls back to matching the resolved text.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A value resolved to null or empty.</exception>
    public static async Task<ResolvedGuestEnvironment> ResolveAsync(
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);
        List<GuestEndpointOccurrence> occurrences = [];
        HashSet<string> opaque = new(StringComparer.OrdinalIgnoreCase);

        EnvironmentCallbackAnnotation[] annotations = [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()];
        if (annotations.Length == 0)
        {
            return new(resolved, occurrences, opaque);
        }

        EnvironmentCallbackContext context = new(executionContext, resource, cancellationToken: cancellationToken);
        foreach (EnvironmentCallbackAnnotation annotation in annotations)
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }

        foreach ((string name, object? value) in context.EnvironmentVariables)
        {
            string? text = await ResolveValueAsync(value, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(text))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{name}' on resource '{resource.Name}' resolved to an empty value. " +
                    "HCS treats an empty value as a deletion, so it would be absent inside the container rather " +
                    "than present-and-empty — an app reading it would see nothing set. Give it a value, or drop " +
                    "the WithEnvironment call. If it came from a parameter or an endpoint reference, that source " +
                    "has not resolved.");
            }

            resolved[name] = text;

            // The last write wins in the dictionary, so the last write's provenance must win too.
            occurrences.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            opaque.Remove(name);
            Classify(name, value, occurrences, opaque, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
        }

        return new(resolved, occurrences, opaque);
    }

    /// <summary>
    /// Walks one callback value's object graph for endpoints of other resources. Strings and
    /// parameters are the user's own inputs and contribute nothing; a provider that cannot be
    /// classified marks the variable opaque instead of guessing.
    /// </summary>
    private static void Classify(
        string name, object? value, List<GuestEndpointOccurrence> occurrences, HashSet<string> opaque,
        HashSet<object> visited, int depth)
    {
        // A cycle cannot be built through Aspire's expression factories, but a custom provider
        // could hand one over; the guard turns that into an opaque variable, not a hang.
        if (value is null || depth > 8 || !visited.Add(value))
        {
            return;
        }

        switch (value)
        {
            case string:
            case ParameterResource:
                // The user's literal or the user's parameter. Even one that spells a loopback
                // endpoint is configuration meant as written — never a rewrite candidate.
                return;

            case EndpointReference endpoint:
                AddOccurrence(name, EndpointOccurrenceKind.Embedded, endpoint, occurrences, opaque);
                return;

            case EndpointReferenceExpression expression:
                EndpointOccurrenceKind? kind = expression.Property switch
                {
                    EndpointProperty.Host or EndpointProperty.IPV4Host => EndpointOccurrenceKind.HostOnly,
                    EndpointProperty.Port => EndpointOccurrenceKind.PortOnly,
                    EndpointProperty.Url or EndpointProperty.HostAndPort => EndpointOccurrenceKind.Embedded,
                    // Scheme, TargetPort, TlsEnabled: no host-perspective address content.
                    _ => null,
                };
                if (kind is { } k)
                {
                    AddOccurrence(name, k, expression.Endpoint, occurrences, opaque);
                }
                return;

            case ReferenceExpression reference:
                foreach (IValueProvider provider in reference.ValueProviders)
                {
                    Classify(name, provider, occurrences, opaque, visited, depth + 1);
                }
                return;

            case ConnectionStringReference connectionString:
                Classify(name, connectionString.Resource.ConnectionStringExpression, occurrences, opaque, visited, depth + 1);
                return;

            case IResourceWithConnectionString resource:
                Classify(name, resource.ConnectionStringExpression, occurrences, opaque, visited, depth + 1);
                return;

            case IValueWithReferences composite:
                foreach (object reference in composite.References)
                {
                    Classify(name, reference, occurrences, opaque, visited, depth + 1);
                }
                return;

            case IValueProvider:
                // Resolves to text this walk cannot see through. The redirect falls back to
                // matching the resolved value for this variable only.
                opaque.Add(name);
                return;

            default:
                // Formatted via ToString by the resolver: a number, a bool — nothing addressable.
                return;
        }
    }

    private static void AddOccurrence(
        string name, EndpointOccurrenceKind kind, EndpointReference endpoint,
        List<GuestEndpointOccurrence> occurrences, HashSet<string> opaque)
    {
        // The value's string already resolved, so a referenced endpoint is allocated by now; an
        // unallocated one here means a provider resolved without waiting, and the resolved text
        // is all there is to go on.
        if (!endpoint.IsAllocated)
        {
            opaque.Add(name);
            return;
        }

        occurrences.Add(new(name, kind, endpoint.Host, endpoint.Port));
    }

    /// <summary>
    /// Renders the resolved environment as an env file — one <c>NAME=value</c> line per variable,
    /// the format <c>/etc/aspire.env</c> promises a VM guest's workload.
    /// </summary>
    /// <remarks>
    /// A line-oriented format cannot carry line breaks, and a name containing <c>=</c> would split
    /// wrong on read. Both are rejected here, by name, rather than silently writing a file whose
    /// reader would see different variables than the model set — the same honesty rule as the
    /// empty-value check above.
    /// </remarks>
    public static string BuildEnvFile(string resourceName, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        StringBuilder file = new();
        foreach ((string name, string value) in environment)
        {
            if (name.AsSpan().ContainsAny(NameBreakers))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{name}' on resource '{resourceName}' has a name an env file " +
                    "cannot carry: '=' or a line break would change which variables the guest reads back.");
            }

            if (value.AsSpan().ContainsAny(LineBreakers))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{name}' on resource '{resourceName}' has a value containing a " +
                    "line break, which /etc/aspire.env cannot carry: each line is one variable, so the guest " +
                    "would read a truncated value plus stray lines.");
            }

            file.Append(name).Append('=').Append(value).Append('\n');
        }

        return file.ToString();
    }

    private static readonly SearchValues<char> NameBreakers = SearchValues.Create("=\r\n");

    private static readonly SearchValues<char> LineBreakers = SearchValues.Create("\r\n");

    /// <summary>
    /// Aspire's callbacks put strings, parameters, endpoint references and expressions into the
    /// same dictionary. Everything that can resolve itself is asked to; anything else is
    /// formatted. The caller rejects an empty result.
    /// </summary>
    private static async Task<string?> ResolveValueAsync(object? value, CancellationToken cancellationToken)
        => value switch
        {
            null => null,
            string text => text,
            IValueProvider provider => await provider.GetValueAsync(cancellationToken).ConfigureAwait(false),
            _ => value.ToString(),
        };
}
