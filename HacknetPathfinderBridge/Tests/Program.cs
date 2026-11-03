using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Freezer;
using Hnpb;

int assertions = 0;
void Check(bool condition, string message)
{ assertions++; if (!condition) throw new Exception("FAIL: " + message); }
void Throws<T>(Action action, string message) where T : Exception
{
    bool caught = false;
    try { action(); } catch (T) { caught = true; }
    Check(caught, message);
}

var budget = new FrameBudget();
Check(!budget.IsPaused && budget.AdmitUpdate() && budget.AdmitDraw(), "normal gate starts open");
budget.SetPaused(true);
Check(!budget.AdmitUpdate() && !budget.AdmitDraw(), "pause blocks both originals");
budget.Advance(2, 1);
Check(budget.IsPaused && budget.IsStepping, "step never clears logical pause");
Throws<InvalidOperationException>(() => budget.Advance(1, 0), "overlapping budgets rejected");
Check(budget.AdmitUpdate() && budget.AdmitUpdate() && !budget.AdmitUpdate(), "exact update budget");
Check(budget.IsStepping, "draw budget remains independent");
Check(budget.AdmitDraw() && !budget.AdmitDraw() && !budget.IsStepping, "exact draw budget and parked state");
budget.NoteUpdateBody(); budget.NoteDrawBody();
Check(budget.Counters()[2] == 1 && budget.Counters()[3] == 1, "body markers independent of admissions");
budget.Advance(200, 0);
int admitted = 0;
Parallel.For(0, 1000, _ => { if (budget.AdmitUpdate()) Interlocked.Increment(ref admitted); });
Check(admitted == 200 && !budget.IsStepping, "atomic permits under concurrency");
budget.Advance(2, 2); budget.SetPaused(true);
Check(!budget.IsStepping && !budget.AdmitUpdate(), "pause cancels outstanding permits");
budget.Toggle(); Check(!budget.IsPaused && budget.AdmitDraw(), "manual toggle resumes");
Throws<InvalidOperationException>(() => budget.Advance(1, 0), "stepping while running rejected");
budget.SetPaused(true);
Throws<ArgumentOutOfRangeException>(() => budget.Advance(0, 0), "empty budgets rejected");
Throws<ArgumentOutOfRangeException>(() => budget.Advance(301, 0), "oversized budgets rejected");
Throws<ArgumentOutOfRangeException>(() => budget.Advance(-1, 1), "negative budgets rejected");

using (var frame = new MemoryStream())
{
    Wire.WriteFrame(frame, "{\"text\":\"中文\"}"); frame.Position = 0;
    Check(Wire.ReadFrame(frame) == "{\"text\":\"中文\"}", "UTF-8 pipe round trip");
}
Throws<EndOfStreamException>(() => Wire.ReadFrame(new MemoryStream(Encoding.UTF8.GetBytes("{}"))), "partial frame rejected");
Throws<InvalidDataException>(() => Wire.ReadFrame(new MemoryStream(Encoding.UTF8.GetBytes("{}\n{}\n"))), "multiple frames rejected");
Throws<DecoderFallbackException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 255, 10 })), "invalid UTF-8 rejected");
Throws<InvalidDataException>(() => Wire.ReadFrame(new MemoryStream(new byte[Wire.MaximumFrameBytes + 1])), "frame size bounded");

var adapter = new FixtureAdapter();
var registration = BridgeAdapters.Register(adapter);
Check(BridgeAdapters.Describe().Length == 1, "custom adapter discovery");
Check(ReferenceEquals(BridgeAdapters.Get(adapter.Id), adapter), "adapter lookup identity");
Throws<ArgumentException>(() => BridgeAdapters.Register(adapter), "duplicate adapter rejected");
Check((int)adapter.Observe(null) == 0, "custom state observation");
adapter.Invoke(null, "increment", new Dictionary<string, object>());
Check((int)adapter.Observe(null) == 1, "custom semantic invocation");
registration.Dispose(); registration.Dispose();
Check(BridgeAdapters.Describe().Length == 0, "adapter unregister is idempotent");
Throws<ArgumentException>(() => BridgeAdapters.Get(adapter.Id), "unloaded adapter cannot be invoked");

string workspace = FindWorkspace();
string host = Path.Combine(workspace, "Output", "HacknetPathfinderBridge", "Host", "HacknetPathfinderBridge.Host.dll");
string registry = Path.Combine(AppContext.BaseDirectory, "test-instances-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(registry);
string instanceId = "hnpb-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
var descriptor = new InstanceDescriptor { InstanceId = instanceId, PipeName = instanceId, ProcessId = Environment.ProcessId, StartedUtc = DateTime.UtcNow.ToString("O") };
File.WriteAllText(Path.Combine(registry, instanceId + ".json"), JsonSerializer.Serialize(descriptor));
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var received = new List<BridgeRequest>();
const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQAAAABJRU5ErkJggg==";
var fake = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        using var pipe = new NamedPipeServerStream(instanceId, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(stop.Token);
        var request = JsonSerializer.Deserialize<BridgeRequest>(Wire.ReadFrame(pipe))!;
        lock (received) received.Add(request);
        var reply = new BridgeReply { Ok = true, RequestId = request.RequestId, Data = new { Paused = true, SessionEpoch = "fixture-epoch", Method = request.Method } };
        if (request.Method == "capture") reply.ImageBase64 = image;
        if (request.Method == "resume" && request.SessionEpoch != "fixture-epoch") { reply.Ok = false; reply.Error = "staleSession"; }
        Wire.WriteFrame(pipe, JsonSerializer.Serialize(reply));
    }
}, stop.Token);

var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
    RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
