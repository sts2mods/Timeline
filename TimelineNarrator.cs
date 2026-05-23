// Turns a TimelineEvent into a short, natural-language sentence for
// the hover tooltip, mirroring the in-game hover-tip pattern of
// "Title" + "Description" lines.
//
// Two parts:
//   • Title  — the headline shown in gold. Something like "+2 Strength"
//              or "8 Damage" — readable at a glance.
//   • Detail — a full sentence describing who did what to whom, with
//              the cause/target names already resolved. Description
//              is BBCode-formatted: keywords (power names, card names,
//              etc.) wrapped in [color=#EFC851]…[/color] gold and
//              numbers in [color=#87CEEB]…[/color] blue to match the
//              game's hover-tip palette.
using System.Text;

namespace Timeline;

public static class TimelineNarrator
{
    // Matches the game's StsColors palette so the hover tip reads as
    // an in-game element.
    private const string GoldHex = "EFC851";
    private const string BlueHex = "87CEEB";

    private static string Gold(string s) => string.IsNullOrEmpty(s) ? s : $"[color=#{GoldHex}]{s}[/color]";
    private static string Blue(string s) => string.IsNullOrEmpty(s) ? s : $"[color=#{BlueHex}]{s}[/color]";
    private static string Num(int n) => Blue(n.ToString());
    private static string SignedNum(int n) => Blue((n > 0 ? "+" : "") + n.ToString());

    public static (string title, string detail) Describe(TimelineEvent ev)
    {
        var (title, detail) = ev.Effect switch
        {
            EffectKind.Damage             => DescribeDamage(ev),
            EffectKind.Block              => DescribeBlock(ev),
            EffectKind.PowerApplied       => DescribePowerApplied(ev),
            EffectKind.PowerRemoved       => DescribePowerRemoved(ev),
            EffectKind.CardAdded          => DescribeCardAdded(ev),
            EffectKind.CardRemoved        => DescribeCardRemoved(ev),
            EffectKind.CardDrawn          => DescribeCardDrawn(ev),
            EffectKind.EnergyGained       => DescribeEnergyGained(ev),
            EffectKind.EnergySpent        => DescribeEnergySpent(ev),
            EffectKind.AfflictionApplied  => DescribeAffliction(ev),
            EffectKind.HealApplied        => DescribeHeal(ev),
            EffectKind.CardUpgraded       => DescribeCardUpgraded(ev),
            EffectKind.Stunned            => DescribeStunned(ev),
            EffectKind.RelicTrigger       => DescribeRelicTrigger(ev),
            EffectKind.EnemyMove          => DescribeEnemyMove(ev),
            EffectKind.CardPlayed         => DescribeCardPlayed(ev),
            EffectKind.CombatStart        => ("Combat Start", "A new fight begins."),
            EffectKind.CombatEnd          => ("Combat End", "The fight concludes."),
            EffectKind.TurnStart          => DescribeTurnStart(ev),
            EffectKind.TurnEnd            => DescribeTurnEnd(ev),
            EffectKind.CreatureKilled     => DescribeKilled(ev),
            _                             => (ev.Effect.ToString(), ""),
        };

        // Append cross-kind extras (the lookback merge folds in
        // effects of DIFFERENT kinds when they share cause+target —
        // Whistle's damage+stun, Shrug It Off's block+draw, etc.).
        // The per-effect describer above only knows about same-kind
        // extras (e.g. PowerApplied collapses Tender's -Str/-Dex);
        // this post-step handles the mixed case so the tooltip
        // reflects the WHOLE row, not just the primary effect.
        if (ev.Extra.Count > 0)
        {
            var crossKind = new System.Collections.Generic.List<EffectEntry>();
            foreach (var e in ev.Extra)
                if (e.Effect != EffectKind.None && e.Effect != ev.Effect)
                    crossKind.Add(e);
            if (crossKind.Count > 0)
            {
                detail = AppendExtraClauses(detail, ev, crossKind);
                title = AppendExtraTitle(title, crossKind);
            }
        }
        return (title, detail);
    }

