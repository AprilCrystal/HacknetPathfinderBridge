using System;

namespace Freezer;

/// <summary>Thread-safe, independent update/draw permits. Paused stays true while stepping.</summary>
public sealed class FrameBudget
{
    private readonly object sync = new();
    private bool paused;
    private int updates, draws;
    private long admittedUpdates, admittedDraws, updateBodies, drawBodies;

    public bool IsPaused { get { lock (sync) return paused; } }
    public bool IsStepping { get { lock (sync) return updates != 0 || draws != 0; } }
    public void SetPaused(bool value) { lock (sync) { paused = value; updates = draws = 0; } }
    public void Toggle() { lock (sync) { paused = !paused; updates = draws = 0; } }
    public void Advance(int updateCount, int drawCount)
    {
        if (updateCount < 0 || drawCount < 0 || updateCount > 300 || drawCount > 300 || updateCount + drawCount == 0)
            throw new ArgumentOutOfRangeException(nameof(updateCount), "Each budget must be 0..300, with at least one permit.");
        lock (sync)
        {
            if (!paused || updates != 0 || draws != 0) throw new InvalidOperationException("Pause and finish the previous budget first.");
            updates = updateCount; draws = drawCount;
        }
    }
    public bool AdmitUpdate() { lock (sync) { if (paused && updates == 0) return false; if (paused) updates--; admittedUpdates++; return true; } }
    public bool AdmitDraw() { lock (sync) { if (paused && draws == 0) return false; if (paused) draws--; admittedDraws++; return true; } }
    public void NoteUpdateBody() { lock (sync) updateBodies++; }
    public void NoteDrawBody() { lock (sync) drawBodies++; }
    public long[] Counters() { lock (sync) return new[] { admittedUpdates, admittedDraws, updateBodies, drawBodies, (long)updates, draws }; }
}
