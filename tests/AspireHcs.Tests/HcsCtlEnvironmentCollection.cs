using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Serializes every test that touches <c>ASPIREHCS_HCSCTL</c> or runs the real binary. The
/// environment variable is process-wide, and xunit runs collections in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HcsCtlEnvironmentCollection
{
    public const string Name = "hcsctl environment";
}
