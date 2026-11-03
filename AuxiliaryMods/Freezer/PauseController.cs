namespace Freezer;

/// <summary>
/// Controls the Game1.Update gate. External loops may change this state without
/// accessing game objects. Original update/draw and SDL text input handlers are
/// skipped while paused. Independent workers are not paused.
/// </summary>
public static class PauseController
{
    private static readonly FrameBudget budget = new();

    public static bool IsPaused => budget.IsPaused;
    public static bool IsStepping => budget.IsStepping;

    public static void Pause() => budget.SetPaused(true);

    public static void Resume() => budget.SetPaused(false);

    public static void Toggle() => budget.Toggle();
    public static void Advance(int updates, int draws) => budget.Advance(updates, draws);
    public static long[] Counters() => budget.Counters();
    internal static bool AdmitUpdate() => budget.AdmitUpdate();
    internal static bool AdmitDraw() => budget.AdmitDraw();
    public static void NoteUpdateBody() => budget.NoteUpdateBody();
    public static void NoteDrawBody() => budget.NoteDrawBody();

    internal static void Reset() => Resume();
}
