using System.Text.Json;
using Singularity.Contracts.Json;

namespace Singularity.Contracts.Inference;

/// <summary>Line-level encoding of the worker protocol (see <see cref="WorkerCommand"/> / <see cref="WorkerEvent"/>).</summary>
public static class WorkerProtocol
{
    /// <summary>Bumped on breaking changes; the app refuses a worker whose <see cref="ReadyEvent.ProtocolVersion"/> differs.</summary>
    public const int Version = 1;

    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "ready", "stage_started", "progress_update", "stage_completed", "task_finished", "log",
    };

    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        "process_track", "separate_stems", "cancel", "shutdown",
    };

    /// <summary>One JSONL line, without the trailing newline.</summary>
    public static string Encode(WorkerCommand command) => JsonSerializer.Serialize(command, ContractJson.Wire);

    public static string Encode(WorkerEvent evt)
    {
        if (evt is UnknownWorkerEvent) throw new ArgumentException("Unknown events are read-only.", nameof(evt));
        return JsonSerializer.Serialize(evt, ContractJson.Wire);
    }

    /// <summary>
    /// Parses one stdout line. Returns null for blank lines. A well-formed object with an event name
    /// this build doesn't know becomes <see cref="UnknownWorkerEvent"/> so a newer worker can't crash the app.
    /// </summary>
    /// <exception cref="JsonException">The line isn't a JSON object with an "event" string, or a known event is malformed.</exception>
    public static WorkerEvent? DecodeEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var name = ReadDiscriminator(line, "event");
        if (!KnownEvents.Contains(name)) return new UnknownWorkerEvent(name, line);
        return JsonSerializer.Deserialize<WorkerEvent>(line, ContractJson.Wire)
               ?? throw new JsonException("Event line deserialized to null.");
    }

    /// <exception cref="JsonException">The line isn't a known command.</exception>
    public static WorkerCommand DecodeCommand(string line)
    {
        var name = ReadDiscriminator(line, "command");
        if (!KnownCommands.Contains(name)) throw new JsonException($"Unknown command '{name}'.");
        return JsonSerializer.Deserialize<WorkerCommand>(line, ContractJson.Wire)
               ?? throw new JsonException("Command line deserialized to null.");
    }

    private static string ReadDiscriminator(string line, string property)
    {
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Expected a JSON object, got {doc.RootElement.ValueKind}.");
        if (!doc.RootElement.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Missing \"{property}\" discriminator.");
        return value.GetString()!;
    }
}
