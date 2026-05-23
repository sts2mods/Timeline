// Emit the TurnStart structural row BEFORE the engine fires
// start-of-turn effects (relic triggers, card draws, power
// re-applications, etc.). Without this, the existing CombatManager
// .TurnStarted event hook fires AFTER all those effects have already
// landed in the log — which makes the player's "draw 5 cards" and
// any relic triggers look like they happened during the enemy's
// turn because the "Player Turn" divider hadn't been written yet.
//
// Patching the private CombatManager.StartTurn with a Prefix lets us
// drop the divider in at the very top of the turn, before any of
// those effects fire.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace Timeline;

[HarmonyPatch(typeof(CombatManager), "StartTurn")]
public static class CombatManager_StartTurn_Patch
{
    static void Prefix(CombatManager __instance)
    {
        try
        {
            if (!__instance.IsInProgress) return;
            var state = __instance.DebugOnlyGetState();
            if (state == null) return;
            // Clearing the sticky cause prevents the first event of
            // the new turn from inheriting the last relic / card of
            // the previous turn as its source.
            EventFrameStack.ClearActiveCause();
            bool isPlayer = state.CurrentSide == CombatSide.Player;
            TimelineLog.Add(new TimelineEvent
            {
                Effect = EffectKind.TurnStart,
                Detail = isPlayer ? "Player" : "Enemy",
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}StartTurn prefix: {ex.Message}");
        }
    }
}
