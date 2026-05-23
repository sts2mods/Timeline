// Stamp the potion as the active cause for the duration of its
// OnUseWrapper. Without this, ActiveCause was still pointing at
// whatever Power / Card / Relic last fired, so the potion's
// block / power / draw events would inherit that cause and merge
// into the previous timeline row (e.g. Tender's body had just
// finished, so a Strength potion's "+2 Strength" was getting
// folded into the Tender row instead of producing its own).
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
public static class PotionModel_OnUseWrapper_Patch
{
    static void Prefix(PotionModel __instance, Creature? target)
    {
        if (!TimelineMod.Enabled || __instance == null) return;
        try
        {
            EventFrameStack.SetActiveCause(TimelineEmit.PotionActor(__instance));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}OnUseWrapper prefix: {ex.Message}");
        }
    }
}
