// Parent-linking + cause-attribution for the timeline.
//
// Two pieces of state:
//
//   1. _stack — event indices for parent linking (card play opens a
//      frame, child effects nest under it). Strictly push/pop with
//      Prefix/Postfix because card resolution actually waits inside
//      AutoPlay so the surrounding patch IS synchronous w.r.t. the
//      effects.
//
//   2. _activeCause — *sticky* attribution for the most recent
//      high-level source (relic flash, card play, monster move). Why
//      sticky? Relic bodies are `async`, so when NRelicFlashVfx.Create
//      fires inside a relic's AfterSideTurnStart, the Harmony Postfix
//      pops *immediately* after the VFX spawns — long before the
//      relic's `await PowerCmd.Apply(...)` runs. So we can't bracket
//      the relic's body with a stack push/pop. Instead the relic
//      "claims" the slot when it flashes and stays the active cause
//      until something else replaces it (another flash, a card play,
//      a turn start). Anything that emits with an empty / generic
//      cause inherits the active cause as its attribution.
//
// The game is effectively single-threaded under Godot's main loop, so
// the static state is safe.
using System.Collections.Generic;

namespace Timeline;

public static class EventFrameStack
{
    private static readonly Stack<int> _stack = new();
    private static TimelineActor _activeCause = TimelineActor.None;

    public static void Reset()
    {
        _stack.Clear();
        _activeCause = TimelineActor.None;
    }

    public static int CurrentParent => _stack.Count > 0 ? _stack.Peek() : -1;
    public static int Depth => _stack.Count;
    public static TimelineActor ActiveCause => _activeCause;

    public static void SetActiveCause(TimelineActor cause) => _activeCause = cause;
    public static void ClearActiveCause() => _activeCause = TimelineActor.None;

    public static void Push(int eventIndex)
    {
        if (eventIndex < 0) return;
        _stack.Push(eventIndex);
    }

    public static void Pop()
    {
        if (_stack.Count > 0) _stack.Pop();
    }

    public static FrameScope Scope(int eventIndex) => new FrameScope(eventIndex);

    public readonly struct FrameScope : System.IDisposable
    {
        private readonly bool _shouldPop;
        public FrameScope(int eventIndex)
        {
            _shouldPop = eventIndex >= 0;
            if (_shouldPop) Push(eventIndex);
        }
        public void Dispose() { if (_shouldPop) Pop(); }
    }
}
