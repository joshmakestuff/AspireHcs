using System.Text.Json.Serialization;

namespace AspireHcs.Hcs.Schema;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ComputeSystemDocument))]
internal sealed partial class HcsJsonContext : JsonSerializerContext;
