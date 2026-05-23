// PowerRemoved rows. PowerCmd.Remove(power) is called both when a
// power's stack hits 0 via decrement and when an effect forcibly
// strips a power. We currently log the -1 ticks (via ModifyAmount)
// but the removal itself disappears — the timeline shows "Weak
// applied -1 Weak" rows and then the icon just stops without an
// explicit goodbye row. This emits the goodbye.
//
// The Remove path has no applier / cardSource — we let the sticky
// ActiveCause fill it in if anything is active; otherwise the row
// attributes to the power itself ("Weak expired on minion").
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Remove), new[] { typeof(PowerModel) })]
public static class PowerCmd_Remove_Patch
{
    static void Prefix(PowerModel? power)
    {
        if (!TimelineMod.Enabled || power == null) return;
        try
        {
            string powerDisplay = TimelineEmit.SafeLoc(power.Title) ?? power.GetType().Name;
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.PowerRemoved,
                Cause = TimelineEmit.PowerActor(power),
                Target = TimelineEmit.CreatureActor(power.Owner),
                EffectIcon = TimelineIcons.TryLoad(() => power.Icon),
                EffectDescription = TimelineEmit.PowerDescription(power),
                EffectModel = power,
                Detail = powerDisplay,
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}PowerRemove prefix: {ex.Message}");
        }
    }
}