start.ArgumentList.Add(host); start.ArgumentList.Add("--registry"); start.ArgumentList.Add(registry);
using var process = Process.Start(start)!;
var stderr = process.StandardError.ReadToEndAsync();
int nextId = 0;
async Task<JsonElement> Rpc(string method, object? parameters = null)
{
    int id = ++nextId;
    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } }));
    await process.StandardInput.FlushAsync();
    string line = await process.StandardOutput.ReadLineAsync(stop.Token) ?? throw new Exception("Host terminated: " + await stderr);
    using var json = JsonDocument.Parse(line);
    Check(json.RootElement.GetProperty("id").GetInt32() == id, "response id correlation");
    return json.RootElement.Clone();
}
async Task<JsonElement> Tool(string name, object arguments) => (await Rpc("tools/call", new { name, arguments })).GetProperty("result");

try
{
    Check((await Rpc("tools/list")).GetProperty("error").GetProperty("code").GetInt32() == -32002, "lifecycle enforced before initialization");
    var init = await Rpc("initialize", new { protocolVersion = "2099-01-01", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
    Check(init.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-06-18", "protocol fallback negotiated");
    await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    var tools = (await Rpc("tools/list")).GetProperty("result").GetProperty("tools");
    Check(tools.GetArrayLength() == 6, "stable six-tool surface");
    Check(tools.EnumerateArray().All(t => t.TryGetProperty("inputSchema", out _) && t.TryGetProperty("outputSchema", out _)), "schemas exported");
    var instances = await Tool("hnpb_instances", new { });
    Check(!instances.GetProperty("isError").GetBoolean(), "instances tool success");
    Check(instances.GetProperty("structuredContent").GetProperty("data").GetArrayLength() == 1, "descriptor discovery");
    var state = await Tool("hnpb_state", new { instanceId });
    Check(!state.GetProperty("isError").GetBoolean() && state.GetProperty("structuredContent").GetProperty("data").GetProperty("Paused").GetBoolean(), "MCP to real named pipe round trip");
    var advance = await Tool("hnpb_control", new { instanceId, sessionEpoch = "fixture-epoch", action = "advance", updates = 2, draws = 1 });
    Check(!advance.GetProperty("isError").GetBoolean(), "advance request transported");
    var screenshot = await Tool("hnpb_ui", new { instanceId, sessionEpoch = "fixture-epoch", action = "capture" });
    Check(screenshot.GetProperty("content").EnumerateArray().Any(x => x.GetProperty("type").GetString() == "image" && x.GetProperty("data").GetString() == image), "screenshot is MCP image content");
    Check(!screenshot.GetProperty("structuredContent").GetRawText().Contains(image), "image bytes excluded from structured text");
    var command = await Tool("hnpb_command", new { instanceId, sessionEpoch = "fixture-epoch", command = "ls", requestId = "stable-request" });
    Check(command.GetProperty("structuredContent").GetProperty("requestId").GetString() == "stable-request", "caller-controlled deduplication id preserved");
    var resume = await Tool("hnpb_control", new { instanceId, action = "resume", sessionEpoch = "old" });
    Check(resume.GetProperty("isError").GetBoolean(), "game failure is a tool error");
    var missing = await Tool("hnpb_command", new { instanceId, command = "ls" });
    Check(missing.GetProperty("isError").GetBoolean(), "missing session rejected by schema");
    var oversized = await Tool("hnpb_control", new { instanceId, action = "advance", updates = 301 });
    Check(oversized.GetProperty("isError").GetBoolean(), "oversized budget rejected before transport");
    var wrongType = await Tool("hnpb_control", new { instanceId, action = "advance", updates = true });
    Check(wrongType.GetProperty("isError").GetBoolean(), "wrong argument type rejected");
    var unknown = await Tool("hnpb_state", new { instanceId, arbitrary = true });
    Check(unknown.GetProperty("isError").GetBoolean(), "unknown argument rejected");
    var pathTraversal = await Tool("hnpb_state", new { instanceId = "../arbitrary" });
    Check(pathTraversal.GetProperty("isError").GetBoolean(), "instance path traversal rejected");
    Check((await Rpc("unknown/method")).GetProperty("error").GetProperty("code").GetInt32() == -32601, "unknown RPC method error");
    await process.StandardInput.WriteLineAsync("{\"jsonrpc\":9,\"id\":999,\"method\":\"ping\"}");
    using (var bad = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(stop.Token))!))
        Check(bad.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32600, "malformed envelope cannot crash host");
    Check((await Rpc("ping")).TryGetProperty("result", out _), "host still responsive after invalid request");
    lock (received)
    {
        Check(received.Select(r => r.Method).SequenceEqual(new[] { "observe", "advance", "capture", "command", "resume" }), "tool routing exact, invalid calls never hit game");
        Check(received[1].SessionEpoch == "fixture-epoch" && received[1].Arguments.ContainsKey("updates"), "epoch and budget preserved");
    }
    process.StandardInput.Close();
    await process.WaitForExitAsync(stop.Token);
    Check(process.ExitCode == 0, "MCP host exits cleanly on EOF");
    Console.WriteLine("PASS: " + assertions + " assertions (Freezer permits, framing, real stdio MCP process and named-pipe fixture). Game integration not run.");
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    stop.Cancel();
    try { await fake; } catch (OperationCanceledException) { }
}

static string FindWorkspace()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "HacknetPathfinderBridge.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Workspace root not found.");
}

internal sealed class FixtureAdapter : IBridgeAdapter
{
    private int value;
    public string Id => "tests.fixture";
    public object Describe() => new { Version = 1, Observe = "integer", Operations = new[] { "increment" } };
    public object Observe(object? os) => value;
    public object Invoke(object? os, string operation, Dictionary<string, object> arguments)
    { if (operation != "increment") throw new ArgumentException("Unknown fixture operation."); return ++value; }
}
