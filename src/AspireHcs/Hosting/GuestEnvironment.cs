using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace AspireHcs.Hosting;

/// <summary>
/// Resolves a resource's <c>WithEnvironment</c> annotations — <c>WithReference</c> compiles down
/// to these — into name/value pairs. Containers pass them to hcsctl as <c>--env</c>; VMs write
/// them to <c>/etc/aspire.env</c> in the guest.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty value never reaches the guest.</b> HCS and Win32 treat <c>FOO=</c> as a deletion,
/// so the variable is silently <em>absent</em> inside the container rather than present-and-empty
/// — measured in hcsctl, which rejects it outright. This class rejects it too, before anything is
/// attempted, because the failure mode is the worst kind: an app inside the guest reading an unset
/// variable that the AppHost model swears it set.
/// </para>
/// <para>
/// Aspire's own conventions make empty values likely rather than exotic — an unresolved parameter
/// or a not-yet-allocated endpoint reference can produce one — which is exactly why this fails
/// loudly instead of dropping.
/// </para>
/// </remarks>
internal static class GuestEnvironment
{
    /// <summary>
    /// Runs the resource's environment callbacks and resolves every value to a string.
    /// </summary>
    /// <remarks>
    /// A referenced endpoint that is not allocated yet does not fail here: Aspire's value
    /// providers wait for the allocation, so a consumer that boots before DCP has published its
    /// proxies simply resolves late, under the caller's cancellation token.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A value resolved to null or empty.</exception>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        EnvironmentCallbackAnnotation[] annotations = [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()];
        if (annotations.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        EnvironmentCallbackContext context = new(executionContext, resource, cancellationToken: cancellationToken);
        foreach (EnvironmentCallbackAnnotation annotation in annotations)
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }

        Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);
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
        }

        return resolved;
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

        System.Text.StringBuilder file = new();
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

    private static readonly System.Buffers.SearchValues<char> NameBreakers =
        System.Buffers.SearchValues.Create("=\r\n");

    private static readonly System.Buffers.SearchValues<char> LineBreakers =
        System.Buffers.SearchValues.Create("\r\n");

    /// <summary>
    /// Aspire's callbacks put strings, parameters, endpoint references and expressions into the
    /// same dictionary. Everything that can resolve itself is asked to; anything else is
    /// formatted, and a value that formats to nothing is caught by the empty check above rather
    /// than passed through as the string "".
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
