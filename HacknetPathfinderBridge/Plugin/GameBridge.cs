using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Freezer;
using Hacknet;
using Hacknet.Gui;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Hnpb;

internal sealed class UiControl
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Masked { get; set; }
    public bool InputBlocked { get; set; }
}

internal sealed class GameBridge : IDisposable
{
    private readonly BridgeServer server;
    private OS? lastOs;
    private string epoch = System.Guid.NewGuid().ToString("N");
    private Pending? active;
    private Pending? pauseAcknowledgement;
    private long pauseBodyCount;
    private long[] before = new long[6];
    private int expectedUpdates, expectedDraws;
    private readonly List<UiControl> drawing = new();
    private UiControl[] controls = Array.Empty<UiControl>();
    private long uiRevision;
    private string uiEpoch = "";
    private UiControl? activation;
    private bool activated;
    private bool capture;
    private string? image;
    private string? imageError;
    private string? error;
    private int errors;
    public string? FillValue { get; private set; }
    private bool drawingThisFrame;
    public bool IsolatingInput { get; private set; }
    private MouseState savedMouse, savedLastMouse;
    private int savedActive, savedHot;

    internal GameBridge(BridgeServer server) { this.server = server; }

    public void Pump()
    {
        try
        {
            if (!ReferenceEquals(lastOs, OS.currentInstance))
            {
                Cancel("Session changed; old operations and UI handles invalidated.");
                lastOs = OS.currentInstance; epoch = System.Guid.NewGuid().ToString("N");
                controls = Array.Empty<UiControl>(); uiEpoch = "";
            }
            if (active != null)
            {
                if (active.IsExpired) Cancel("Operation expired; budget cancelled and game held paused.");
                else if (!PauseController.IsPaused) Cancel("Pause was changed externally; operation interrupted.");
                else if (!PauseController.IsStepping)
                {
                    var now = PauseController.Counters();
                    string? failure = error ?? imageError;
                    if (now[2] - before[2] != expectedUpdates || now[3] - before[3] != expectedDraws)
                        failure = "Another patch skipped an admitted original method, or a permit was cancelled.";
                    if (activation != null && !activated) failure = "Control disappeared, was blocked, or no longer matched; activation not performed.";
                    var pending = active; active = null;
                    pending.Reply(new { State = Snapshot(), Ui = Ui(),
                        AdmittedUpdates = now[0] - before[0], AdmittedDraws = now[1] - before[1],
                        UpdateBodies = now[2] - before[2], DrawBodies = now[3] - before[3],
                        Activated = activated, Semantics = "Native host timing; Update/Draw budgets are independent. Background workers and wall clocks are not frozen." }, failure, image);
                    activation = null; FillValue = null; capture = false; image = null;
                }
            }
            for (int i = 0; i < 4 && server.TryDequeue(out var pending); i++)
            {
                if (pending.IsExpired) { pending.Reply(error: "Request expired before execution."); continue; }
                if (pending.Request.SessionEpoch.Length != 0 && pending.Request.SessionEpoch != epoch)
                { pending.Reply(error: "staleSession: observe the new session before acting."); continue; }
                try { Execute(pending); }
                catch (Exception e) { pending.Reply(error: e.Message); }
            }
        }
        catch (Exception e) { RecordError("Control pump: " + e.Message); Cancel(e.Message); }
    }

