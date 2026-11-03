using System.Linq;
using Hacknet;
using HarmonyLib;

namespace Hnpb;

[HarmonyPatch(typeof(OS), nameof(OS.addExe))]
internal static class RamAdmission
{
    internal static long Used(OS os) => os.exes?.Sum(exe => (long)System.Math.Max(0, exe.ramCost)) ?? 0;
    private static void Refresh(OS os) => os.ramAvaliable = (int)System.Math.Max(int.MinValue, os.totalRam - Used(os));

    // This prefix is void and has no ref arguments so Harmony also runs it
    // when the Pathfinder prefix replaces the original implementation.
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static void Before(OS __instance) => Refresh(__instance);
    private static void BeforeCustom(OS __0) => Refresh(__0);

    internal static void PatchCustom(Harmony harmony)
    {
        var manager = AccessTools.TypeByName("Pathfinder.Executable.ExecutableManager");
        var executable = AccessTools.TypeByName("Pathfinder.Executable.GameExecutable");
        if (manager != null && executable != null)
            harmony.Patch(AccessTools.Method(manager, "AddGameExecutable", new[] { typeof(OS), executable }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(RamAdmission), nameof(BeforeCustom))));
    }
}
