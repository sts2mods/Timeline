// Resolution-side events — emit as leaves under whatever frame is
// currently open. Each helper fills the structured Cause/Target/
// Amount slots so the renderer can build icon-first rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

internal static class TimelineEmit
{
    public static void Leaf(TimelineEvent ev)
    {
        try
        {
            ev.ParentIndex = EventFrameStack.CurrentParent;
            ev.IndentDepth = EventFrameStack.Depth;
            // Sticky cause attribution: if the emit didn't fill in a
            // specific cause OR only knows "Player" / "Power" (which
            // are too generic to tell *what* caused it), inherit the
            // active source (the most recently flashed relic, played
            // card, etc.).
            if (NeedsOverride(ev.Cause) && !EventFrameStack.ActiveCause.IsEmpty)
                ev.Cause = EventFrameStack.ActiveCause;

            // Fold into a prior event when it's mergeable. Three
            // scenarios collapse to a single row:
            //   • Same cause + target + effect (buff/debuff stacking)
            //     — Tender's "-1 Str AND -1 Dex" reads as one row.
            //   • Same cause + target + DIFFERENT effect — Shrug It
            //     Off granting Block AND drawing a card reads as
            //     "Shrug It Off → 5 Block 1 Card → player".
            //   • Same cause + same effect + DIFFERENT target of the
            //     SAME KIND — an affliction landing on every card in
            //     hand collapses to one row with target.Count = N
            //     instead of N rows.
            // The lookback stops at structural events or events with
            // a different cause so we never reach across a turn/card
            // boundary.
            var events = TimelineLog.Events;
            int limit = System.Math.Max(0, events.Count - MergeLookback);
            for (int i = events.Count - 1; i >= limit; i--)
            {
                var prev = events[i];
                if (IsStructural(prev.Effect)) break;
                // Skip — not break — on cause mismatch. Used to break,
                // but that prevented Stoke's run of exhausts from
                // multi-target-merging once Burning Sticks' relic add
                // event (different cause) slipped between them. The
                // inner CanMerge / CanMergeMultiTarget calls already
                // require matching cause, so skipping is safe; we're
                // bounded by MergeLookback either way.
                if (!SameActor(prev.Cause, ev.Cause)) continue;

                // Same-target merge (Extra path).
                if (CanMerge(prev, ev))
                {
                    prev.Extra.Add(new EffectEntry
                    {
                        Effect = ev.Effect,
                        Amount = ev.Amount,
                        Icon = ev.EffectIcon,
                        HitCount = ev.HitCount,
                        Detail = ev.Detail,
                        Description = ev.EffectDescription,
                        Model = ev.EffectModel,
                    });
                    TimelinePanel.MarkRowDirty(i);
                    return;
                }

                // Multi-target merge: same cause, same effect, both
                // targets share a Kind but are distinct individuals.
                // Collapse them into a group target with a count.
                if (CanMergeMultiTarget(prev, ev))
                {
                    ExtendMultiTarget(prev, ev.Target);
                    TimelinePanel.MarkRowDirty(i);
                    return;
                }
            }
            TimelineLog.Add(ev);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}emit leaf: {ex.Message}");
        }
    }

    private static bool CanMerge(TimelineEvent prev, TimelineEvent next)
    {
        // CardPlayed / EnemyMove are header events ("this card was
        // played", "this enemy used X"); their follow-up effects
        // belong on their own row beneath the header.
        if (!IsMergeableEffect(prev.Effect) || !IsMergeableEffect(next.Effect))
            return false;
        if (!SameActor(prev.Cause, next.Cause)) return false;
        if (!SameActor(prev.Target, next.Target)) return false;
        // Different effect kinds → always merge (single source +
        // single target + multiple effects = one row).
        if (prev.Effect != next.Effect) return true;
        // Same effect kind → only merge stackable buff/debuff
        // shapes. Multi-hit damage already carries HitCount, and we
        // want separate Block / Energy events to stay distinct.
        return prev.Effect == EffectKind.PowerApplied
            || prev.Effect == EffectKind.PowerRemoved
            || prev.Effect == EffectKind.AfflictionApplied;
    }

    // Lookback window for "merge into an earlier event with matching
    // cause + target". Card patterns like Thunderclap interleave
    // (dmg→E1, dmg→E2, vuln→E1, vuln→E2), so the immediate previous
    // doesn't always match. 6 covers up to 3 enemies' interleaved
    // dual effects without reaching far enough back to collapse
    // unrelated history.
    private const int MergeLookback = 6;

    private static bool IsMergeableEffect(EffectKind e)
    {
        return e != EffectKind.CardPlayed && e != EffectKind.EnemyMove;
    }

    // Multi-target collapse: same cause, same effect, both targets
    // share the same Kind but are distinct individuals (or prev is
    // already a multi-group). Used to flatten "applied X to every
    // card in hand" / "upgraded 2 cards" / "applied vuln to all
    // enemies" from N rows into one.
    private static bool CanMergeMultiTarget(TimelineEvent prev, TimelineEvent next)
    {
        if (!IsMergeableEffect(prev.Effect) || !IsMergeableEffect(next.Effect)) return false;
        if (prev.Effect != next.Effect) return false;
        // Now that the lookback skips cause-mismatched events instead
        // of breaking on them, we need to verify cause equality here
        // too — otherwise a Burning Sticks add could fold into a Stoke
        // add run just because both targets are cards.
        if (!SameActor(prev.Cause, next.Cause)) return false;
        if (prev.Target.IsEmpty || next.Target.IsEmpty) return false;
        if (prev.Target.Kind != next.Target.Kind) return false;
        // Same individual: would have been caught by the regular
        // same-target merge or would already match — skip here.
        if (SameActor(prev.Target, next.Target)) return false;
        // Only the target-kinds that meaningfully come in groups —
        // a card affecting "all enemies" or "all cards in hand".
        // Player isn't ever a group in this game.
        return prev.Target.Kind == ActorKind.Card
            || prev.Target.Kind == ActorKind.Enemy;
    }

    private static void ExtendMultiTarget(TimelineEvent prev, TimelineActor incoming)
    {
        var t = prev.Target;
        if (t.Count > 1)
        {
            t.Count++;
            t.Name = FormatMultiTargetName(t.Kind, t.Count);
            prev.Target = t;
            prev.Targets.Add(incoming);
            return;
        }
        // First conversion: seed the per-target list with both the
        // original (which was prev.Target until now) and the new
        // incoming target, then swap prev.Target for the generic
        // "N cards / N enemies" placeholder used by the row icon.
        prev.Targets.Add(t);
        prev.Targets.Add(incoming);
        prev.Target = MakeMultiTarget(t.Kind, 2);
    }

    public static TimelineActor MakeMultiTarget(ActorKind kind, int count)
    {
        var icon = MultiTargetIcon(kind);
        return new TimelineActor
        {
            Kind = kind,
            Name = FormatMultiTargetName(kind, count),
            Id = $"__multi_{kind}__",
            Icon = icon,
            Count = count,
        };
    }

    private static string FormatMultiTargetName(ActorKind kind, int count)
    {
        return kind switch
        {
            ActorKind.Card => $"{count} cards",
            ActorKind.Enemy => $"{count} enemies",
            _ => $"{count} targets",
        };
    }

    private static Texture2D? MultiTargetIcon(ActorKind kind) => kind switch
    {
        ActorKind.Card => TimelineIcons.LoadOrNull("res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres")
            ?? TimelineIcons.LoadOrNull("res://images/packed/sprite_fonts/card_icon.png"),
        ActorKind.Enemy => TimelineIcons.LoadOrNull("res://images/ui/emote/skull.png"),
        _ => null,
    };

    private static bool IsStructural(EffectKind kind) => kind switch
    {
        EffectKind.CombatStart or EffectKind.CombatEnd or
        EffectKind.TurnStart or EffectKind.TurnEnd => true,
        _ => false,
    };

    private static bool SameActor(TimelineActor a, TimelineActor b)
    {
        if (a.IsEmpty && b.IsEmpty) return true;
        if (a.IsEmpty || b.IsEmpty) return false;
        if (a.Kind != b.Kind) return false;
        if (!string.IsNullOrEmpty(a.Id) || !string.IsNullOrEmpty(b.Id))
            return a.Id == b.Id;
        return a.Name == b.Name;
    }

    private static bool NeedsOverride(TimelineActor existing)
    {
        if (existing.IsEmpty) return true;
        // Player as cause = "the player did this" — true but unhelpful;
        // the relic / card / power that drove the action is the real
        // source.
        if (existing.Kind == ActorKind.Player) return true;
        // Power as cause is now always a fully-resolved specific
        // power (PowerCmd.Apply / ModifyAmount when applier=null —
        // duration ticks at end of turn, self-decay effects). Don't
        // overwrite it with whichever enemy happens to be acting:
        // a Weak / Vulnerable that ticked down on a minion isn't
        // "caused by" the Queen who's moving on the other side.
        // Enemy as cause should usually stick — enemy attacks really
        // are caused by the enemy. The ONE exception is when a Power
        // is currently flashing: its body is executing, the "applier"
        // creature on the underlying PowerCmd call is the original
        // applier of that power, not the conceptual cause (e.g.
        // Tender applies -1 Str via PowerCmd.Apply with applier=the
        // enemy that placed Tender, but the real source is Tender).
        //
        // Relics intentionally don't override Enemy — a relic's body
        // finishes synchronously at combat start, then enemies apply
        // their own start-of-combat powers, and those should be
        // attributed to the enemy (not the relic that triggered
        // earlier and left its sticky cause lying around).
        if (existing.Kind == ActorKind.Enemy)
        {
            var active = EventFrameStack.ActiveCause;
            if (active.Kind == ActorKind.Power) return true;
        }
        return false;
    }

    public static TimelineActor CreatureActor(Creature? c)
    {
        if (c == null) return TimelineActor.None;
        try
        {
            var kind = c.IsPlayer ? ActorKind.Player : ActorKind.Enemy;
            string name = c.IsPlayer
                ? SafeLoc(c.Player?.Character?.Title) ?? "Player"
                : SafeLoc(c.Monster?.Title) ?? c.GetType().Name;
            // Append CombatId so two segments of the same enemy type
            // (Decimillipede has three segments, each a separate
            // Creature) end up with distinct Ids and don't collapse
            // into one row via the SameActor merge check.
            string id = c.GetType().FullName ?? "";
            if (c.CombatId.HasValue) id = $"{id}#{c.CombatId.Value}";
            return TimelineActor.Of(kind, name, id, TimelineIcons.CreatureIcon(c));
        }
        catch { return TimelineActor.None; }
    }

    public static string? SafeLoc(MegaCrit.Sts2.Core.Localization.LocString? ls)
    {
        if (ls == null) return null;
        try { return ls.GetFormattedText(); }
        catch { return null; }
    }

    public static TimelineActor CardActor(CardModel? card)
    {
        if (card == null) return TimelineActor.None;
        try
        {
            // CardModel.Title returns the formatted string already.
            return TimelineActor.Of(
                ActorKind.Card,
                card.Title ?? card.GetType().Name,
                card.GetType().FullName,
                TimelineIcons.TryLoad(() => card.Portrait),
                CardTooltipDescription(card),
                card);
        }
        catch { return TimelineActor.None; }
    }

    public static TimelineActor RelicActor(RelicModel? relic)
    {
        if (relic == null) return TimelineActor.None;
        try
        {
            return TimelineActor.Of(
                ActorKind.Relic,
                SafeLoc(relic.Title) ?? relic.GetType().Name,
                relic.GetType().FullName,
                TimelineIcons.RelicIcon(relic),
                RelicTooltipDescription(relic),
                relic);
        }
        catch { return TimelineActor.None; }
    }

    public static TimelineActor PotionActor(PotionModel? potion)
    {
        if (potion == null) return TimelineActor.None;
        try
        {
            return TimelineActor.Of(
                ActorKind.Potion,
                SafeLoc(potion.Title) ?? potion.GetType().Name,
                potion.GetType().FullName,
                TimelineIcons.TryLoad(() => potion.Image),
                PotionTooltipDescription(potion),
                potion);
        }
        catch { return TimelineActor.None; }
    }

    public static string? PotionTooltipDescription(PotionModel potion)
    {
        try { return ConvertGameTags(SafeLoc(potion.DynamicDescription)); }
        catch { return null; }
    }

    public static TimelineActor PowerActor(PowerModel? power, string? fallbackName = null, string? fallbackFullName = null)
    {
        if (power == null)
        {
            return TimelineActor.Of(ActorKind.Power, fallbackName, fallbackFullName);
        }
        try
        {
            return TimelineActor.Of(
                ActorKind.Power,
                SafeLoc(power.Title) ?? power.GetType().Name,
                power.GetType().FullName,
                TimelineIcons.TryLoad(() => power.Icon),
                PowerDescription(power),
                power);
        }
        catch { return TimelineActor.None; }
    }

    public static string? PowerDescription(PowerModel power)
    {
        // Prefer SmartDescription (the in-game dynamic description with
        // current numbers) when the model has one; fall back to the
        // plain Description otherwise. Either way, populate the same
        // SmartFormat variables the game's hover tip pipeline uses
        // (Amount, the model's DynamicVars) so placeholders like
        // {Amount:cond:<0?Decreases|Increases} resolve to real text.
        try
        {
            var ls = power.HasSmartDescription ? power.SmartDescription : power.Description;
            try { ls.Add("Amount", power.Amount); } catch { }
            try { power.DynamicVars.AddTo(ls); } catch { }
            try { ls.Add("OnPlayer", power.Owner?.IsPlayer ?? false); } catch { }
            return ConvertGameTags(SafeLoc(ls));
        }
        catch { return null; }
    }

    // Pull the affliction's overlay graphic (the icon stamped on the
    // card itself) as its timeline icon. Same file the game uses for
    // the Hexed / Galvanized / etc. badge overlay.
    public static Texture2D? AfflictionIcon(MegaCrit.Sts2.Core.Models.AfflictionModel affliction)
    {
        try
        {
            var id = affliction.Id?.Entry?.ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) return null;
            return TimelineIcons.LoadOrNull($"res://images/vfx/ui/card/afflictions/{id}/ui_card_{id}_main.png");
        }
        catch { return null; }
    }

    public static string? AfflictionDescription(MegaCrit.Sts2.Core.Models.AfflictionModel affliction)
    {
        try
        {
            var ls = affliction.DynamicDescription;
            try { ls.Add("Amount", affliction.Amount); } catch { }
            return ConvertGameTags(SafeLoc(ls));
        }
        catch { return null; }
    }

    public static string? RelicTooltipDescription(RelicModel relic)
    {
        try
        {
            var ls = relic.DynamicDescription;
            try { relic.DynamicVars.AddTo(ls); } catch { }
            return ConvertGameTags(SafeLoc(ls));
        }
        catch { return null; }
    }

    public static string? CardTooltipDescription(CardModel card)
    {
        // The game's CardModel has its own description builder that
        // populates the full set of card-specific SmartFormat vars
        // (IfUpgraded, OnTable, InCombat, Block:diff(), GainsBlock,
        // energyPrefix, singleStarIcon, …). Trying to recreate that
        // by hand from outside is a losing game — use it directly.
        try
        {
            string raw = card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.None);
            return ConvertGameTags(raw);
        }
        catch
        {
            // Fall back to the basic LocString with minimum vars when
            // the full path errors (e.g. card has no Pile reference
            // yet).
            try
            {
                var ls = card.Description;
                try { card.DynamicVars.AddTo(ls); } catch { }
                return ConvertGameTags(SafeLoc(ls));
            }
            catch { return null; }
        }
    }

    // The game's descriptions use custom BBCode-style tags that its
    // MegaRichTextLabel resolves via theme colors and effect scripts.
    // Standard Godot RichTextLabel doesn't know those names, so we
    // rewrite them: colour tags → [color=#hex] from the StsColors
    // palette, and animation/decoration tags get stripped (the
    // wrapped text still renders, just without the wobble). [lb] /
    // [rb] are the game's escape for literal brackets.
    private static readonly System.Collections.Generic.Dictionary<string, string> _colorTags = new()
    {
        { "aqua",   "#2AEBBE" },
        { "blue",   "#87CEEB" },
        { "cream",  "#FFF6E2" },
        { "gold",   "#EFC851" },
        { "green",  "#7FFF00" },
        { "orange", "#FFA518" },
        { "pink",   "#FF78A0" },
        { "purple", "#EE82EE" },
        { "red",    "#FF5555" },
    };

    // Tags that wrap text we want to keep but whose effect we can't
    // honour (animation effects from MegaRichTextLabel that Godot's
    // standard RichTextLabel doesn't implement). We just drop the
    // wrapper and let the inner text render plain.
    private static readonly System.Collections.Generic.HashSet<string> _stripTags = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "jitter", "shake", "sine", "energy",
    };

    public static string? ConvertGameTags(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s!.Length);
        int i = 0;
        while (i < s!.Length)
        {
            int open = s.IndexOf('[', i);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);
            int close = s.IndexOf(']', open + 1);
            if (close < 0) { sb.Append(s, open, s.Length - open); break; }
            // Parse the tag body — name (and optional "/"), no args
            // (the game doesn't use [tag=value] in its color codes).
            string tagBody = s.Substring(open + 1, close - open - 1);
            string tag = tagBody.StartsWith("/") ? tagBody.Substring(1) : tagBody;
            bool isClose = tagBody.StartsWith("/");

            if (tag == "lb") sb.Append('[');
            else if (tag == "rb") sb.Append(']');
            else if (_colorTags.TryGetValue(tag.ToLowerInvariant(), out var hex))
                sb.Append(isClose ? "[/color]" : $"[color={hex}]");
            else if (_stripTags.Contains(tag))
                { /* drop the wrapper, keep inner text */ }
            else
                sb.Append('[').Append(tagBody).Append(']'); // leave [b]/[i]/[center] etc. as-is
            i = close + 1;
        }
        return sb.ToString();
    }

    public static int? ToInt(decimal d)
    {
        try { return (int)d; } catch { return null; }
    }
}

