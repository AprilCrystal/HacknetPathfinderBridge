using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hnpb;

public sealed class BridgeRequest
{
    public int Protocol { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public string Method { get; set; } = "";
    public string SessionEpoch { get; set; } = "";
    public int TimeoutMs { get; set; } = 10000;
    public Dictionary<string, object> Arguments { get; set; } = new();
}

public sealed class BridgeReply
{
    public bool Ok { get; set; }
    public string RequestId { get; set; } = "";
    public object? Data { get; set; }
    public string? Error { get; set; }
    public string? ImageBase64 { get; set; }
}

public sealed class InstanceDescriptor
{
    public int Protocol { get; set; } = 1;
    public string InstanceId { get; set; } = "";
    public string PipeName { get; set; } = "";
    public int ProcessId { get; set; }
    public string GameDirectory { get; set; } = "";
    public string StartedUtc { get; set; } = "";
}

public static class Wire
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    // One bounded UTF-8 JSON frame per connection. Do not mix StreamReader buffering with raw framing.
    public static string ReadFrame(Stream stream)
    {
        using (var data = new MemoryStream())
        {
            var buffer = new byte[8192];
            while (true)
            {
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) throw new EndOfStreamException("Pipe closed before a complete response; operation outcome may be unknown.");
                int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
                int take = end < 0 ? count : end;
                if (data.Length + take > MaximumFrameBytes) throw new InvalidDataException("Frame size limit exceeded.");
                data.Write(buffer, 0, take);
                if (end >= 0)
                {
                    if (end != count - 1) throw new InvalidDataException("Only one frame is allowed per connection.");
                    return new UTF8Encoding(false, true).GetString(data.ToArray());
                }
            }
        }
    }
    public static void WriteFrame(Stream stream, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Frame size limit exceeded.");
        stream.Write(bytes, 0, bytes.Length); stream.Flush();
    }
}
