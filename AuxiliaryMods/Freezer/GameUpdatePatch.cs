using Hacknet;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;

namespace Freezer;

[HarmonyPatch(typeof(Game1), "Update", new[] { typeof(GameTime) })]
internal static class GameUpdatePatch
{
    internal static Keys ToggleKey = Keys.F8;
    private static bool wasDown;

    internal static void ResetInput()
    {
        // Do not interpret a key already held during loading as a new press.
        wasDown = Keyboard.GetState().IsKeyDown(ToggleKey);
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Game1 __instance)
    {
        bool down = Keyboard.GetState().IsKeyDown(ToggleKey);

        // The prefix continues to run even when the original Update is skipped.
        // Track input while unfocused too, so refocusing with a held key does
        // not toggle the pause state.
        if (__instance.IsActive && down && !wasDown)
            PauseController.Toggle();

        wasDown = down;
        return PauseController.AdmitUpdate();
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> MarkBody(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
            AccessTools.Method(typeof(PauseController), nameof(PauseController.NoteUpdateBody)));
        foreach (var instruction in instructions) yield return instruction;
    }
}
