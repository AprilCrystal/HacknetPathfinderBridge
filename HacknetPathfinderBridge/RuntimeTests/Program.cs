using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Freezer;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Hnpb;

internal static class Program
{
    private static int count, controls, assertions;
    private static readonly Func<bool> Admit = (Func<bool>)AccessTools.Method(typeof(PauseController), "AdmitUpdate").CreateDelegate(typeof(Func<bool>));
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Tick() { count++; }
    private static bool Permit() => Admit();
    private static bool Skip() => false;
    private static void Pump() => controls++;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FakeTextBox(int id, int x, int y, int width, int lines, string text, SpriteFont font) => text;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FakeTerminalField(int id, int x, int y, int width, int selectionHeight, int lines, string text, SpriteFont font) => text;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FakeButton(int id, int x, int y, int width, int height, string text, Color? color, Texture2D texture) => false;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => new AssemblyName(args.Name).Name == "Hacknet"
            ? Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HacknetPathfinder.exe")) : null;
        try { TestHooks(); TestBridgeHookCompilation(); TestServer().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " assertions (actual Freezer transpiler, Bridge UI hook signatures, Framework pipe server, deduplication and deadlines). No game started."); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void TestHooks()
    {
        var freezer = new Harmony("BepInEx.Plugin.com.AprilCrystalo.dev");
        var bridge = new Harmony("tests.bridge"); var foreign = new Harmony("tests.foreign");
        var target = AccessTools.Method(typeof(Program), nameof(Tick));
        try
        {
            var marker = typeof(PauseController).Assembly.GetType("Freezer.GameUpdatePatch")!.GetMethod("MarkBody", BindingFlags.NonPublic | BindingFlags.Static)!;
            freezer.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(Permit))), transpiler: new HarmonyMethod(marker));
            var pump = new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(Pump))) { priority = int.MaxValue, before = new[] { freezer.Id } };
            bridge.Patch(target, prefix: pump);
            PauseController.Pause(); var initial = PauseController.Counters();
            for (int i = 0; i < 10; i++) Tick();
            Check(count == 0 && controls == 10, "Control prefix must remain live while the original is gated.");
            Check(PauseController.Counters()[2] == initial[2], "Skipped original must not increment real body marker.");
            PauseController.Advance(2, 0);
            for (int i = 0; i < 10; i++) Tick();
            var after = PauseController.Counters();
            Check(count == 2 && after[2] - initial[2] == 2, "Actual Freezer transpiler must mark exactly two original body entries.");
            Check(PauseController.IsPaused && !PauseController.IsStepping, "Gate remains paused after permit exhaustion.");
            foreign.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(Skip))) { priority = Priority.Last });
            initial = PauseController.Counters(); PauseController.Advance(1, 0); Tick(); after = PauseController.Counters();
            Check(after[0] - initial[0] == 1 && after[2] - initial[2] == 0, "Foreign skip must be distinguishable from an admitted budget.");
        }
        finally { foreign.UnpatchSelf(); bridge.UnpatchSelf(); freezer.UnpatchSelf(); PauseController.Resume(); }
    }

    private static void TestBridgeHookCompilation()
    {
        var harmony = new Harmony("tests.bridge.compile");
        try
        {
            // Publicized reference binaries are not executable game binaries. Compile the real UI
            // postfix methods against equivalent signatures, without running a reference module initializer.
            foreach (var pair in new[] { new[] { nameof(FakeTextBox), "TextBoxHook" }, new[] { nameof(FakeTerminalField), "TerminalFieldHook" }, new[] { nameof(FakeButton), "ButtonHook" } })
            {
                var hook = typeof(BridgePlugin).Assembly.GetType("Hnpb." + pair[1])!.GetMethod("After", BindingFlags.NonPublic | BindingFlags.Static)!;
                harmony.Patch(AccessTools.Method(typeof(Program), pair[0]), postfix: new HarmonyMethod(hook));
            }
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Program), nameof(FakeTextBox))).Owners.Contains(harmony.Id), "Real text-box postfix must compile without __args support.");
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Program), nameof(FakeTerminalField))).Owners.Contains(harmony.Id), "Real terminal-field postfix parameter mapping must compile.");
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Program), nameof(FakeButton))).Owners.Contains(harmony.Id), "Real button postfix parameter mapping must compile.");
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static async Task TestServer()
    {
        string registry = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server-test-" + Guid.NewGuid().ToString("N"));
        using (var server = new BridgeServer(Console.Error.WriteLine, registry))
        {
            server.Start();
            var request = new BridgeRequest { RequestId = "once", Method = "fixture", TimeoutMs = 1000 };
            var first = Send(server.InstanceId, request);
            Pending pending = await Dequeue(server);
            var live = new System.Collections.Generic.Dictionary<string, object> { ["value"] = 1 };
            pending.Reply(live); live["value"] = 9;
            var reply = await first;
            Check(reply.Ok, "Framework server must return a main-thread reply.");
            var detached = (System.Collections.Generic.Dictionary<string, object>)reply.Data!;
            Check(Convert.ToInt32(detached["value"]) == 1, "Reply must detach DTOs before worker serialization.");
            var duplicate = await Send(server.InstanceId, request);
            Check(duplicate.Ok && !server.TryDequeue(out _), "Same request ID must replay result without re-enqueueing an action.");
            var expiring = new BridgeRequest { RequestId = "expire", Method = "fixture", TimeoutMs = 150 };
            var expirationReply = await Send(server.InstanceId, expiring);
            Check(!expirationReply.Ok && expirationReply.Error!.Contains("deadlineExceeded"), "Unserviced request must return an explicit deadline failure.");
            var expired = await Dequeue(server);
            Check(expired.IsExpired, "Timed-out queued action must be marked so the pump cannot execute it later.");
            string path = Directory.GetFiles(registry, "*.json").Single();
            server.Dispose();
            Check(!File.Exists(path), "Unload must remove only its own instance descriptor.");
        }
    }

    private static async Task<Pending> Dequeue(BridgeServer server)
    {
        for (int i = 0; i < 200; i++) { if (server.TryDequeue(out var pending)) return pending; await Task.Delay(10); }
        throw new TimeoutException("Fixture request never reached control queue.");
    }
    private static Task<BridgeReply> Send(string pipeName, BridgeRequest request) => Task.Run(() =>
    {
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect(3000); var serializer = new JavaScriptSerializer();
            Wire.WriteFrame(pipe, serializer.Serialize(request));
            return serializer.Deserialize<BridgeReply>(Wire.ReadFrame(pipe));
        }
    });
}
