// Emit a Stunned row whenever a creature gets stunned. Without
// this, Whistle's "deal damage, stun the enemy" only showed the
// damage half — stun goes through its own CreatureCmd.Stun
// pipeline and never touches PowerCmd / DamageCmd.
//
// The 2-arg overload of Stun forwards to the 3-arg one, so we
// only need to patch the leaf to catch every path (cards,
// monster-self-stuns, etc.). The lookback merge in
// TimelineEmit.Leaf will fold this event onto the same row as
// the preceding damage when both share a cause + target, so
// Whistle reads as one row: card → damage stun → enemy.
using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Timeline;

[HarmonyPatch]
public static class CreatureCmd_Stun_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Stun") continue;
            // The leaf overload is the 3-arg one (creature, stunMove,
            // nextMoveId). Every caller (cards, monsters) routes
            // through it.
            if (m.GetParameters().Length == 3) yield return m;
        }
    }

    static void Prefix(Creature creature)
    {
        if (!TimelineMod.Enabled || creature == null) return;
        try
        {
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.Stunned,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CreatureActor(creature),
                Detail = "Stun",
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}Stun prefix: {ex.Message}");
        }
    }
}
