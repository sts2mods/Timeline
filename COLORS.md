# Timeline mod — color rules

Inferred from the game's localization data
(`localization/eng/*.json`), `StsColors.cs`, and the `diff` SmartFormat
formatter (`HighlightDifferencesFormatter` + `DynamicVar.ToHighlightedString`).
Hex codes mirror `MegaCrit.Sts2.Core.Helpers.StsColors`.

## Palette

| Tag                | Hex       | Role                                                                |
|--------------------|-----------|---------------------------------------------------------------------|
| `[gold]`           | `#EFC851` | Keywords, proper names, headers                                     |
| `[blue]`           | `#87CEEB` | Static / "this is the value" numbers in tooltip-style descriptions  |
| `[green]`          | `#7FFF00` | Buff diff (a `DynamicVar` modified **upward** from its base)        |
| `[red]`            | `#FF5555` | Debuff diff (a `DynamicVar` modified **downward**) + danger text    |
| `[purple]`         | `#EE82EE` | Enchantment text overlaid on cards                                  |
| `[orange]`         | `#FFA518` | Money / loot emphasis, escape-intent flavor                         |
| `[pink]`           | `#FF78A0` | Affliction-related emphasis                                         |
| `[aqua]`           | `#2AEBBE` | Reserved for special emphasis (rarely used)                         |
| `[cream]`          | `#FFF6E2` | Default body text colour (rarely written explicitly)                |

## When to use what

### Gold — names and keywords
- Game-defined keywords: `Block`, `Exhaust`, `Strength`, `Dexterity`,
  `Poison`, `Weak`, `Vulnerable`, `Doom`, etc.
- Pile names: `Hand`, `Draw Pile`, `Discard Pile`, `Exhaust Pile`.
- Proper names: card titles, relic titles, power titles,
  character/monster titles.
- Card type names referenced as keywords (`Attack`, `Skill`, `Power`).

Examples from loc:
```
"[gold]Poison[/gold] is triggered an additional time."
"At the start of your turn, put a random Attack from your [gold]Discard Pile[/gold] into your [gold]Hand[/gold]..."
"Increases attack damage by 1."   ← keyword not always wrapped when it's a common noun
```

### Blue — static numeric values
- Numbers in **power** smart descriptions ("deal [blue]{Amount}[/blue]
  damage").
- Numbers in **enemy intent** tooltips ("Aggressive — intends to Attack
  for 19 damage" — the `19` reads blue).
- Numbers in **enchantment** descriptions
  ("deals [blue]{Damage}[/blue] additional damage").
- Any value that represents "the current amount, as of now". This is
  the default colour for a numeric quantity that isn't being compared
  against anything.

### Green / Red — DynamicVar diff highlighting
- Triggered exclusively by SmartFormat's `:diff()` formatter on a
  `DynamicVar` in **card** descriptions.
  - Green → the var's PreviewValue is **higher** than its EnchantedValue
    (i.e. the card has been buffed: Strength on a damage card,
    upgrade preview, etc.).
  - Red → PreviewValue is **lower** (debuffed: Weakness on attack,
    enchantments that reduce damage).
  - No color (cream/default) → at base.
- So the **same numeric value** can appear differently across
  contexts:
  - Card body: "Deal [green]7[/green] damage." (you have +2 Strength)
  - Power smart desc: "...deals [blue]2[/blue] additional damage."
  - Event row: "Iron Wave dealt [blue]7[/blue] damage." (post-hoc, no
    diff context — just the static number)
- This is **not** an inconsistency. It's the rule: green/red carry
  information about deviation from base, blue carries the static
  current value.

### Purple, orange, pink, aqua
- Purple: enchantment-extra-text wraps the entire enchantment line.
- Orange: small-value loot/money phrasing in flavour ("Money!"),
  escape-intent dialogue.
- Pink: affliction-flavor emphasis (rare; mostly appears in dialogue).
- Aqua: reserved; sparing usage.

## Timeline-mod conventions (narrator output)

The `TimelineNarrator` builds prose for the hover-tooltip detail line.
It is a **tooltip-style** description (post-hoc, "this is what
happened"), so it follows the **power smartDescription** convention:

| Element                                                 | Color |
|---------------------------------------------------------|-------|
| Cause name (Tender, Brimstone, Iron Wave, …)            | gold  |
| Target name (The Ironclad, Hunter Killer, …)            | gold  |
| Keyword (Strength, Block, Damage, energy, card, …)      | gold  |
| Numeric amount (damage dealt, block gained, +/- power)  | blue  |
| Multi-hit hit count (`4×`)                              | blue  |
| Total damage in the parenthetical (`(20 total)`)        | blue  |

**Diff highlighting is NOT applied** to narrator numbers. The narrator
describes what *did* happen, not what *would* change relative to a
base — so there's no "compared against what" to anchor a green/red
choice on.

When the in-game card description (which we surface as a stacked
reference card) contains `[green]` or `[red]` values, that's the
game's own diff highlighting and we preserve it as-is. So you may
see a card description reading "Deal [green]7[/green] damage" stacked
above a narrator line reading "Iron Wave dealt [blue]7[/blue] damage"
— that's the rule, applied consistently, not a bug.

## Tag rewriting

The standard Godot `RichTextLabel` only knows `[color=#rrggbb]`. The
game's text uses unqualified names (`[gold]`, `[green]`, …) that
`MegaRichTextLabel` resolves via the theme. The mod converts at
display time in `TimelineEmit.ConvertGameTags`:

- Color tags → `[color=#hex]…[/color]` from the palette above.
- `[lb]` / `[rb]` → literal `[` / `]`.
- Animation tags (`[jitter]`, `[shake]`, `[sine]`) → wrapper dropped,
  inner text preserved (Godot's standard label has no equivalent).
- Standard BBCode (`[b]`, `[i]`, `[center]`, `[code]`, `[img]`) is left
  alone — Godot handles those natively.

Any future tag introduced by the game can be added to the lookup table
in `ConvertGameTags` without touching narrator logic.
