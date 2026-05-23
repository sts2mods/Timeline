// Emit a CardAdded row whenever a card is generated and dropped
// into combat — Splash's chosen attack landing in hand, Reaper /
// Genetic Algorithm style "create a copy of X", curse-generating
// enemy moves dumping Wounds into your draw pile, etc.
//
// The single-card AddGeneratedCardToCombat forwards to the
// IEnumerable AddGeneratedCardsToCombat, so we only need to
// hook the leaf to catch every path.
using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch]
public static class CardPileCmd_AddGeneratedCardsToCombat_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = typeof(CardPileCmd);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "AddGeneratedCardsToCombat") continue;
            var ps = m.GetParameters();
            if (ps.Length < 1) continue;
            if (typeof(IEnumerable<CardModel>).IsAssignableFrom(ps[0].ParameterType))
                yield return m;
        }
    }

    static void Prefix(IEnumerable<CardModel> cards)
    {
        if (!TimelineMod.Enabled || cards == null) return;
        try
        {
            foreach (var card in cards)
            {
                if (card == null) continue;
                TimelineEmit.Leaf(new TimelineEvent
                {
                    Effect = EffectKind.CardAdded,
                    Cause = TimelineActor.None,
                    Target = TimelineEmit.CardActor(card),
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}AddGeneratedCardsToCombat prefix: {ex.Message}");
        }
    }
}
