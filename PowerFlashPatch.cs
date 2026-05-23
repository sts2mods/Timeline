// NPowerFlashVfx.Create is the "this power just fired" hook —
// PowerModel.Flash() ultimately spawns one of these. Mirrors the
// RelicFlash pattern: we don't emit a row for the flash itself, just
// claim the power as the active cause so any downstream
// PowerCmd.Apply / ModifyAmount that the power's body runs gets
// attributed to the power, not to the creature that originally
// applied the power.
//
// Example: Tender debuffs the player. When the player plays a card,
// Tender.AfterCardPlayed calls Flash() then PowerCmd.Apply<Strength>
// with applier=monster. Without this hook, the -1 Strength row reads
// "monster → -1 Strength → player". With it, the active cause is
// Tender, so it reads "Tender → -1 Strength → player".
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace Timeline;

[HarmonyPatch(typeof(NPowerFlashVfx), nameof(NPowerFlashVfx.Create))]
public static class NPowerFlashVfx_Create_Patch
{
    static void Prefix(PowerModel power)
    {
        if (!TimelineMod.Enabled || power == null) return;
        try
        {
            EventFrameStack.SetActiveCause(TimelineEmit.PowerActor(power));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}PowerFlash prefix: {ex.Message}");
        }
    }
}