    private static string AppendExtraClauses(string detail, TimelineEvent ev, System.Collections.Generic.List<EffectEntry> extras)
    {
        var sb = new StringBuilder(detail);
        // Drop trailing period so we can chain.
        if (sb.Length > 0 && sb[sb.Length - 1] == '.') sb.Length--;
        sb.Append(" Also ");
        for (int i = 0; i < extras.Count; i++)
        {
            if (i > 0) sb.Append(i == extras.Count - 1 ? " and " : ", ");
            sb.Append(ExtraClause(extras[i], ev));
        }
        sb.Append('.');
        return sb.ToString();
    }

    private static string AppendExtraTitle(string title, System.Collections.Generic.List<EffectEntry> extras)
    {
        var sb = new StringBuilder(title);
        foreach (var e in extras)
            sb.Append(" + ").Append(ExtraTitleLabel(e));
        return sb.ToString();
    }

    private static string ExtraClause(EffectEntry e, TimelineEvent parent)
    {
        switch (e.Effect)
        {
            case EffectKind.Damage:
                if (e.HitCount.HasValue && e.HitCount > 1)
                    return $"dealt {Blue($"{e.Amount ?? 0}x{e.HitCount}")} {Gold("damage")}";
                return $"dealt {Num(e.Amount ?? 0)} {Gold("damage")}";
            case EffectKind.Block:
                return $"gave {Num(e.Amount ?? 0)} {Gold("block")}";
            case EffectKind.Stunned:
                return $"stunned {Gold(ObjectName(parent.Target))}";
            case EffectKind.HealApplied:
                return $"healed for {Num(e.Amount ?? 0)} {Gold("HP")}";
            case EffectKind.PowerApplied:
                {
                    var amt = e.Amount ?? 0;
                    var name = string.IsNullOrEmpty(e.Detail) ? "a power" : e.Detail!;
                    return amt != 0 ? $"applied {SignedNum(amt)} {Gold(name)}" : $"applied {Gold(name)}";
                }
            case EffectKind.PowerRemoved:
                return $"removed {Gold(string.IsNullOrEmpty(e.Detail) ? "a power" : e.Detail!)}";
            case EffectKind.CardAdded:
                return e.Amount.HasValue
                    ? $"added {Num(e.Amount.Value)} {Gold("card(s)")}"
                    : $"added a {Gold("card")}";
            case EffectKind.CardRemoved:
                return $"removed a {Gold("card")}";
            case EffectKind.CardDrawn:
                {
                    var amt = e.Amount ?? 1;
                    return $"drew {Num(amt)} {Gold(amt == 1 ? "card" : "cards")}";
                }
            case EffectKind.EnergyGained:
                return $"gained {Num(e.Amount ?? 0)} {Gold("energy")}";
            case EffectKind.EnergySpent:
                return $"spent {Num(e.Amount ?? 0)} {Gold("energy")}";
            case EffectKind.AfflictionApplied:
                return $"applied an {Gold("affliction")}";
            case EffectKind.CardUpgraded:
                return $"upgraded a {Gold("card")}";
            case EffectKind.RelicTrigger:
                return "triggered a relic";
            default:
                return e.Effect.ToString();
        }
    }

    private static string ExtraTitleLabel(EffectEntry e)
    {
        switch (e.Effect)
        {
            case EffectKind.Damage:
                if (e.HitCount.HasValue && e.HitCount > 1)
                    return $"{e.Amount ?? 0}x{e.HitCount} Damage";
                return $"{e.Amount ?? 0} Damage";
            case EffectKind.Block:           return $"+{e.Amount ?? 0} Block";
            case EffectKind.Stunned:         return "Stun";
            case EffectKind.HealApplied:     return $"+{e.Amount ?? 0} HP";
            case EffectKind.PowerApplied:
                {
                    var amt = e.Amount ?? 0;
                    var name = string.IsNullOrEmpty(e.Detail) ? "Power" : e.Detail!;
                    return amt != 0 ? $"{(amt > 0 ? "+" : "")}{amt} {name}" : name;
                }
            case EffectKind.PowerRemoved:    return $"-{(string.IsNullOrEmpty(e.Detail) ? "Power" : e.Detail!)}";
            case EffectKind.CardAdded:       return e.Amount.HasValue ? $"+{e.Amount} Card" : "Card";
            case EffectKind.CardRemoved:     return "-Card";
            case EffectKind.CardDrawn:       return $"Drew {e.Amount ?? 1}";
            case EffectKind.EnergyGained:    return $"+{e.Amount ?? 0} Energy";
            case EffectKind.EnergySpent:     return $"-{e.Amount ?? 0} Energy";
            case EffectKind.AfflictionApplied: return "Affliction";
            case EffectKind.CardUpgraded:    return "Upgrade";
            default:                         return e.Effect.ToString();
        }
    }

