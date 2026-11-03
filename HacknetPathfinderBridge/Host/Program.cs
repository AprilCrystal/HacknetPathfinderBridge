using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Hnpb;
using Hnpb.Host;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
string registry = Path.Combine(Path.GetTempPath(), "HacknetPathfinderBridge");
if (args.Length == 2 && args[0] == "--registry") registry = Path.GetFullPath(args[1]);
else if (args.Length != 0) { Console.Error.WriteLine("Usage: HacknetPathfinderBridge.Host [--registry directory]"); return 2; }
var server = new McpServer(registry);
await server.Run();
return 0;

internal sealed class McpServer(string registry)
{
    private readonly PipeClient client = new(registry);
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> calls = new();
    private readonly List<Task> tasks = [];
    private int initialized;

    public async Task Run()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            JsonElement request;
            try
            {
                if (line.Length > 1024 * 1024) throw new JsonException("MCP input size limit exceeded.");
                using var document = JsonDocument.Parse(line); request = document.RootElement.Clone();
            }
            catch (JsonException e) { await WriteError(null, -32700, e.Message); continue; }
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0" ||
                !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            { await WriteError(null, -32600, "Invalid JSON-RPC request."); continue; }
            if (!request.TryGetProperty("id", out var id))
            {
                if (method.GetString() == "notifications/initialized" && initialized == 1) initialized = 2;
                if (method.GetString() == "notifications/cancelled" && request.TryGetProperty("params", out var cancel) && cancel.ValueKind == JsonValueKind.Object &&
                    cancel.TryGetProperty("requestId", out var cancelledId) && calls.TryGetValue(cancelledId.GetRawText(), out var token)) token.Cancel();
                continue;
            }
            if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) { await WriteError(null, -32600, "Invalid request id."); continue; }
            var cancellation = new CancellationTokenSource();
            if (calls.Count >= 8 || !calls.TryAdd(id.GetRawText(), cancellation))
            { cancellation.Dispose(); await WriteError(id, -32000, "Too many concurrent calls or duplicate id."); continue; }
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Handle(request, id, method.GetString()!, cancellation));
        }
        foreach (var entry in calls) entry.Value.Cancel();
        await Task.WhenAll(tasks);
    }

    private async Task Handle(JsonElement request, JsonElement id, string method, CancellationTokenSource cancellation)
    {
        try
        {
            object result;
            switch (method)
            {
                case "initialize":
                    if (initialized != 0) { await WriteError(id, -32600, "Already initialized."); return; }
                    if (!request.TryGetProperty("params", out var init) || !init.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String)
                    { await WriteError(id, -32602, "protocolVersion is required."); return; }
                    initialized = 1;
                    result = new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "HacknetPathfinderBridge", version = "0.1.0" },
                        instructions = "Observe with hnpb_state (pause=true) before acting. Keep the game paused while thinking. capture/activate/fill advance Draw. Original trace is observed; arbitrary mod wall clocks/workers are not frozen. No business completion is inferred from command dispatch." };
                    break;
                case "ping": result = new { }; break;
                case "tools/list":
                    if (initialized != 2) { await WriteError(id, -32002, "Initialization not complete."); return; }
                    result = new { tools = Tools.All.Select(t => t.Definition).ToArray() }; break;
                case "tools/call":
                    if (initialized != 2) { await WriteError(id, -32002, "Initialization not complete."); return; }
                    result = await Call(request, cancellation.Token); break;
                default: await WriteError(id, -32601, "Method not found."); return;
            }
            await Write(new { jsonrpc = "2.0", id, result });
        }
        catch (Exception e) { await WriteError(id, -32603, e.Message); }
        finally { calls.TryRemove(id.GetRawText(), out _); cancellation.Dispose(); }
    }

    private async Task<object> Call(JsonElement request, CancellationToken cancellation)
    {
        try
        {
            var parameters = request.GetProperty("params");
            string name = parameters.GetProperty("name").GetString()!;
            var tool = Tools.All.SingleOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown tool: " + name);
            var arguments = parameters.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
            tool.Validate(arguments);
            BridgeReply reply;
            if (name == "hnpb_instances") reply = new BridgeReply { Ok = true, Data = client.Instances(), RequestId = "local" };
            else
            {
                string action = arguments.TryGetProperty("action", out var selected) ? selected.GetString()! : "";
                string method = name switch
                {
                    "hnpb_state" => "observe", "hnpb_control" => action, "hnpb_command" => "command",
                    "hnpb_ui" => action == "inspect" ? "ui" : action,
                    "hnpb_adapters" => action == "list" ? "adapters" : "adapter." + action,
                    _ => throw new ArgumentException("Unsupported tool.")
                };
                reply = await client.Send(arguments, method, cancellation);
            }
            return ToolResult(reply);
        }
        catch (Exception e)
        {
            return ToolResult(new BridgeReply { Error = e is OperationCanceledException || cancellation.IsCancellationRequested
                ? "Call cancelled/timed out. An accepted game operation is not rolled back; observe before retrying."
                : e.Message });
        }
    }

    private static object ToolResult(BridgeReply reply)
    {
        var data = new { ok = reply.Ok, requestId = reply.RequestId, data = reply.Data, error = reply.Error };
        var content = new List<object> { new { type = "text", text = JsonSerializer.Serialize(data) } };
        if (reply.ImageBase64 != null) content.Add(new { type = "image", data = reply.ImageBase64, mimeType = "image/png" });
        return new { content, structuredContent = data, isError = !reply.Ok };
    }
    private Task WriteError(object? id, int code, string message) => Write(new { jsonrpc = "2.0", id, error = new { code, message } });
    private async Task Write(object response)
    {
        await writer.WaitAsync();
        try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response)); await Console.Out.FlushAsync(); }
        finally { writer.Release(); }
    }
}