// PowerCmd.ModifyAmount runs when a power is applied to a creature
// that ALREADY has that power — instead of creating a new instance,
// the existing one's amount is adjusted. The Apply<T>(...) entry
// point branches into either Apply(...) (new) or ModifyAmount(...)
// (existing), so patching Apply alone misses stack changes — like
// Tender's -1 Strength when the player already has Strength from
// Brimstone. This patch covers the existing-stack case.
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount))]
public static class PowerCmd_ModifyAmount_Patch
{
    // Prefix captures the prior ActiveCause into __state so the
    // Postfix wrapper can restore it once the power's AfterApplied
    // (and any nested effects it emits) finish. Without the restore,
    // each Apply leaves its own power lingering as the sticky cause
    // and the NEXT Apply's Enemy cause gets overridden into the
    // previous power — turning "enemy applied Frail / Weak / Vuln"
    // into a confusing chain of "Frail applied Weak", "Weak applied
    // Vulnerable", etc.
    static void Prefix(PowerModel power, decimal offset, Creature? applier, out TimelineActor __state)
    {
        __state = EventFrameStack.ActiveCause;
        if (!TimelineMod.Enabled || power == null || offset == 0m) return;
        try
        {
            string powerDisplay = TimelineEmit.SafeLoc(power.Title) ?? power.GetType().Name;
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.PowerApplied,
                Cause = applier != null
                    ? TimelineEmit.CreatureActor(applier)
                    : TimelineEmit.PowerActor(power),
                Target = TimelineEmit.CreatureActor(power.Owner),
                Amount = TimelineEmit.ToInt(offset),
                EffectIcon = TimelineIcons.TryLoad(() => power.Icon),
                EffectDescription = TimelineEmit.PowerDescription(power),
                EffectModel = power,
                Detail = powerDisplay,
            });
            // Claim the power as the active cause for any downstream
            // effects fired from its body. Postfix restores __state
            // once those finish so adjacent Apply calls aren't
            // contaminated.
            EventFrameStack.SetActiveCause(TimelineEmit.PowerActor(power));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}ModifyAmount prefix: {ex.Message}");
        }
    }

    static void Postfix(TimelineActor __state, ref Task __result)
    {
        if (!TimelineMod.Enabled || __result == null) return;
        var orig = __result;
        __result = ResolutionPatchHelpers.RestoreActiveCauseAfter(orig, __state);
    }
}