    // ---------- per-effect builders --------------------------------

    private static (string, string) DescribeDamage(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 0;
        var hits = ev.HitCount ?? 1;
        // "{damage-per-hit}x{repeat}" matches the game's own
        // attack-intent label format ("9x3" rather than "3× 9").
        var title = hits > 1
            ? $"{amt}x{hits} Damage"
            : (amt > 0 ? $"{amt} Damage" : "Damage");

        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "An effect").Append(" dealt ");
        if (hits > 1) detail.Append(Blue($"{amt}x{hits}")).Append(' ').Append(Gold("damage"));
        else detail.Append(Num(amt)).Append(' ').Append(Gold("damage"));
        if (!ev.Target.IsEmpty) detail.Append(" to ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        if (hits > 1) detail.Append(" (").Append(Num(hits * amt)).Append(" total)");
        return (title, detail.ToString());
    }

    private static (string, string) DescribeBlock(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 0;
        var title = amt > 0 ? $"+{amt} Block" : "Block";
        var detail = new StringBuilder();
        if (!ev.Cause.IsEmpty) Subject(detail, ev.Cause).Append(" gave ");
        else detail.Append("Gave ");
        detail.Append(ev.Target.IsEmpty ? "a target " : Gold(ObjectName(ev.Target)) + " ");
        detail.Append(Num(amt)).Append(' ').Append(Gold("block")).Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribePowerApplied(TimelineEvent ev)
    {
        // Collect all (amount, name) pairs — primary + any merged
        // extras — so Tender's combined "-1 Strength + -1 Dex" reads
        // as one sentence.
        var parts = new System.Collections.Generic.List<(int amt, string name)>();
        parts.Add((ev.Amount ?? 0, string.IsNullOrEmpty(ev.Detail) ? "a power" : ev.Detail));
        foreach (var e in ev.Extra)
            parts.Add((e.Amount ?? 0, string.IsNullOrEmpty(e.Detail) ? "a power" : e.Detail!));

        // Title is plain text (no BBCode) — the panel's Title label
        // is already styled gold.
        string title = parts.Count == 1
            ? FormatAmountPlain(parts[0])
            : string.Join(", ", parts.ConvertAll(FormatAmountPlain));

        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" applied ");
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) detail.Append(i == parts.Count - 1 ? " and " : ", ");
            var (amt, name) = parts[i];
            if (amt != 0) detail.Append(SignedNum(amt)).Append(' ');
            detail.Append(Gold(name));
        }
        if (!ev.Target.IsEmpty) detail.Append(" to ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (title, detail.ToString());
    }

    private static string FormatAmountPlain((int amt, string name) p)
    {
        if (p.amt == 0) return p.name;
        var sign = p.amt > 0 ? "+" : "";
        return $"{sign}{p.amt} {p.name}";
    }

    private static (string, string) DescribePowerRemoved(TimelineEvent ev)
    {
        var power = string.IsNullOrEmpty(ev.Detail) ? "a power" : ev.Detail;
        var amt = ev.Amount ?? 0;
        var title = amt > 0 ? $"-{amt} {power}" : $"-{power}";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" removed ");
        if (amt > 0) detail.Append(Num(amt)).Append(' ');
        detail.Append(Gold(power));
        if (!ev.Target.IsEmpty) detail.Append(" from ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeCardAdded(TimelineEvent ev)
    {
        var title = ev.Amount.HasValue ? $"+{ev.Amount} Card" : "Card Added";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" added ");
        if (ev.Amount.HasValue) detail.Append(Num(ev.Amount.Value)).Append(' ').Append(Gold("card(s)"));
        else detail.Append("a ").Append(Gold("card"));
        if (!ev.Target.IsEmpty) detail.Append(" to ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeCardRemoved(TimelineEvent ev)
    {
        // Detail carries the kind of removal: "exhausted",
        // "exhausted (ethereal)", "discarded", or empty for a plain
        // remove. Pick a matching verb so the row reads naturally.
        string detailWord = string.IsNullOrEmpty(ev.Detail) ? "removed" : ev.Detail!;
        string verb = detailWord.StartsWith("exhausted") ? "exhausted"
                    : detailWord == "discarded"          ? "discarded"
                    :                                       "removed";
        string title = verb switch
        {
            "exhausted" => "Card Exhausted",
            "discarded" => "Card Discarded",
            _           => "Card Removed",
        };
        var sb = new StringBuilder();
        Subject(sb, ev.Cause, fallback: "The game").Append(' ').Append(verb).Append(' ');
        sb.Append(ev.Target.IsEmpty ? Gold("a card") : Gold(ObjectName(ev.Target)));
        if (detailWord == "exhausted (ethereal)") sb.Append(" (ethereal)");
        sb.Append('.');
        return (title, sb.ToString());
    }

    private static (string, string) DescribeCardDrawn(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 1;
        var title = $"Drew {amt} Card{(amt == 1 ? "" : "s")}";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "The game").Append(" drew ");
        detail.Append(Num(amt)).Append(' ').Append(Gold(amt == 1 ? "card" : "cards"));
        if (!ev.Target.IsEmpty) detail.Append(" for ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeEnergyGained(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 0;
        var title = amt > 0 ? $"+{amt} Energy" : "Energy Gained";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" gave ");
        detail.Append(ev.Target.IsEmpty ? "a target" : Gold(ObjectName(ev.Target)));
        detail.Append(' ').Append(Num(amt)).Append(' ').Append(Gold("energy")).Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeEnergySpent(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 0;
        var title = amt > 0 ? $"-{amt} Energy" : "Energy Spent";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" spent ").Append(Num(amt)).Append(' ').Append(Gold("energy")).Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeStunned(TimelineEvent ev)
    {
        var title = "Stunned";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" stunned ");
        detail.Append(ev.Target.IsEmpty ? "a target" : Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeCardUpgraded(TimelineEvent ev)
    {
        // Detail carries the un-upgraded card name (set by the patch);
        // the target's Name carries the post-upgrade title with "+"
        // and is what the stacked reference card uses, so the player
        // still sees the upgraded card in the tooltip stack.
        string cardNames = NamesForMultiTarget(ev,
            single: !string.IsNullOrEmpty(ev.Detail)
                ? ev.Detail!
                : (ev.Target.IsEmpty ? "a card" : ev.Target.Name ?? "a card"));
        var title = "Card Upgraded";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" upgraded ").Append(cardNames).Append('.');
        return (title, detail.ToString());
    }

    // For events whose target was collapsed via multi-target merge
    // (Stone Cracker upgrading 2 cards, Thunderclap vulning 3 enemies),
    // produce a comma-joined gold-coloured name list. Falls back to
    // the single-target name when the event isn't collapsed, and
    // collapses again to the group placeholder ("37 cards") once the
    // list grows beyond the small-group threshold — listing every
    // card touched by a 37-card Hexed wouldn't fit anywhere.
    private const int MultiTargetNameCap = 5;
    private static string NamesForMultiTarget(TimelineEvent ev, string single)
    {
        if (ev.Targets.Count == 0) return Gold(single);
        if (ev.Targets.Count > MultiTargetNameCap)
            return Gold(ev.Target.Name ?? $"{ev.Targets.Count} targets");
        var sb = new StringBuilder();
        for (int i = 0; i < ev.Targets.Count; i++)
        {
            if (i > 0)
                sb.Append(i == ev.Targets.Count - 1 ? " and " : ", ");
            var name = ev.Targets[i].Name;
            sb.Append(Gold(string.IsNullOrEmpty(name) ? "a card" : name!));
        }
        return sb.ToString();
    }

    private static (string, string) DescribeHeal(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 0;
        var title = amt > 0 ? $"+{amt} HP" : "Heal";
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" healed ");
        if (!ev.Target.IsEmpty) detail.Append(Gold(ObjectName(ev.Target))).Append(" for ");
        detail.Append(Num(amt)).Append(' ').Append(Gold("HP")).Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeAffliction(TimelineEvent ev)
    {
        var amt = ev.Amount ?? 1;
        // Affliction identity lives in Detail (set by the patch). Cause
        // is the triggering power/card/enemy, separately attributed.
        var what = !string.IsNullOrEmpty(ev.Detail) ? ev.Detail! : "an affliction";
        var title = amt > 1 ? $"{amt}× {what}" : what;
        var detail = new StringBuilder();
        Subject(detail, ev.Cause, fallback: "Something").Append(" afflicted ");
        if (!ev.Target.IsEmpty) detail.Append(Gold(ObjectName(ev.Target)));
        else detail.Append("a card");
        detail.Append(" with ").Append(Num(amt)).Append(' ').Append(Gold(what)).Append('.');
        return (title, detail.ToString());
    }

    private static (string, string) DescribeRelicTrigger(TimelineEvent ev)
    {
        var name = ev.Cause.IsEmpty ? "A relic" : ev.Cause.Name ?? "A relic";
        return (name, $"{Gold(name)} triggered.");
    }

    private static (string, string) DescribeEnemyMove(TimelineEvent ev)
    {
        var who = ev.Cause.IsEmpty ? "An enemy" : ev.Cause.Name ?? "An enemy";
        var move = string.IsNullOrEmpty(ev.Detail) ? "performed a move" : $"used {Gold(ev.Detail!)}";
        return (who, $"{Gold(who)} {move}.");
    }

    private static (string, string) DescribeTurnStart(TimelineEvent ev)
    {
        string side = string.IsNullOrEmpty(ev.Detail) ? "" : ev.Detail!;
        string sideLower = side.ToLowerInvariant();
        if (side.Length == 0) return ("Turn Start", "A new turn begins.");
        return ($"{side} Turn", $"{Gold(side)} turn started.");
    }

    private static (string, string) DescribeTurnEnd(TimelineEvent ev)
    {
        string side = string.IsNullOrEmpty(ev.Detail) ? "" : ev.Detail!;
        if (side.Length == 0) return ("Turn End", "The current turn ends.");
        return ($"End of {side} Turn", $"{Gold(side)} turn ended.");
    }

    private static (string, string) DescribeKilled(TimelineEvent ev)
    {
        var name = ObjectName(ev.Target);
        return ($"{name} Killed", $"{Gold(name)} died.");
    }

    private static (string, string) DescribeCardPlayed(TimelineEvent ev)
    {
        var card = ev.Cause.IsEmpty ? "A card" : ev.Cause.Name ?? "A card";
        var detail = new StringBuilder();
        detail.Append(Gold(card)).Append(" was played");
        if (!ev.Target.IsEmpty) detail.Append(" targeting ").Append(Gold(ObjectName(ev.Target)));
        detail.Append('.');
        return (card, detail.ToString());
    }

    // ---------- helpers --------------------------------------------

    private static StringBuilder Subject(StringBuilder sb, TimelineActor actor, string fallback = "Something")
    {
        if (!actor.IsEmpty && !string.IsNullOrEmpty(actor.Name))
        {
            sb.Append(Gold(actor.Name!));
        }
        else
        {
            sb.Append(fallback);
        }
        return sb;
    }

    private static string ObjectName(TimelineActor actor)
    {
        if (actor.IsEmpty) return "a target";
        var name = actor.Name;
        if (string.IsNullOrEmpty(name))
        {
            return actor.Kind switch
            {
                ActorKind.Player => "the player",
                ActorKind.Enemy => "an enemy",
                _ => "a target",
            };
        }
        return name!;
    }
}
