// Non-attack damage and creature-death events.
//
// AttackCommand is what we already patch — it groups multi-hit
// attacks into a single row and reads the modified per-hit amount
// off DamageResults. But powers like Combust, Bleed, Burn-style
// self-damage, and percent-HP relics call CreatureCmd.Damage
// directly, bypassing AttackCommand entirely; those rows were
// silently missing.
//
// We patch CreatureCmd.Damage's leaf overload (the one all the
// forwarding overloads funnel into) and emit a Damage row per
// target. To avoid double-counting damage from a normal card
// attack — which goes AttackCommand → CreatureCmd.Damage — we gate
// emission behind an "in attack command" flag set by
// AttackCommand.Execute's Prefix and cleared by its Postfix
// wrapper. The game is single-threaded under Godot's main loop, so
// the static flag is safe.
//
// CreatureCmd.Kill produces a CreatureKilled marker so the row
// stream actually shows when an enemy dies, instead of just having
// the damage event followed by silence.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

internal static class AttackInFlight
{
    // > 0 while AttackCommand.Execute is running (incremented in
    // AttackCommand_Execute_Patch.Prefix in ResolutionPatches.cs,
    // decremented after the Task completes). CreatureCmd.Damage
    // calls during that window are already covered by the
    // AttackCommand-level emit and would double-row.
    public static int Depth;
}

[HarmonyPatch]
public static class CreatureCmd_Damage_Patch
{
    // The leaf overload — all the convenience overloads funnel into
    // it. Pinning to this exact signature avoids re-patching every
    // overload and producing duplicate rows.
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Damage") continue;
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            // (PlayerChoiceContext, IEnumerable<Creature>, decimal,
            //  ValueProp, Creature?, CardModel?)
            if (ps.Length != 6) continue;
            if (ps[1].ParameterType != typeof(IEnumerable<Creature>)) continue;
            if (ps[2].ParameterType != typeof(decimal)) continue;
            if (ps[4].ParameterType != typeof(Creature)) continue;
            if (ps[5].ParameterType != typeof(CardModel)) continue;
            yield return m;
        }
    }

    static void Prefix(object[] __args)
    {
        if (!TimelineMod.Enabled) return;
        if (AttackInFlight.Depth > 0) return; // AttackCommand already handles it.
        try
        {
            IEnumerable<Creature>? targets = null;
            decimal amount = 0m;
            Creature? dealer = null;
            CardModel? cardSource = null;
            foreach (var a in __args)
            {
                switch (a)
                {
                    case IEnumerable<Creature> ts: targets ??= ts; break;
                    case decimal d: amount = d; break;
                    case Creature c: dealer = c; break;
                    case CardModel cm: cardSource = cm; break;
                }
            }
            if (targets == null) return;

            // Cause: prefer the card (cardSource), then the dealer
            // creature, then fall back to whatever ActiveCause is
            // (powers like Combust call Damage with both dealer and
            // cardSource null — their Flash sets ActiveCause).
            TimelineActor cause = cardSource != null
                ? TimelineEmit.CardActor(cardSource)
                : dealer != null
                    ? TimelineEmit.CreatureActor(dealer)
                    : TimelineActor.None;

            foreach (var t in targets)
            {
                if (t == null) continue;
                TimelineEmit.Leaf(new TimelineEvent
                {
                    Effect = EffectKind.Damage,
                    Cause = cause,
                    Target = TimelineEmit.CreatureActor(t),
                    Amount = TimelineEmit.ToInt(amount),
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}CreatureCmd.Damage prefix: {ex.Message}");
        }
    }
}

[HarmonyPatch]
public static class CreatureCmd_Kill_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        // Pin to the single-creature Kill overload — the
        // IReadOnlyCollection version internally awaits this one per
        // creature, so patching both would emit duplicates.
        foreach (var m in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Kill") continue;
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType != typeof(Creature)) continue;
            yield return m;
        }
    }

    static void Prefix(Creature creature)
    {
        if (!TimelineMod.Enabled || creature == null) return;
        try
        {
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.CreatureKilled,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CreatureActor(creature),
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}Kill prefix: {ex.Message}");
        }
    }
}