[HarmonyPatch]
public static class PowerCmd_Apply_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        // Harmony can't patch open-generic method definitions; the
        // non-generic Apply(PowerModel, ...) still catches every
        // call site whose T isn't fixed at compile time.
        foreach (var m in typeof(PowerCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Apply") continue;
            if (m.IsGenericMethodDefinition) continue;
            yield return m;
        }
    }

    static void Prefix(MethodBase __originalMethod, object[] __args, out TimelineActor __state)
    {
        __state = EventFrameStack.ActiveCause;
        if (!TimelineMod.Enabled) return;
        try
        {
            string? powerName = null;
            string? powerFullName = null;
            PowerModel? powerModel = null;
            decimal amount = 0m;
            Creature? targetCreature = null;
            Creature? applier = null;

            if (__originalMethod is MethodInfo mi && mi.IsGenericMethod)
            {
                var ga = mi.GetGenericArguments();
                if (ga.Length > 0) { powerName = ga[0].Name; powerFullName = ga[0].FullName; }
            }

            for (int i = 0; i < __args.Length; i++)
            {
                switch (__args[i])
                {
                    case PowerModel pm:
                        powerModel = pm;
                        powerName ??= pm.GetType().Name;
                        powerFullName ??= pm.GetType().FullName;
                        break;
                    case decimal d:
                        amount = d;
                        break;
                    case Creature c:
                        if (targetCreature == null) targetCreature = c;
                        else applier ??= c;
                        break;
                }
            }

            string powerDisplay = powerModel != null
                ? (TimelineEmit.SafeLoc(powerModel.Title) ?? powerModel.GetType().Name)
                : (powerName ?? "");

            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.PowerApplied,
                Cause = applier != null
                    ? TimelineEmit.CreatureActor(applier)
                    : TimelineEmit.PowerActor(powerModel, powerName, powerFullName),
                Target = TimelineEmit.CreatureActor(targetCreature),
                Amount = TimelineEmit.ToInt(amount),
                // Show the actual power icon (Strength, Vulnerable, …)
                // in the effect column rather than a generic buff arrow.
                EffectIcon = powerModel != null
                    ? TimelineIcons.TryLoad(() => powerModel.Icon)
                    : null,
                EffectDescription = powerModel != null
                    ? TimelineEmit.PowerDescription(powerModel)
                    : null,
                EffectModel = powerModel,
                Detail = powerDisplay,
            });

            // Claim the power as the active cause for any downstream
            // effects fired from its AfterApplied body. HexPower for
            // example loops over every card in hand calling Afflict —
            // those rows should attribute to "HexPower", not to the
            // enemy who applied HexPower or to nothing. We do this
            // INSTEAD of relying on NPowerFlashVfx because powers
            // applied via PowerCmd.Apply don't always Flash before
            // their AfterApplied runs. The Postfix wraps the task so
            // the prior cause is restored once AfterApplied finishes —
            // otherwise the next Power Apply in the same move would
            // see this power lingering as ActiveCause and the
            // Enemy→Power override on its event would misattribute.
            if (powerModel != null)
            {
                EventFrameStack.SetActiveCause(TimelineEmit.PowerActor(powerModel, powerName, powerFullName));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}PowerCmd.Apply prefix: {ex.Message}");
        }
    }

    static void Postfix(TimelineActor __state, ref Task __result)
    {
        if (!TimelineMod.Enabled || __result == null) return;
        var orig = __result;
        __result = ResolutionPatchHelpers.RestoreActiveCauseAfter(orig, __state);
    }
}

