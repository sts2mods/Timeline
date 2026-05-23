// In-memory ring buffer of combat events. Cleared at combat start so
// each fight starts with an empty log. Events flow in via Harmony
// patches throughout the assembly and out via the overlay's render
// pass.
//
// Each event has three semantic slots:
//   Cause  — what triggered it (a card, relic, enemy, power, ...)
//   Effect — what happened (damage, block, power applied, draw, ...)
//   Target — what the effect landed on (player, an enemy, deck, ...)
// The row UI renders these as small icons; the hover tooltip
// expands the same fields into prose. Patches don't need to format
// strings — just fill the slots and the renderer handles display.
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

public enum ActorKind
{
    None,
    Card,       // card a player or enemy played
    Relic,      // a relic that triggered
    Enemy,      // enemy creature
    Player,     // player creature
    Power,      // a power triggering (e.g. Vulnerable boosting damage)
    Potion,     // a potion the player drank
    System,     // generic / combat infrastructure
}

public enum EffectKind
{
    None,
    Damage,
    Block,
    PowerApplied,
    PowerRemoved,
    CardAdded,
    CardRemoved,
    CardDrawn,
    EnergyGained,
    EnergySpent,
    AfflictionApplied,
    RelicTrigger,
    HealApplied,
    CardUpgraded,
    Stunned,
    // Structural markers — rendered without a cause/target.
    CombatStart,
    CombatEnd,
    TurnStart,
    TurnEnd,
    EnemyMove,
    CardPlayed,
    CreatureKilled,
}

public struct TimelineActor
{
    public ActorKind Kind;
    public string? Name;        // human-readable label
    public string? Id;          // fully-qualified type name or model id (for icon lookup)
    public Texture2D? Icon;     // pre-resolved icon
    public string? Description; // pre-resolved Smart/Description, surfaced as a stacked reference card
    // > 1 when this actor represents a collapsed group (e.g. an
    // affliction applied to every card in hand should render as one
    // row with target.Count = handSize, not one row per card).
    public int Count;
    // Optional reference to the underlying game model (CardModel,
    // RelicModel, PowerModel, AfflictionModel, PotionModel). Lets the
    // tooltip call model.HoverTips at render time so the stacked
    // cards match exactly what the game would show when hovering
    // this entity directly — including the model's hand-curated
    // ExtraHoverTips and the per-keyword cards CardModel adds for
    // its Keywords enum. We deliberately don't scan descriptions for
    // gold-coloured words because the game doesn't either; that
    // path produced noise (Pantograph mentions "Boss" → game shows
    // no Boss card, but description-scan did).
    public AbstractModel? Model;

    public bool IsEmpty => Kind == ActorKind.None;

    public static TimelineActor None => new TimelineActor { Kind = ActorKind.None };

    public static TimelineActor Of(ActorKind kind, string? name, string? id = null, Texture2D? icon = null, string? description = null, AbstractModel? model = null) =>
        new TimelineActor { Kind = kind, Name = name, Id = id, Icon = icon, Description = description, Count = 1, Model = model };
}

public sealed class TimelineEvent
{
    public EffectKind Effect;
    public double TimestampMs;
    public TimelineActor Cause;
    public TimelineActor Target;
    public int? Amount;
    // When set, overrides the per-EffectKind icon lookup. Used by
    // PowerApplied so we render the actual power's icon (Strength,
    // Vulnerable, …) instead of a generic "buff" symbol.
    public Texture2D? EffectIcon;
    // Pre-resolved power/affliction description for the effect. Lets
    // the tooltip stack a generic "Strength: increases damage by 1"
    // card above the event-specific sentence.
    public string? EffectDescription;
    // The game model for the primary effect (a power for PowerApplied,
    // an affliction for AfflictionApplied). The tooltip pulls
    // model.HoverTips off this so chained tips (HexPower → Hexed →
    // Ethereal) come from the game's own curation rather than our
    // description-text scan.
    public AbstractModel? EffectModel;
    // For multi-hit attacks (Conflagration etc.) — render as "4×5"
    // instead of one row per hit. Null/0/1 = no multi-hit notation.
    public int? HitCount;
    // Additional effects that share this event's cause + target.
    // Tender applies -1 Strength AND -1 Dex to the player from one
    // trigger — the second effect gets folded in here so the row
    // renders as "Tender → -1 [str] -1 [dex] → player" instead of
    // two separate rows.
    public List<EffectEntry> Extra = new();
    // When the multi-target merge collapses N events into one
    // (Stone Cracker upgrading 2 cards, Thunderclap vulning 3
    // enemies, …), Target itself becomes a generic "N cards/enemies"
    // placeholder. This list keeps the individual target identities
    // so the tooltip can still name them and stack each card's
    // reference panel. Empty when the row has a single target.
    public List<TimelineActor> Targets = new();
    // Free-form detail string surfaced in the hover tooltip when the
    // structured slots aren't enough.
    public string Detail = "";
    public int ParentIndex = -1;
    public int IndentDepth;
}

public struct EffectEntry
{
    // The kind of effect for this extra item — lets a single timeline
    // row carry mixed effects (e.g. Shrug It Off granting Block AND
    // drawing a card from the same cause/target). The renderer uses
    // this to pick per-item icon and number colour.
    public EffectKind Effect;
    public int? Amount;
    public Texture2D? Icon;
    public int? HitCount;
    public string? Detail;
    public string? Description;
    // Optional model whose HoverTips feed the tooltip stack (see
    // TimelineActor.Model). For PowerApplied extras this is the
    // power; for affliction extras, the affliction.
    public AbstractModel? Model;
}

public static class TimelineLog
{
    private const int Capacity = 4096;
    private static readonly List<TimelineEvent> _events = new(Capacity);

    public static IReadOnlyList<TimelineEvent> Events => _events;

    public static void Clear() => _events.Clear();

    public static int Add(TimelineEvent ev)
    {
        if (ev == null) return -1;
        ev.TimestampMs = Godot.Time.GetTicksMsec();
        if (_events.Count >= Capacity)
            _events.RemoveRange(0, Capacity / 2);
        _events.Add(ev);
        return _events.Count - 1;
    }
}
