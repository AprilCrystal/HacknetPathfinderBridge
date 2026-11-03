using Hacknet;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System.Collections.Generic;

namespace Freezer;

[HarmonyPatch(typeof(Game1), "Draw", new[] { typeof(GameTime) })]
internal static class GameDrawPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => PauseController.AdmitDraw();

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> MarkBody(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
            AccessTools.Method(typeof(PauseController), nameof(PauseController.NoteDrawBody)));
        foreach (var instruction in instructions) yield return instruction;
    }
}
