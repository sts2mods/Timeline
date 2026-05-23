// EnergySpent rows. PlayerCmd.LoseEnergy is called when cards are
// played (or X-cost cards drain remaining energy). We already emit
// EnergyGained for the gain path; this is the symmetric drain row.
//
// LoseEnergy itself early-returns on amount<=0 / combat ending, so
// we filter the same way to keep the timeline clean.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Timeline;

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseEnergy))]
public static class PlayerCmd_LoseEnergy_Patch
{
    static void Prefix(decimal amount, Player player)
    {
        if (!TimelineMod.Enabled) return;
        if (amount <= 0m) return;
        if (CombatManager.Instance.IsEnding) return;
        try
        {
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.EnergySpent,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CreatureActor(player?.Creature),
                Amount = TimelineEmit.ToInt(amount),
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}LoseEnergy prefix: {ex.Message}");
        }
    }
}
