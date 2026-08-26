namespace AspireHcs;

/// <summary>
/// Environment overrides for where AspireHcs reads and writes on disk, following the
/// <c>ASPIREHCS_HCSCTL</c> precedent: an explicit builder value wins, then the environment
/// variable, then the built-in default.
/// </summary>
internal static class AspireHcsEnvironment
{
    /// <summary>
    /// The hcsctl store for resources that do not name one via <c>WithStore</c> /
    /// <c>WithHcsCtl</c>. Unset means hcsctl's per-user default store.
    /// </summary>
    internal const string StoreVariable = "ASPIREHCS_STORE";

    /// <summary>
    /// Base directory for AspireHcs's temporary files. Unset means <c>AspireHcs</c> under the
    /// system temp directory.
    /// </summary>
    internal const string TempVariable = "ASPIREHCS_TEMP";

    /// <summary>The store to fall back to when a resource has none configured.</summary>
    internal static string? DefaultStorePath
        => ResolveStore(Environment.GetEnvironmentVariable(StoreVariable));

    /// <summary>The base directory for temporary files.</summary>
    internal static string TempDirectory
        => ResolveTemp(Environment.GetEnvironmentVariable(TempVariable));

    internal static string? ResolveStore(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());

    internal static string ResolveTemp(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Path.Combine(Path.GetTempPath(), "AspireHcs")
            : Path.GetFullPath(value.Trim());
}
