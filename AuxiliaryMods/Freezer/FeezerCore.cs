using BepInEx;
using BepInEx.Hacknet;
using Microsoft.Xna.Framework.Input;

namespace Freezer;

[BepInPlugin(ModGUID, ModName, ModVer)]
public class FreezerCore : HacknetPlugin
{
    public const string ModGUID = "com.AprilCrystalo.dev";
    public const string ModName = "Freezer";
    public const string ModVer = "1.1.0";

    public override bool Load()
    {
        PauseController.Reset();
        GameUpdatePatch.ToggleKey = Config.Bind("Pause", "ToggleKey", Keys.F8,
            "Press once to pause updates, drawing and text input; press again to resume.").Value;
        GameUpdatePatch.ResetInput();
        try
        {
            HarmonyInstance.PatchAll(typeof(FreezerCore).Assembly);
        }
        catch
        {
            HarmonyInstance.UnpatchSelf();
            PauseController.Reset();
            throw;
        }
        Log.LogInfo($"Freezer loaded. Toggle pause with {GameUpdatePatch.ToggleKey}.");
        return true;
    }

    public override bool Unload()
    {
        PauseController.Reset();
        GameUpdatePatch.ResetInput();
        return base.Unload();
    }
}
