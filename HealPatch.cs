// Emit a HealApplied row whenever a creature is healed. Without
// this, Blood Potion (which calls CreatureCmd.Heal directly with
// no PowerCmd / DamageCmd / GainBlock event in between) left no
// trace at all in the timeline — the player would drink it and
// the panel would do nothing.
//
// CreatureCmd.Heal is the single entry point for every heal in
// combat (potions, cards, rest sites that bleed into combat, etc.)
// so patching it here covers them all.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Timeline;

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
public static class CreatureCmd_Heal_Patch
{
    static void Prefix(Creature creature, decimal amount)
    {
        if (!TimelineMod.Enabled || creature == null) return;
        try
        {
            // Clamp the displayed amount to the creature's missing HP
            // — drinking Blood Potion at full health would otherwise
            // read as a huge percent-of-max heal that didn't actually
            // happen.
            decimal effective = System.Math.Min(amount, creature.MaxHp - creature.CurrentHp);
            if (effective <= 0) return;
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.HealApplied,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CreatureActor(creature),
                Amount = TimelineEmit.ToInt(effective),
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}Heal prefix: {ex.Message}");
        }
    }
}