    private void Execute(Pending pending)
    {
        var request = pending.Request; var args = request.Arguments;
        if (active != null && request.Method != "status" && request.Method != "ui")
            throw new InvalidOperationException("Another operation is active.");
        switch (request.Method)
        {
            case "status": pending.Reply(Snapshot()); break;
            case "observe":
                if (!args.TryGetValue("pause", out var pause) || Convert.ToBoolean(pause)) AcknowledgePauseAfterTick(pending);
                else pending.Reply(Snapshot());
                break;
            case "pause": AcknowledgePauseAfterTick(pending); break;
            case "resume": RequireEpoch(request); PauseController.Resume(); pending.Reply(Snapshot()); break;
            case "advance":
                RequireEpoch(request);
                Begin(pending, Number(args, "updates", 1), Number(args, "draws", 0)); break;
            case "command":
                RequireEpoch(request); RequirePaused();
                var os = RequireOs();
                string command = Text(args, "command");
                if (command.Length == 0 || command.Length > 2048 || command.Contains("\n") || command.Contains("\r"))
                    throw new ArgumentException("Command must be a single nonempty line of at most 2048 characters.");
                if (os.terminal.preventingExecution) throw new InvalidOperationException("Terminal is preventing execution.");
                os.terminal.currentLine = command;
                // Existing game semantic dispatch: deliberately not keyboard emulation or OS.execute's worker thread.
                os.terminal.NonThreadedInstantExecuteLine();
                pending.Reply(new { DispatchCompleted = true, BusinessCompletion = "not asserted; spawned executables/actions may need advance", State = Snapshot() });
                break;
            case "ui": pending.Reply(Ui()); break;
            case "capture":
                RequireEpoch(request); Begin(pending, 0, 1); capture = true; break;
            case "activate":
            case "fill":
                RequireEpoch(request); RequirePaused();
                if (Number(args, "revision", -1) != uiRevision || uiEpoch != epoch) throw new InvalidOperationException("staleUi: capture the current interface first.");
                var target = controls.Where(c => c.Key == Text(args, "key")).ToArray();
                if (target.Length != 1) throw new InvalidOperationException("Control key is missing or ambiguous.");
                if (target[0].InputBlocked) throw new InvalidOperationException("UI input is blocked.");
                if (request.Method == "activate" && target[0].Kind != "button" && target[0].Kind != "terminal") throw new ArgumentException("Only buttons and terminal text fields can be activated.");
                if (request.Method == "fill" && target[0].Kind != "textbox" && target[0].Kind != "terminal") throw new ArgumentException("Only text inputs can be filled.");
                string? value = request.Method == "fill" ? Text(args, "text") : null;
                if (value != null && value.Length > 4096) throw new ArgumentException("Text limit is 4096 characters.");
                Begin(pending, 0, 1); activation = target[0]; FillValue = value; break;
            case "adapters": pending.Reply(BridgeAdapters.Describe()); break;
            case "adapter.observe": pending.Reply(BridgeAdapters.Get(Text(args, "adapterId")).Observe(lastOs)); break;
            case "adapter.invoke":
                RequireEpoch(request); RequirePaused();
                var adapterArgs = args.TryGetValue("arguments", out var nested) ? nested as Dictionary<string, object> : new Dictionary<string, object>();
                pending.Reply(BridgeAdapters.Get(Text(args, "adapterId")).Invoke(lastOs, Text(args, "operation"), adapterArgs ?? throw new ArgumentException("arguments must be an object."))); break;
            default: throw new ArgumentException("Unknown bridge method: " + request.Method);
        }
    }

    private void Begin(Pending pending, int updates, int draws)
    {
        RequirePaused();
        PauseController.Advance(updates, draws);
        active = pending; before = PauseController.Counters(); expectedUpdates = updates; expectedDraws = draws;
        activation = null; activated = false; FillValue = null; capture = false; image = imageError = error = null;
    }

    private void AcknowledgePauseAfterTick(Pending pending)
    {
        if (pauseAcknowledgement != null) throw new InvalidOperationException("A pause acknowledgement is already pending.");
        PauseController.Pause(); pauseBodyCount = PauseController.Counters()[2]; pauseAcknowledgement = pending;
    }

    public void AfterUpdate()
    {
        var pending = pauseAcknowledgement;
        if (pending == null) return;
        pauseAcknowledgement = null;
        try
        {
            bool interrupted = !PauseController.IsPaused || PauseController.Counters()[2] != pauseBodyCount;
            if (interrupted) PauseController.Pause();
            pending.Reply(Snapshot(), interrupted ? "Pause was interrupted during this tick; pause reasserted, observe again." : null);
        }
        catch (Exception e) { pending.Reply(error: "Pause acknowledgement failed: " + e.Message); }
    }

    private object Snapshot()
    {
        var os = OS.currentInstance;
        var history = os?.terminal == null ? null : AccessTools.Field(typeof(Terminal), "history")?.GetValue(os.terminal) as List<string>;
        var trace = os?.traceTracker;
        return new
        {
            server.InstanceId, SessionEpoch = epoch, Paused = PauseController.IsPaused, Stepping = PauseController.IsStepping,
            Counters = PauseController.Counters(), Game = typeof(OS).Assembly.FullName, Freezer = FreezerCore.ModVer,
            OsReady = os != null && os.isLoaded, CanRunContent = os?.canRunContent ?? false,
            GameTimeSeconds = OS.currentElapsedTime,
            Terminal = os?.terminal == null ? null : new { CurrentLine = TextBox.MaskingText ? "<masked>" : os.terminal.currentLine,
                os.terminal.prompt, os.terminal.preventingExecution, History = history?.Skip(Math.Max(0, history.Count - 60))
                    .Select(line => line.Length > 4096 ? line.Substring(0, 4096) + "<truncated>" : line).ToArray() ?? Array.Empty<string>() },
            ConnectedNode = os?.connectedComp == null ? null : new { os.connectedComp.idName, os.connectedComp.ip },
            Trace = trace == null ? null : new { trace.active, RemainingSeconds = AccessTools.Field(trace.GetType(), "timer")?.GetValue(trace) },
            Nodes = os?.netMap?.nodes?.Take(300).Select(c => new { c.idName, c.ip, Daemons = c.daemons.Select(d => d.GetType().FullName).ToArray() }).ToArray(),
            UiRevision = uiRevision, ErrorsObserved = errors, LastError = error,
            Consistency = "mainThreadBoundary; background workers and wall clocks are not frozen",
            Supported = new[] { "pause", "resume", "advance.nativeTiming", "terminal.semanticDispatch", "ui.commonControls", "ui.semanticActivate", "ui.semanticFill", "capture.oneDraw", "adapters.v1" }
        };
    }

