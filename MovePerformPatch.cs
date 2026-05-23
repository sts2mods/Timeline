// Monster move boundary. We don't emit a row for the move itself —
// the AttackCommand / PowerCmd hooks already capture the move's
// concrete effects with the enemy as their attribution.
//
// What we DO at this boundary: claim the moving monster as the
// active cause for the duration of its move. Without this, effects
// emitted by the move with no explicit cause (CreatureCmd.GainBlock
// gives Cause=None, for example) would render with the system
// fallback icon instead of the enemy who's actually performing the
// move. Powers that flash mid-move (Curl Up, etc.) re-claim sticky
// cause via PowerFlash and override us, which is the correct
// behaviour — the power is a more specific source than the monster.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
public static class MonsterModel_PerformMove_Patch
{
    static void Prefix(MonsterModel __instance)
    {
        if (!TimelineMod.Enabled || __instance == null) return;
        try
        {
            EventFrameStack.SetActiveCause(TimelineEmit.CreatureActor(__instance.Creature));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}MonsterModel.PerformMove prefix: {ex.Message}");
        }
    }
}
