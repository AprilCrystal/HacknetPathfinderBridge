using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Text;

namespace Hnpb;

internal sealed class Pending
{
    public readonly BridgeRequest Request;
    public readonly TaskCompletionSource<BridgeReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly long Deadline;
    public volatile bool Expired;
    public Pending(BridgeRequest request)
    {
        Request = request;
        Deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (request.TimeoutMs / 1000.0));
    }
    public bool IsExpired => Expired || Stopwatch.GetTimestamp() >= Deadline;
    public void Reply(object? data = null, string? error = null, string? image = null)
    {
        // Detach provider results while still on the game thread, before a pipe worker serializes them.
        object? detached = null;
        if (data != null)
        {
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                string serialized = json.Serialize(data);
                if (Encoding.UTF8.GetByteCount(serialized) > 1024 * 1024) throw new InvalidDataException("DTO exceeds 1 MiB UTF-8 limit.");
                detached = json.DeserializeObject(serialized);
            }
            catch (Exception e) { error = "Result could not be converted to bounded DTOs: " + e.Message; }
        }
        Completion.TrySetResult(new BridgeReply { Ok = error == null, RequestId = Request.RequestId, Data = detached, Error = error, ImageBase64 = image });
    }
}

internal sealed class BridgeServer : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<Pending> queue = new();
    private readonly Dictionary<string, Pending> requests = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private NamedPipeServerStream? currentPipe;
    private readonly string descriptorPath;
    private readonly string pipeName;
    private readonly Action<string> log;
    public string InstanceId { get; }
    public string RegistryDirectory { get; }
    public Task? Worker { get; private set; }

    public BridgeServer(Action<string> log, string? registryDirectory = null)
    {
        this.log = log;
        InstanceId = "hnpb-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
        pipeName = InstanceId;
        RegistryDirectory = registryDirectory ?? Path.Combine(Path.GetTempPath(), "HacknetPathfinderBridge");
        Directory.CreateDirectory(RegistryDirectory);
        descriptorPath = Path.Combine(RegistryDirectory, InstanceId + ".json");
    }

    public void Start()
    {
        // Create the ACL-protected endpoint before advertising the instance.
        currentPipe = MakePipe();
        var descriptor = new InstanceDescriptor { InstanceId = InstanceId, PipeName = pipeName,
            ProcessId = Process.GetCurrentProcess().Id, GameDirectory = AppDomain.CurrentDomain.BaseDirectory,
            StartedUtc = DateTime.UtcNow.ToString("O") };
        File.WriteAllText(descriptorPath, new JavaScriptSerializer().Serialize(descriptor));
        Worker = Task.Run(Listen);
    }

    private NamedPipeServerStream MakePipe()
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user SID unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 65536, security);
    }

    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var pipe = currentPipe!;
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                // Enforce a deadline even for a client that connects but never sends a frame.
                using (var timeout = new CancellationTokenSource(15000))
                using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = Wire.MaximumFrameBytes };
                    var request = serializer.Deserialize<BridgeRequest>(Wire.ReadFrame(pipe));
                    BridgeReply reply;
                    if (request == null || request.Protocol != 1 || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
                        request.Arguments == null || request.SessionEpoch == null || string.IsNullOrWhiteSpace(request.Method) ||
                        request.TimeoutMs < 100 || request.TimeoutMs > 12000)
                        reply = new BridgeReply { Error = "Invalid bridge request." };
                    else
                    {
                        Pending pending;
                        lock (sync)
                        {
                            if (requests.TryGetValue(request.RequestId, out pending!))
                            {
                                if (serializer.Serialize(pending.Request) != serializer.Serialize(request))
                                    throw new InvalidDataException("RequestId reused with different arguments.");
                            }
                            else
                            {
                                if (queue.Count >= 32 || requests.Count >= 4096)
                                    throw new InvalidOperationException("Request capacity reached; reconnect/reload the bridge before issuing more operations.");
                                pending = new Pending(request); requests.Add(request.RequestId, pending); queue.Enqueue(pending);
                            }
                        }
                        var completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(request.TimeoutMs, stop.Token)).ConfigureAwait(false);
                        if (completed == pending.Completion.Task) reply = await pending.Completion.Task.ConfigureAwait(false);
                        else
                        {
                            pending.Expired = true;
                            pending.Reply(error: "deadlineExceeded: do not retry with a new RequestId; an already running operation may have side effects.");
                            reply = await pending.Completion.Task.ConfigureAwait(false);
                        }
                    }
                    Wire.WriteFrame(pipe, serializer.Serialize(reply));
                }
            }
            catch (Exception e) { if (!stop.IsCancellationRequested) log("Pipe request failed: " + e.Message); }
            finally
            {
                lock (sync)
                {
                    currentPipe?.Dispose();
                    if (!stop.IsCancellationRequested)
                    {
                        try { currentPipe = MakePipe(); }
                        catch (Exception e) { log("Bridge endpoint stopped: " + e); stop.Cancel(); }
                    }
                }
            }
        }
    }

    public bool TryDequeue(out Pending pending) => queue.TryDequeue(out pending!);
    public void Dispose()
    {
        lock (sync) { stop.Cancel(); currentPipe?.Dispose(); }
        while (queue.TryDequeue(out var item)) item.Reply(error: "Bridge unloaded.");
        lock (sync) foreach (var item in requests.Values) item.Reply(error: "Bridge unloaded.");
        if (File.Exists(descriptorPath)) File.Delete(descriptorPath);
    }
}
