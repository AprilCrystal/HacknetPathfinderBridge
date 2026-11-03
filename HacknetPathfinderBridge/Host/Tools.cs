using System.Text.Json;

namespace Hnpb.Host;

internal sealed class Tool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Required { get; init; } = [];
    public Dictionary<string, object> Properties { get; init; } = [];
    public object Schema => new { type = "object", properties = Properties, required = Required, additionalProperties = false };
    public object Definition => new { name = Name, description = Description, inputSchema = Schema,
        outputSchema = new { type = "object", required = new[] { "ok", "data" }, properties = new { ok = new { type = "boolean" }, data = new { } } } };

    public void Validate(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
        foreach (string required in Required)
            if (!args.TryGetProperty(required, out _)) throw new ArgumentException("Missing argument: " + required);
        foreach (var property in args.EnumerateObject())
        {
            if (!Properties.TryGetValue(property.Name, out var schema)) throw new ArgumentException("Unknown argument: " + property.Name);
            var spec = JsonSerializer.SerializeToElement(schema);
            string type = spec.GetProperty("type").GetString()!;
            bool correct = type switch
            {
                "string" => property.Value.ValueKind == JsonValueKind.String,
                "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out _),
                "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => property.Value.ValueKind == JsonValueKind.Object,
                _ => false
            };
            if (!correct) throw new ArgumentException(property.Name + " must be " + type);
            if (type == "string")
            {
                string value = property.Value.GetString()!;
                if (spec.TryGetProperty("maxLength", out var maximum) && value.Length > maximum.GetInt32()) throw new ArgumentException(property.Name + " is too long.");
                if (spec.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(x => x.GetString() == value)) throw new ArgumentException("Invalid " + property.Name);
            }
            if (type == "integer")
            {
                int value = property.Value.GetInt32();
                if (spec.TryGetProperty("minimum", out var min) && value < min.GetInt32() || spec.TryGetProperty("maximum", out var max) && value > max.GetInt32())
                    throw new ArgumentException(property.Name + " is outside the permitted range.");
            }
        }
    }
}

internal static class Tools
{
    private static object Str(int maximum = 256) => new { type = "string", maxLength = maximum };
    private static object Choice(params string[] values) => new { type = "string", @enum = values };
    private static object Count(int max) => new { type = "integer", minimum = 0, maximum = max };
    private static Dictionary<string, object> Args(params (string Key, object Schema)[] values)
    {
        var result = new Dictionary<string, object> { ["instanceId"] = Str(), ["sessionEpoch"] = Str(), ["requestId"] = Str(128) };
        foreach (var item in values) result[item.Key] = item.Schema;
        return result;
    }
    public static readonly Tool[] All =
    [
        new() { Name = "hnpb_instances", Description = "List advertised game instances; does not pause or start a game. A descriptor may be stale.", Properties = [] },
        new() { Name = "hnpb_state", Description = "Read terminal, original trace, nodes and pause state at a game-thread boundary. Pauses by default so AI thinking does not advance normal gameplay. Background workers/wall clocks remain uncontrolled.", Required = ["instanceId"], Properties = Args(("pause", new { type = "boolean" })) },
        new() { Name = "hnpb_control", Description = "Pause, resume, or advance independent Update/Draw budgets (0..300 each) using normal host timing; advance automatically remains paused. Not virtual time. Resume/advance require the observed sessionEpoch.", Required = ["instanceId", "action"], Properties = Args(("action", Choice("pause", "resume", "advance")), ("updates", Count(300)), ("draws", Count(300))) },
        new() { Name = "hnpb_command", Description = "While paused, dispatch one terminal command via the game's synchronous semantic terminal path. Does not emulate typing. Dispatch completion does not mean spawned programs/actions have finished. Requires sessionEpoch.", Required = ["instanceId", "sessionEpoch", "command"], Properties = Args(("command", Str(2048))) },
        new() { Name = "hnpb_ui", Description = "Inspect cached common controls; capture advances one Draw and returns PNG+controls; activate clicks a button or submits a terminal text field; fill replaces input without submitting. Each mutation runs during one Draw. Not mouse emulation. Self-drawn controls need adapters. Mutations require sessionEpoch; activate/fill also require key and revision from the latest UI.", Required = ["instanceId", "action"], Properties = Args(("action", Choice("inspect", "capture", "activate", "fill")), ("revision", Count(int.MaxValue)), ("key", Str()), ("text", Str(4096))) },
        new() { Name = "hnpb_adapters", Description = "Discover, observe or invoke explicitly registered mod adapters. Adapter schemas describe custom content. Invocations require a paused game and sessionEpoch; unknown mods are not automatically introspected.", Required = ["instanceId", "action"], Properties = Args(("action", Choice("list", "observe", "invoke")), ("adapterId", Str()), ("operation", Str()), ("arguments", new { type = "object" })) }
    ];
}