internal static class ResolutionPatchHelpers
{
    // Wraps the original async Task: awaits it to completion, then
    // restores ActiveCause to whatever it was before the Prefix
    // claimed a new one. Catches inside the finally so a faulted
    // task still leaves global state clean.
    public static async Task RestoreActiveCauseAfter(Task orig, TimelineActor prev)
    {
        try { await orig; }
        finally
        {
            try { EventFrameStack.SetActiveCause(prev); }
            catch { /* don't let cleanup mask the original exception */ }
        }
    }
}

[HarmonyPatch]
public static class CreatureCmd_GainBlock_Patch
{
    // CreatureCmd.GainBlock has two overloads:
    //   • (Creature, BlockVar, CardPlay?, bool) — wraps and forwards
    //   • (Creature, decimal, ValueProp, CardPlay?, bool) — the leaf
    // Patching both produced double events (one with amount=0 from
    // the wrapper because the loop didn't see a `decimal` arg). Patch
    // only the leaf.
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GainBlock" || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length < 2 || ps[1].ParameterType != typeof(decimal)) continue;
            yield return m;
        }
    }

    // Postfix wraps the returned Task<decimal> so we can read the
    // *modified* block amount (after Dexterity / Reverberation / etc.)
    // rather than the raw input.
    static void Postfix(object[] __args, ref Task<decimal> __result)
    {
        if (!TimelineMod.Enabled || __result == null) return;
        var origTask = __result;
        Creature? target = null;
        foreach (var a in __args) { if (a is Creature c) { target = c; break; } }
        __result = AwaitAndEmit(origTask, target);
    }

    private static async Task<decimal> AwaitAndEmit(Task<decimal> task, Creature? target)
    {
        decimal modified = await task;
        try
        {
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.Block,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CreatureActor(target),
                Amount = TimelineEmit.ToInt(modified),
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}GainBlock postfix emit: {ex.Message}");
        }
        return modified;
    }
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
public static class PlayerCmd_GainEnergy_Patch
{
    static void Prefix(decimal amount, Player player)
    {
        if (!TimelineMod.Enabled) return;
        TimelineEmit.Leaf(new TimelineEvent
        {
            Effect = EffectKind.EnergyGained,
            Cause = TimelineActor.None,
            Target = TimelineEmit.CreatureActor(player?.Creature),
            Amount = TimelineEmit.ToInt(amount),
        });
    }
}

