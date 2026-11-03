using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hnpb.Host;

internal sealed class PipeClient(string registry)
{
    private static readonly Regex InstanceName = new("^hnpb-[0-9]+-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    internal object[] Instances()
    {
        if (!Directory.Exists(registry)) return [];
        var results = new List<object>();
        foreach (var path in Directory.EnumerateFiles(registry, "hnpb-*.json").Take(64))
        {
            try
            {
                var instance = JsonSerializer.Deserialize<InstanceDescriptor>(File.ReadAllText(path));
                if (instance == null || !Valid(instance) || Path.GetFileNameWithoutExtension(path) != instance.InstanceId) continue;
                bool? present;
                try { using var process = Process.GetProcessById(instance.ProcessId); present = !process.HasExited; }
                catch (ArgumentException) { present = false; }
                catch { present = null; }
                results.Add(new { instance.InstanceId, instance.ProcessId, instance.GameDirectory, instance.StartedUtc,
                    ProcessPresent = present, Reachability = "not probed; stale descriptors are possible after a crash" });
            }
            catch (Exception e) { Console.Error.WriteLine("Ignored invalid instance descriptor: " + Path.GetFileName(path) + ": " + e.Message); }
        }
        return results.ToArray();
    }

    internal async Task<BridgeReply> Send(JsonElement args, string method, CancellationToken cancellation)
    {
        string id = args.GetProperty("instanceId").GetString()!;
        if (!InstanceName.IsMatch(id)) throw new ArgumentException("Invalid instanceId.");
        string path = Path.Combine(registry, id + ".json");
        var instance = JsonSerializer.Deserialize<InstanceDescriptor>(await File.ReadAllTextAsync(path, cancellation));
        if (instance == null || !Valid(instance) || instance.InstanceId != id) throw new InvalidDataException("Invalid instance descriptor.");
        var request = new BridgeRequest { RequestId = args.TryGetProperty("requestId", out var rid) ? rid.GetString()! : Guid.NewGuid().ToString("N"),
            Method = method, SessionEpoch = args.TryGetProperty("sessionEpoch", out var epoch) ? epoch.GetString()! : "",
            TimeoutMs = 10000,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(args.GetRawText())! };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(12500);
        using var pipe = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1500, timeout.Token);
        // Closing the pipe interrupts the bounded blocking frame reader on cancellation.
        using var registration = timeout.Token.Register(() => pipe.Dispose());
        return await Task.Run(() =>
        {
            Wire.WriteFrame(pipe, JsonSerializer.Serialize(request));
            var reply = JsonSerializer.Deserialize<BridgeReply>(Wire.ReadFrame(pipe)) ?? throw new InvalidDataException("Empty bridge reply.");
            if (reply.RequestId != request.RequestId) throw new InvalidDataException("Bridge response RequestId mismatch.");
            return reply;
        }, timeout.Token);
    }

    private static bool Valid(InstanceDescriptor descriptor) => descriptor.Protocol == 1 &&
        InstanceName.IsMatch(descriptor.InstanceId) && descriptor.PipeName == descriptor.InstanceId;
}
