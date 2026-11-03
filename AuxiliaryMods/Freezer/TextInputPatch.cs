using Hacknet.Input;
using HarmonyLib;

namespace Freezer;

// SDL text events bypass Update/Draw. Discard paused input rather than queue it.
[HarmonyPatch(typeof(TextInputHook), "OnTextInput", new[] { typeof(char) })]
internal static class TextInputPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => !PauseController.IsPaused;
}