    private object Ui() => new { Revision = uiRevision, SessionEpoch = uiEpoch, Controls = controls,
        CurrentSession = uiEpoch == epoch, Freshness = "Last completed Draw; capture explicitly to refresh (advances one Draw).", CustomDrawnControls = "require adapter" };

    public void BeginDraw()
    {
        drawingThisFrame = !PauseController.IsPaused || PauseController.Counters()[5] > 0;
        if (drawingThisFrame) drawing.Clear();
        if (drawingThisFrame && PauseController.IsPaused)
        {
            savedMouse = GuiData.mouse; savedLastMouse = GuiData.lastMouse;
            savedActive = GuiData.active; savedHot = GuiData.hot;
            GuiData.mouse = GuiData.lastMouse = new MouseState(-10000, -10000, savedMouse.ScrollWheelValue,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            GuiData.active = GuiData.hot = -1;
            IsolatingInput = true;
        }
    }

    public bool Control(string kind, int id, int x, int y, int width, int height, string text, bool masked)
    {
        if (!drawingThisFrame) return false;
        string key = kind + ":" + id + ":" + x + ":" + y + ":" + width + ":" + height;
        var item = new UiControl { Key = key, Kind = kind, Id = id, X = x, Y = y, Width = width, Height = height,
            Text = text, Masked = masked, InputBlocked = GuiData.blockingInput };
        drawing.Add(item);
        if (activation == null || activated || activation.Key != key || item.InputBlocked ||
            activation.Text != item.Text || activation.Masked != item.Masked) return false;
        activated = true; return true;
    }

    public void EndDraw(GraphicsDevice graphics, bool bodyEntered, Exception? exception)
    {
        try
        {
            drawingThisFrame = false;
            if (!bodyEntered || exception != null) return;
            controls = drawing.ToArray(); uiEpoch = epoch; uiRevision++;
            if (!capture) return;
            int width = graphics.PresentationParameters.BackBufferWidth, height = graphics.PresentationParameters.BackBufferHeight;
            if ((long)width * height > 4_000_000 || width < 1 || height < 1) throw new InvalidOperationException("Screenshot pixel limit exceeded.");
            var pixels = new Color[width * height]; graphics.GetBackBufferData(pixels);
            using (var texture = new Texture2D(graphics, width, height, false, SurfaceFormat.Color))
            using (var output = new MemoryStream())
            {
                texture.SetData(pixels); texture.SaveAsPng(output, width, height);
                if (output.Length > 4_000_000) throw new InvalidOperationException("PNG size limit exceeded.");
                image = Convert.ToBase64String(output.ToArray());
            }
        }
        catch (Exception e) { imageError = "Screenshot unavailable: " + e.Message; }
        finally
        {
            RestoreInput();
        }
    }

    private void RestoreInput()
    {
        if (!IsolatingInput) return;
        GuiData.mouse = savedMouse; GuiData.lastMouse = savedLastMouse;
        if (!activated) { GuiData.active = savedActive; GuiData.hot = savedHot; }
        IsolatingInput = false;
    }

    public void RecordError(string message) { errors++; error = message; }
    private void Cancel(string reason)
    {
        if (active == null) return;
        PauseController.Pause(); active.Reply(error: reason); active = null;
        activation = null; FillValue = null; capture = false; image = null;
    }
    private void RequireEpoch(BridgeRequest request)
    { if (request.SessionEpoch != epoch) throw new InvalidOperationException("sessionEpoch is required; observe before acting."); }
    private static void RequirePaused()
    { if (!PauseController.IsPaused || PauseController.IsStepping) throw new InvalidOperationException("Pause the game before acting."); }
    private static OS RequireOs() => OS.currentInstance ?? throw new InvalidOperationException("No active OS session.");
    private static int Number(Dictionary<string, object> args, string key, int fallback) => args.TryGetValue(key, out var value) ? Convert.ToInt32(value) : fallback;
    private static string Text(Dictionary<string, object> args, string key) => args.TryGetValue(key, out var value) && value is string text ? text : throw new ArgumentException(key + " must be a string.");
    public void Dispose()
    {
        RestoreInput();
        Cancel("Bridge unloaded. Game remains paused if it was controlled by the bridge.");
        pauseAcknowledgement?.Reply(error: "Bridge unloaded before pause acknowledgement."); pauseAcknowledgement = null;
    }
}
