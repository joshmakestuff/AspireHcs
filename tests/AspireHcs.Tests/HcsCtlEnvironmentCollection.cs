using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Serializes every test that touches an <c>ASPIREHCS_*</c> environment variable or runs the
/// real binary. The environment variables are process-wide, and xunit runs collections in
/// parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HcsCtlEnvironmentCollection
{
    public const string Name = "hcsctl environment";
}
