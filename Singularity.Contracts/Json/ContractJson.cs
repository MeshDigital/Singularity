using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singularity.Contracts.Json;

/// <summary>
/// The one set of JSON settings every contract is written and read with: camelCase names, enums as
/// strings, nulls omitted. The Python worker (inference/singularity_inference) mirrors these names.
/// </summary>
public static class ContractJson
{
    /// <summary>Settings for files on disk (metadata.json): indented for diffs and hand inspection.</summary>
    public static JsonSerializerOptions File { get; } = Create(indented: true);

    /// <summary>Settings for the JSONL wire protocol: one compact object per line.</summary>
    public static JsonSerializerOptions Wire { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            // The Python side doesn't guarantee the discriminator ("event"/"command") comes first.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
