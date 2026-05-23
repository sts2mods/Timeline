// Card exhaust / discard / removal events. Without these, the
// timeline silently swallows Ethereal cards exhausting at turn end,
// Stoke+'s exhaust, hand-wide discards, and so on. We emit a
// CardRemoved row tagged with the kind of removal so the narrator
// can say "exhausted X" vs "discarded X".
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Exhaust))]
public static class CardCmd_Exhaust_Patch
{
    // Save the current ActiveCause into __state in the Prefix so the
    // Postfix wrapper can put it back after Exhaust completes. Why:
    // Exhaust fires the AfterCardExhausted hook, which can flash a
    // relic (Burning Sticks etc.) and set ActiveCause to that relic.
    // Without restoring, the next Exhaust call in the caller's loop
    // (Stoke exhausting hand) inherits the relic as cause — turning
    // "Stoke exhausted 4 cards" into "Burning Sticks exhausted 3
    // cards" + one orphaned Stoke row.
    static void Prefix(CardModel card, bool causedByEthereal, out TimelineActor __state)
    {
        __state = EventFrameStack.ActiveCause;
        if (!TimelineMod.Enabled || card == null) return;
        try
        {
            // causedByEthereal lets the narrator distinguish
            // "Ethereal exhausted X" (passive end-of-turn behaviour)
            // from a card / power forcing the exhaust.
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.CardRemoved,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CardActor(card),
                Detail = causedByEthereal ? "exhausted (ethereal)" : "exhausted",
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}Exhaust prefix: {ex.Message}");
        }
    }

    static void Postfix(TimelineActor __state, ref Task __result)
    {
        if (!TimelineMod.Enabled || __result == null) return;
        var orig = __result;
        __result = ResolutionPatchHelpers.RestoreActiveCauseAfter(orig, __state);
    }
}

// CardCmd.Discard funnels into DiscardAndDraw(cards, cardsToDraw).
// Patching the funnel rather than each Discard overload covers both
// individual and bulk discards (end-of-turn hand dump, Headbutt-
// style targeted discards, …) with a single hook.
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.DiscardAndDraw))]
public static class CardCmd_DiscardAndDraw_Patch
{
    // Save/restore ActiveCause for the same reason as Exhaust:
    // discarding triggers AfterCardDiscarded which can flash relics
    // and we don't want the relic leaking onto whatever comes next.
    static void Prefix(IEnumerable<CardModel> cardsToDiscard, out TimelineActor __state)
    {
        __state = EventFrameStack.ActiveCause;
        if (!TimelineMod.Enabled || cardsToDiscard == null) return;
        try
        {
            foreach (var card in cardsToDiscard)
            {
                if (card == null) continue;
                TimelineEmit.Leaf(new TimelineEvent
                {
                    Effect = EffectKind.CardRemoved,
                    Cause = TimelineActor.None,
                    Target = TimelineEmit.CardActor(card),
                    Detail = "discarded",
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}DiscardAndDraw prefix: {ex.Message}");
        }
    }

    static void Postfix(TimelineActor __state, ref Task __result)
    {
        if (!TimelineMod.Enabled || __result == null) return;
        var orig = __result;
        __result = ResolutionPatchHelpers.RestoreActiveCauseAfter(orig, __state);
    }
}
