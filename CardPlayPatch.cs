// Hook into the unified card-play entry point so both *manual* plays
// (Player drags a card → PlayCardAction.Execute calls
// card.OnPlayWrapper(..., isAutoPlay: false, ...)) AND *auto* plays
// (CardCmd.AutoPlay → card.OnPlayWrapper(..., isAutoPlay: true)) get
// the card stamped as the active cause for nested effects.
//
// Cards that resolve to NO effects (Whirlwind with 0 energy left,
// any X-cost at 0, a power that fizzles) wouldn't otherwise show in
// the timeline — none of the resolution patches fire. We snapshot
// the event count at prefix, and if no rows landed during the play
// we add a CardPlayed marker so the player can still see the card
// was used.
using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
public static class CardModel_OnPlayWrapper_Patch
{
    static void Prefix(CardModel __instance, Creature? target, out int __state)
    {
        __state = TimelineLog.Events.Count;
        if (!TimelineMod.Enabled || __instance == null) return;
        try
        {
            // Sticky attribution: every block/damage/power-applied/draw
            // emitted during this card's resolution will pick up the
            // card as its cause via TimelineEmit.Leaf's override.
            EventFrameStack.SetActiveCause(TimelineEmit.CardActor(__instance));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}OnPlayWrapper prefix: {ex.Message}");
        }
    }

    static void Postfix(CardModel __instance, Creature? target, int __state, ref Task __result)
    {
        if (!TimelineMod.Enabled || __instance == null) return;
        if (__result == null) return;
        var orig = __result;
        __result = AwaitThenMaybeEmit(orig, __instance, target, __state);
    }

    private static async Task AwaitThenMaybeEmit(Task task, CardModel card, Creature? target, int countBefore)
    {
        try { await task; }
        finally
        {
            try
            {
                // Nothing emitted between the prefix snapshot and now
                // means the card resolved to a no-op (X-cost at 0 energy,
                // a fizzle). Drop a CardPlayed marker so the row stream
                // still reflects that the card was used.
                if (TimelineLog.Events.Count == countBefore)
                {
                    TimelineEmit.Leaf(new TimelineEvent
                    {
                        Effect = EffectKind.CardPlayed,
                        Cause = TimelineEmit.CardActor(card),
                        Target = target != null ? TimelineEmit.CreatureActor(target) : TimelineActor.None,
                    });
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{TimelineMod.LogPrefix}OnPlayWrapper postfix: {ex.Message}");
            }
        }
    }
}
