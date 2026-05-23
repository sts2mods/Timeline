// Emit a CardUpgraded row for every card that gets upgraded mid-run.
// Stone Cracker upgrades 2 random cards in the draw pile at combat
// start; Armaments / Apotheosis / etc. upgrade during play. Without
// a hook here the upgrades were invisible in the timeline.
//
// CardCmd.Upgrade has two overloads; the single-card overload just
// forwards to the IEnumerable one, so we patch only the leaf.
using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

[HarmonyPatch]
public static class CardCmd_Upgrade_Patch
{
    // Cards that the wrapper has decided to upgrade, captured in
    // Prefix while they're still upgradable. We emit them in Postfix
    // so the tooltip's CardActor.Description reads the POST-upgrade
    // description (showing the +1 values the player just earned).
    private static List<CardModel>? _pendingUpgrades;

    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = typeof(CardCmd);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Upgrade") continue;
            var ps = m.GetParameters();
            if (ps.Length < 1) continue;
            // The leaf overload takes IEnumerable<CardModel>; the
            // single-card overload (CardModel, CardPreviewStyle)
            // forwards to it, so we only need the IEnumerable one.
            if (typeof(IEnumerable<CardModel>).IsAssignableFrom(ps[0].ParameterType))
                yield return m;
        }
    }

    static void Prefix(IEnumerable<CardModel> cards)
    {
        if (!TimelineMod.Enabled || cards == null) return;
        try
        {
            var list = new List<CardModel>();
            foreach (var card in cards)
            {
                if (card == null) continue;
                if (!card.IsUpgradable) continue;
                // Splash/Quasar/etc. generate temporary preview cards
                // (Pile is null because they haven't been added to any
                // pile yet) and call Upgrade on them BEFORE the player
                // chooses one. Those aren't "real" deck upgrades the
                // player cares about — only emit for cards actually
                // sitting in a real pile.
                if (card.Pile == null) continue;
                list.Add(card);
            }
            _pendingUpgrades = list;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}CardCmd.Upgrade prefix: {ex.Message}");
            _pendingUpgrades = null;
        }
    }

    static void Postfix()
    {
        var list = _pendingUpgrades;
        _pendingUpgrades = null;
        if (list == null) return;
        try
        {
            foreach (var card in list)
            {
                if (card == null) continue;
                // Use TitleLocString (the raw card name) for the
                // narrator's "upgraded X" sentence. Card.Title would
                // append "+" / "+N" since we run in Postfix after
                // the upgrade, producing the awkward "upgraded Bash+"
                // reading where Bash+ is actually the result.
                string baseName = TimelineEmit.SafeLoc(card.TitleLocString) ?? card.GetType().Name;
                TimelineEmit.Leaf(new TimelineEvent
                {
                    Effect = EffectKind.CardUpgraded,
                    Cause = TimelineActor.None,
                    Target = TimelineEmit.CardActor(card),
                    Detail = baseName,
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}CardCmd.Upgrade postfix: {ex.Message}");
        }
    }
}
