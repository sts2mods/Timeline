// TurnEnd structural row, mirroring TurnStartPatch. Two boundaries
// fire: the player's end-of-turn (after they hit End Turn but before
// enemies move) and the enemy's end-of-turn (after every monster has
// moved, before the next StartTurn). We tag the row with which side
// just ended so the narrator can say "Player turn ended".
//
// We also clear the sticky ActiveCause at each boundary — the player
// turn might end with a card or relic as cause, and we don't want
// the next turn's first event inheriting that.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace Timeline;

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndPlayerTurnPhaseOneInternal))]
public static class CombatManager_EndPlayerTurn_Patch
{
    static void Prefix(CombatManager __instance)
    {
        if (!TimelineMod.Enabled) return;
        try
        {
            if (!__instance.IsInProgress) return;
            EmitTurnEnd("Player");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}EndPlayerTurn prefix: {ex.Message}");
        }
    }

    internal static void EmitTurnEnd(string side)
    {
        EventFrameStack.ClearActiveCause();
        TimelineLog.Add(new TimelineEvent
        {
            Effect = EffectKind.TurnEnd,
            Detail = side,
        });
    }
}

[HarmonyPatch(typeof(CombatManager), "EndEnemyTurnInternal")]
public static class CombatManager_EndEnemyTurn_Patch
{
    static void Prefix(CombatManager __instance)
    {
        if (!TimelineMod.Enabled) return;
        try
        {
            if (!__instance.IsInProgress) return;
            CombatManager_EndPlayerTurn_Patch.EmitTurnEnd("Enemy");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}EndEnemyTurn prefix: {ex.Message}");
        }
    }
}