[HarmonyPatch]
public static class CardPileCmd_Draw_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        // CardPileCmd.Draw has two overloads:
        //   • Draw(PCC, Player)                 — wraps and forwards
        //   • Draw(PCC, decimal, Player, bool)  — the leaf
        // The wrapper calls the leaf with count=1, so patching both
        // produced two CardDrawn rows for a single draw call.
        // Patch only the leaf (the one with a `decimal` count param).
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardPileCmd");
        if (t == null) yield break;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Draw" || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length < 2 || ps[1].ParameterType != typeof(decimal)) continue;
            yield return m;
        }
    }

    static void Prefix(object[] __args)
    {
        if (!TimelineMod.Enabled) return;
        decimal count = 1m;
        Player? player = null;
        for (int i = 0; i < __args.Length; i++)
        {
            if (__args[i] is decimal d) count = d;
            else if (__args[i] is Player p) player = p;
        }
        TimelineEmit.Leaf(new TimelineEvent
        {
            Effect = EffectKind.CardDrawn,
            Cause = TimelineActor.None,
            Target = TimelineEmit.CreatureActor(player?.Creature),
            Amount = TimelineEmit.ToInt(count),
        });
    }
}

// Card and monster attacks go through DamageCmd.Attack(...) which
// builds an AttackCommand and Execute()s it. The builder loops
// `_hitCount` times calling CreatureCmd.Damage with the modified
// per-hit amount, and accumulates DamageResults. Patching at the
// AttackCommand level (rather than CreatureCmd.Damage) lets us:
//   • read post-modifier (Strength / Vulnerable) damage from the
//     DamageResult that the builder populated
//   • collapse multi-hit attacks (Conflagration: 4×) into one row
//     per target rather than one row per individual hit
[HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
public static class AttackCommand_Execute_Patch
{
    // Prefix bumps the "we're inside an AttackCommand" counter so
    // the CreatureCmd.Damage patch knows to skip emission — the
    // AttackCommand-level emit below already handles those hits.
    static void Prefix() => AttackInFlight.Depth++;

    static void Postfix(AttackCommand __instance, ref Task<AttackCommand> __result)
    {
        if (__result == null)
        {
            AttackInFlight.Depth = System.Math.Max(0, AttackInFlight.Depth - 1);
            return;
        }
        __result = AwaitAndEmit(__instance, __result);
    }

    private static async Task<AttackCommand> AwaitAndEmit(AttackCommand cmd, Task<AttackCommand> task)
    {
        try
        {
            var ret = await task;
            if (TimelineMod.Enabled)
            {
                try { Emit(cmd); }
                catch (Exception ex) { GD.PrintErr($"{TimelineMod.LogPrefix}AttackCommand.Execute emit: {ex.Message}"); }
            }
            return ret;
        }
        finally
        {
            AttackInFlight.Depth = System.Math.Max(0, AttackInFlight.Depth - 1);
        }
    }

    private static void Emit(AttackCommand cmd)
    {
        var grouped = new Dictionary<Creature, (int incoming, int hits)>();
        foreach (var hitResults in cmd.Results)
        {
            foreach (var r in hitResults)
            {
                if (r?.Receiver == null) continue;
                int incoming = r.BlockedDamage + r.UnblockedDamage;
                if (grouped.TryGetValue(r.Receiver, out var v))
                    grouped[r.Receiver] = (v.incoming + incoming, v.hits + 1);
                else
                    grouped[r.Receiver] = (incoming, 1);
            }
        }
        if (grouped.Count == 0) return;

        TimelineActor cause = cmd.ModelSource is CardModel card
            ? TimelineEmit.CardActor(card)
            : cmd.Attacker != null
                ? TimelineEmit.CreatureActor(cmd.Attacker)
                : TimelineActor.None;

        foreach (var kvp in grouped)
        {
            var (incoming, hits) = kvp.Value;
            int perHit = hits > 0 ? incoming / hits : incoming;
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.Damage,
                Cause = cause,
                Target = TimelineEmit.CreatureActor(kvp.Key),
                Amount = perHit,
                HitCount = hits > 1 ? hits : (int?)null,
            });
        }
    }
}

