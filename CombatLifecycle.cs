// Hook the game's CombatManager events to mark log boundaries.
// CombatManager.Instance is a class-init static — events can be wired
// at any point after the assembly is loaded; we just defer one frame
// so the harmony patcher has finished bootstrapping.
using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace Timeline;

public static class CombatLifecycle
{
    private static bool _subscribed;

    public static void Subscribe()
    {
        if (_subscribed) return;
        try
        {
            var cm = CombatManager.Instance;
            cm.CombatSetUp += OnCombatSetUp;
            cm.CombatEnded += OnCombatEnded;
            cm.CombatWon += OnCombatWon;
            // TurnStart row is emitted from the StartTurn Harmony
            // patch instead — TurnStarted fires only AFTER all the
            // start-of-turn relic / draw effects have landed, which
            // would put the divider in the wrong place.
            _subscribed = true;
            GD.Print($"{TimelineMod.LogPrefix}lifecycle subscribed");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}lifecycle subscribe: {ex.Message}");
        }
    }

    private static void OnCombatSetUp(CombatState state)
    {
        TimelineLog.Clear();
        EventFrameStack.Reset();
        TimelineLog.Add(new TimelineEvent { Effect = EffectKind.CombatStart });
        Callable.From(TimelinePanel.EnsureAttached).CallDeferred();
    }

private static void OnCombatEnded(CombatRoom room)
    {
        TimelineLog.Add(new TimelineEvent { Effect = EffectKind.CombatEnd });
        EventFrameStack.Reset();
        TimelinePanel.Detach();
    }

    private static void OnCombatWon(CombatRoom room)
    {
        TimelineLog.Add(new TimelineEvent
        {
            Effect = EffectKind.CombatEnd,
            Detail = "Combat won",
        });
    }
}
