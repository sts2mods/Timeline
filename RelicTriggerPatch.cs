// NRelicFlashVfx.Create is the universal "this relic just triggered"
// hook — the game spawns one of these every time a relic activates.
//
// We DON'T emit a row for the trigger itself. Instead the relic
// becomes the *active cause* on the frame stack so the relic's
// downstream effects (PowerCmd.Apply, PlayerCmd.GainEnergy, etc.) get
// attributed to it in their own rows. This is "sticky": the relic
// stays the active cause until another relic flashes, a card is
// played, or a turn boundary clears it.
//
// We only patch the no-target overload because the target overload
// (NRelicFlashVfx.Create(RelicModel, Creature)) internally calls it.
// Patching both would set the cause twice for the same flash.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace Timeline;

[HarmonyPatch(typeof(NRelicFlashVfx), nameof(NRelicFlashVfx.Create), new[] { typeof(RelicModel) })]
public static class NRelicFlashVfx_Create_Patch
{
    static void Prefix(RelicModel relic)
    {
        if (!TimelineMod.Enabled || relic == null) return;
        try
        {
            EventFrameStack.SetActiveCause(TimelineEmit.RelicActor(relic));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}RelicFlash prefix: {ex.Message}");
        }
    }
}