[HarmonyPatch]
public static class CardCmd_Afflict_Patch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardCmd");
        if (t == null) yield break;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Afflict") continue;
            if (m.IsGenericMethodDefinition) continue;
            yield return m;
        }
    }

    static void Prefix(object[] __args)
    {
        if (!TimelineMod.Enabled) return;
        try
        {
            decimal amount = 0m;
            CardModel? card = null;
            MegaCrit.Sts2.Core.Models.AfflictionModel? afflictionModel = null;
            string? afflictionName = null;
            string? afflictionFullName = null;
            foreach (var a in __args)
            {
                switch (a)
                {
                    case decimal d:
                        amount = d;
                        break;
                    case CardModel cm:
                        card = cm;
                        break;
                    case MegaCrit.Sts2.Core.Models.AfflictionModel am:
                        afflictionModel = am;
                        afflictionName = TimelineEmit.SafeLoc(am.Title) ?? am.GetType().Name;
                        afflictionFullName = am.GetType().FullName;
                        break;
                }
            }
            // The affliction (Hexed, Galvanized, …) is the EFFECT, not
            // the cause — it's what's being applied to the card. The
            // cause is whatever triggered this Afflict call (a power
            // like HexPower that loops over hand and applies Hexed).
            // Leave Cause empty so the sticky ActiveCause (set by the
            // power's flash) fills it in.
            var effectIcon = afflictionModel != null
                ? TimelineEmit.AfflictionIcon(afflictionModel)
                : null;
            var effectDesc = afflictionModel != null
                ? TimelineEmit.AfflictionDescription(afflictionModel)
                : null;
            TimelineEmit.Leaf(new TimelineEvent
            {
                Effect = EffectKind.AfflictionApplied,
                Cause = TimelineActor.None,
                Target = TimelineEmit.CardActor(card),
                Amount = TimelineEmit.ToInt(amount),
                Detail = afflictionName ?? "",
                EffectIcon = effectIcon,
                EffectDescription = effectDesc,
                EffectModel = afflictionModel,
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}CardCmd.Afflict prefix: {ex.Message}");
        }
    }
}
