using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Hacknet;
using Freezer;
using Hacknet;
using Hacknet.Gui;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Hnpb;

[BepInPlugin(Guid, "Hacknet Pathfinder Bridge", "0.1.0")]
[BepInDependency(FreezerCore.ModGUID, "1.1.0")]
public sealed class BridgePlugin : HacknetPlugin
{
    public const string Guid = "com.hnc.HacknetPathfinderBridge";
    internal static GameBridge? Active;
    private BridgeServer? server;
    public override bool Load()
    {
        try
        {
            // Verify the actual terminal API before advertising commands.
            if (AccessTools.Method(typeof(Terminal), "NonThreadedInstantExecuteLine", Type.EmptyTypes) == null ||
                AccessTools.Field(typeof(Terminal), "history") == null)
                throw new NotSupportedException("Terminal API differs from the supported game build.");
            server = new BridgeServer(message => Log.LogWarning(message));
            Active = new GameBridge(server);
            HarmonyInstance.PatchAll(typeof(BridgePlugin).Assembly);
            RamAdmission.PatchCustom(HarmonyInstance);
            server.Start();
            Log.LogInfo("Bridge ready: " + server.InstanceId + "; registry=" + server.RegistryDirectory);
            return true;
        }
        catch
        {
            HarmonyInstance.UnpatchSelf(); Active?.Dispose(); Active = null; server?.Dispose();
            throw;
        }
    }
    public override bool Unload()
    {
        HarmonyInstance.UnpatchSelf(); Active?.Dispose(); Active = null;
        server?.Dispose(); server = null; BridgeAdapters.Clear(); return true;
    }
}

[HarmonyPatch(typeof(Game1), "Update", new[] { typeof(GameTime) })]
internal static class UpdateHook
{
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    [HarmonyBefore("BepInEx.Plugin.com.AprilCrystalo.dev")]
    private static void Before() => BridgePlugin.Active?.Pump();
    [HarmonyFinalizer]
    private static Exception? Finish(Exception? __exception)
    {
        if (__exception != null) BridgePlugin.Active?.RecordError("Update: " + __exception.Message);
        BridgePlugin.Active?.AfterUpdate();
        return __exception;
    }
}

[HarmonyPatch(typeof(Game1), "Draw", new[] { typeof(GameTime) })]
internal static class DrawHook
{
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    [HarmonyBefore("BepInEx.Plugin.com.AprilCrystalo.dev")]
    private static void Before(out long __state)
    { __state = PauseController.Counters()[3]; BridgePlugin.Active?.BeginDraw(); }
    [HarmonyFinalizer]
    private static Exception? Finish(Game1 __instance, long __state, Exception? __exception)
    {
        if (__exception != null) BridgePlugin.Active?.RecordError("Draw: " + __exception.Message);
        BridgePlugin.Active?.EndDraw(__instance.GraphicsDevice, PauseController.Counters()[3] > __state, __exception);
        return __exception;
    }
}

// Prevent a controlled Draw from replaying keyboard edges or consuming pre-pause text.
// Custom mods that read hardware directly still require their own adapter.
[HarmonyPatch]
internal static class KeyboardGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GuiData), "getKeyboadState");
        yield return AccessTools.Method(typeof(GuiData), "getLastKeyboadState");
    }
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static bool Before(ref KeyboardState __result)
    { if (BridgePlugin.Active?.IsolatingInput != true) return true; __result = new KeyboardState(); return false; }
}

[HarmonyPatch(typeof(GuiData), "getFilteredKeys")]
internal static class TextBufferGuard
{
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static bool Before(ref char[] __result)
    { if (BridgePlugin.Active?.IsolatingInput != true) return true; __result = Array.Empty<char>(); return false; }
}

[HarmonyPatch(typeof(GuiData), "getMouseWheelScroll")]
internal static class WheelGuard
{
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static bool Before(ref float __result)
    { if (BridgePlugin.Active?.IsolatingInput != true) return true; __result = 0; return false; }
}

[HarmonyPatch(typeof(Button), "doButton", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(Color?), typeof(Texture2D) })]
internal static class ButtonHook
{
    [HarmonyPostfix]
    private static void After(int __0, int __1, int __2, int __3, int __4, string __5, ref bool __result)
    {
        if (BridgePlugin.Active?.Control("button", __0, __1, __2, __3, __4, __5, false) == true) __result = true;
    }
}

[HarmonyPatch(typeof(TextBox), "doTextBox", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(SpriteFont) })]
internal static class TextBoxHook
{
    [HarmonyPostfix]
    private static void After(int __0, int __1, int __2, int __3, int __4, ref string __result) =>
        TextControls.Apply("textbox", __0, __1, __2, __3, __4, ref __result);
}

[HarmonyPatch(typeof(TextBox), "doTerminalTextField", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(SpriteFont) })]
internal static class TerminalFieldHook
{
    [HarmonyPostfix]
    private static void After(int __0, int __1, int __2, int __3, int __5, ref string __result) =>
        TextControls.Apply("terminal", __0, __1, __2, __3, __5, ref __result);
}

internal static class TextControls
{
    internal static void Apply(string kind, int id, int x, int y, int width, int lines, ref string result)
    {
        var bridge = BridgePlugin.Active;
        if (bridge == null) return;
        bool filled = bridge.Control(kind, id, x, y, width, lines * TextBox.LINE_HEIGHT,
            TextBox.MaskingText ? "<masked>" : result, TextBox.MaskingText);
        if (filled && bridge.FillValue != null)
        {
            result = bridge.FillValue;
            TextBox.cursorPosition = result.Length; TextBox.textDrawOffsetPosition = 0;
        }
        else if (filled && kind == "terminal")
        {
            // The caller consumes this flag immediately after doTerminalTextField returns.
            // This is the native submit signal, without replaying a hardware key edge.
            TextBox.BoxWasActivated = true;
        }
    }
}
