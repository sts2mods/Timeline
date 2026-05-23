// Floating hover popup styled like the game's status-condition tip
// (the panel you see when hovering Strength / Vulnerable / Frail
// etc.). We can't reliably instantiate the original scene from a
// patched runtime — the original uses MegaLabel / MegaRichTextLabel
// addons that fight back when reparented mid-frame — so we mimic the
// look with the same nine-patch texture, fonts, and colours.
//
// One tooltip can hold a STACK of mini-cards. Top of the stack:
// generic reference cards for each power/relic/card the row
// mentions (cause + each applied effect), so hovering "Tender
// applied -1 Strength and -1 Dex" surfaces the generic Strength,
// Dexterity, and Tender descriptions alongside the event-specific
// sentence — matching the way the game stacks tips when you hover a
// creature with multiple statuses.
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

public sealed partial class TimelineTooltip : VBoxContainer
{
    private const string BackplatePath = "res://images/ui/hover_tip.png";
    private const string TitleFontPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string BodyFontPath  = "res://themes/kreon_regular_glyph_space_one.tres";

    private const float OffsetX = 12f;
    private const float MaxDescriptionWidth = 320f;
    // Cap on how many individual-target reference cards we stack
    // when a row was multi-target-collapsed. Stone Cracker (2 cards)
    // stays per-card; Hexed-on-37-cards falls back to no per-target
    // breakdown to keep the popup screen-sized.
    private const int MultiTargetTooltipCap = 5;
    // Top bar in combat takes ~100px; keep a margin so the tooltip
    // never tucks underneath it.
    private const float TopInset = 110f;
    // Hand cards and energy/end-turn UI live in the bottom band of
    // the screen; keep the tooltip above them so a tall stack
    // doesn't overlap interactive elements the player needs to
    // click.
    private const float BottomInset = 220f;
    // Don't let the tooltip slide past the left edge of the screen
    // when a wide expanded panel pushes it far left.
    private const float LeftInset = 16f;

    private Control? _anchorPanel;
    // Remember the row we're following so we can reposition the
    // tooltip mid-flight when the panel toggles. Without this the
    // tooltip stays pinned to its old position while the parchment
    // slides underneath it.
    private Control? _hoveredRow;
    private static Texture2D? _cachedBackplate;
    private static Font? _cachedTitleFont;
    private static Font? _cachedBodyFont;

    public static TimelineTooltip Create(Control anchorPanel)
    {
        var tt = new TimelineTooltip
        {
            Name = "TimelineTooltip",
            Visible = false,
            ZIndex = 200,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        tt._anchorPanel = anchorPanel;
        tt.AddThemeConstantOverride("separation", 1);

        _cachedBackplate ??= TryLoad<Texture2D>(BackplatePath);
        _cachedTitleFont ??= TryLoad<Font>(TitleFontPath);
        _cachedBodyFont ??= TryLoad<Font>(BodyFontPath);

        var parent = anchorPanel.GetParent();
        parent?.AddChild(tt);
        return tt;
    }

    public void Show(TimelineEvent ev, string title, string description, Control hoveredRow)
    {
        if (_anchorPanel == null) return;

        // Rebuild from scratch each show — the stack varies per event
        // and Godot Controls are cheap to construct.
        foreach (var child in GetChildren()) child.QueueFree();

        foreach (var card in CollectReferences(ev))
            AddChild(BuildPanel(card.Title, card.Description, card.Icon, card.Card));

        AddChild(BuildPanel(title, description, null, null));

        _hoveredRow = hoveredRow;
        Visible = true;
        Callable.From(Reposition).CallDeferred();
    }

    public new void Hide()
    {
        Visible = false;
        _hoveredRow = null;
    }

    // Public so the panel can call this every frame while toggling /
    // dynamic-grow tweens are running, so the tooltip slides along
    // with the parchment instead of getting stranded.
    public void Reposition()
    {
        if (!Visible || _hoveredRow == null) return;
        if (_anchorPanel == null) return;
        if (!GodotObject.IsInstanceValid(_hoveredRow)) return;

        var panelRect = _anchorPanel.GetGlobalRect();
        var rowRect = _hoveredRow.GetGlobalRect();
        // GetCombinedMinimumSize is honest right after we rebuilt
        // the stack; Size lags by a frame because layout hasn't run
        // yet, which was causing tall tooltips to clamp to TopInset
        // instead of aligning with the hovered row.
        var size = GetCombinedMinimumSize();
        var viewportRect = GetViewport()?.GetVisibleRect() ?? new Rect2(0, 0, 1920, 1080);

        // Align the BOTTOM of the stack with the row's bottom — the
        // main event description sits at the bottom of the stack and
        // is what the user is hovering, so it should land at row
        // height; the reference cards then build upward above it.
        float targetY = rowRect.Position.Y + rowRect.Size.Y - size.Y;
        float minY = viewportRect.Position.Y + TopInset;
        float maxY = viewportRect.Position.Y + viewportRect.Size.Y - size.Y - BottomInset;
        if (maxY < minY) maxY = minY;
        targetY = Mathf.Clamp(targetY, minY, maxY);

        float targetX = panelRect.Position.X - size.X - OffsetX;
        // Clamp so the tooltip never slides past the left edge of
        // the viewport when the panel is wide and far to the right.
        float minX = viewportRect.Position.X + LeftInset;
        if (targetX < minX) targetX = minX;
        var globalPos = new Vector2(targetX, targetY);
        var parentControl = GetParent() as Control;
        Position = parentControl != null
            ? globalPos - parentControl.GlobalPosition
            : globalPos;
    }

    // Build the stacked tooltip cards using the game's own HoverTips
    // logic. Each model (card / relic / power / affliction / potion)
    // hand-curates an ExtraHoverTips list — Pantograph adds nothing,
    // HexPower adds the Hexed affliction tip, Hexed adds the Ethereal
    // keyword tip, Stoke with the Exhaust keyword adds the Exhaust
    // keyword tip once. Mirroring this means tooltips look the same
    // as anywhere else in the game, no description-text scan.
    private static IEnumerable<(string Title, string Description, Texture2D? Icon, CardModel? Card)> CollectReferences(TimelineEvent ev)
    {
        var seenIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // The actors that contribute to the stack, in render order
        // (cause first, then targets, then the primary effect, then
        // its mergeable extras). For each actor we emit:
        //   (1) the actor itself as a header card (with its icon)
        //   (2) then each tip from model.HoverTips beneath it
        // — matching the convention you see when hovering a card in
        // the game: the card's own tip on top, sub-tips below.
        foreach (var (actor, modelOverride) in EnumerateActors(ev))
        {
            // Actor's own panel — only when there's something
            // user-facing to show (skip empty actors and ones whose
            // description we couldn't resolve).
            if (!actor.IsEmpty
                && !string.IsNullOrEmpty(actor.Description)
                && !string.IsNullOrEmpty(actor.Name)
                && seenIds.Add("actor:" + actor.Name!))
            {
                yield return (actor.Name!, actor.Description!, actor.Icon, actor.Model as CardModel);
            }

            // Then the model's own HoverTips: ExtraHoverTips and any
            // hand-picked keyword / affliction cards the model wants
            // chained beneath itself.
            var model = modelOverride ?? actor.Model;
            if (model == null) continue;
            foreach (var tip in EnumerateHoverTips(model))
            {
                var display = ExtractDisplay(tip);
                if (display == null) continue;
                var (title, description, icon, id, card) = display.Value;
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(description)) continue;
                if (!seenIds.Add(id)) continue;
                yield return (title, description, icon, card);
            }
        }
    }

    // Iterates the actors a row contributes to the tooltip, top-down:
    // cause, then collapsed targets (or single target), then the
    // primary effect's model, then each mergeable Extra's model. The
    // tuple's second slot lets us pass a model for slots that aren't
    // a TimelineActor in their own right (EffectModel, Extra.Model).
    private static IEnumerable<(TimelineActor Actor, AbstractModel? Model)> EnumerateActors(TimelineEvent ev)
    {
        if (!ev.Cause.IsEmpty) yield return (ev.Cause, null);

        if (ev.Targets.Count > 0 && ev.Targets.Count <= MultiTargetTooltipCap)
        {
            foreach (var t in ev.Targets)
                if (t.Kind == ActorKind.Card) yield return (t, null);
        }
        else if (!ev.Target.IsEmpty && ev.Target.Kind == ActorKind.Card)
        {
            yield return (ev.Target, null);
        }

        if (ev.EffectModel != null)
        {
            // Synthesize a "header" actor for the primary effect so
            // its own card lands in the stack — the game shows the
            // power/affliction's tooltip before its sub-tips.
            var synth = new TimelineActor
            {
                Kind = ActorKind.Power,
                Name = ev.Detail,
                Description = ev.EffectDescription,
                Icon = ev.EffectIcon,
            };
            yield return (synth, ev.EffectModel);
        }

        foreach (var extra in ev.Extra)
        {
            if (extra.Model == null && string.IsNullOrEmpty(extra.Description)) continue;
            var synth = new TimelineActor
            {
                Kind = ActorKind.Power,
                Name = extra.Detail,
                Description = extra.Description,
                Icon = extra.Icon,
            };
            yield return (synth, extra.Model);
        }
    }

    // Pull the curated tips off any of the game's model types — they
    // each define HoverTips independently (no shared interface) so
    // we type-switch and bail out cleanly on anything else. Power /
    // Affliction / Potion all start their HoverTips list with the
    // model's *own* tip, which we already render as the actor's
    // header card; drop that first entry so we don't double up.
    // CardModel and RelicModel expose dedicated "exclude self"
    // accessors that do this for us.
    private static IEnumerable<IHoverTip> EnumerateHoverTips(AbstractModel model)
    {
        try
        {
            return model switch
            {
                CardModel c        => c.HoverTips,
                RelicModel r       => r.HoverTipsExcludingRelic,
                PowerModel p       => System.Linq.Enumerable.Skip(p.HoverTips, 1),
                AfflictionModel a  => System.Linq.Enumerable.Skip(a.HoverTips, 1),
                PotionModel pot    => System.Linq.Enumerable.Skip(pot.HoverTips, 1),
                _                  => System.Linq.Enumerable.Empty<IHoverTip>(),
            };
        }
        catch
        {
            return System.Linq.Enumerable.Empty<IHoverTip>();
        }
    }

    // IHoverTip is implemented by several concrete types (HoverTip
    // struct for simple LocString tips, CardHoverTip for cards…) —
    // pull Title/Description/Icon out of each and tag the entry with
    // its Id so the outer loop can dedupe across the row.
    private static (string Title, string Description, Texture2D? Icon, string Id, CardModel? Card)? ExtractDisplay(IHoverTip tip)
    {
        if (tip is HoverTip ht)
        {
            return (ht.Title ?? "", ConvertOrPass(ht.Description), ht.Icon, ht.Id, null);
        }
        if (tip is CardHoverTip cht)
        {
            try
            {
                var card = cht.Card;
                return (
                    card.Title,
                    TimelineEmit.CardTooltipDescription(card) ?? "",
                    TimelineIcons.TryLoad(() => card.Portrait),
                    cht.Id,
                    card);
            }
            catch { return null; }
        }
        return null;
    }

    private static string ConvertOrPass(string? raw) =>
        TimelineEmit.ConvertGameTags(raw) ?? raw ?? "";

    private static PanelContainer BuildPanel(string title, string descriptionBbcode, Texture2D? icon, CardModel? card)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        // Godot's default PanelContainer theme paints a dark rounded
        // rect that was peeking around the edges of our nine-patch.
        // Replace it with an empty stylebox so only the nine-patch is
        // visible behind the text.
        panel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        // Background: nine-patch matching the game's hover_tip frame.
        if (_cachedBackplate != null)
        {
            var bg = new NinePatchRect
            {
                Texture = _cachedBackplate,
                RegionRect = new Rect2(0, 0, 339, 107),
                PatchMarginLeft = 55,
                PatchMarginTop = 43,
                PatchMarginRight = 91,
                PatchMarginBottom = 32,
                AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Stretch,
                AxisStretchVertical = NinePatchRect.AxisStretchMode.Stretch,
                MouseFilter = MouseFilterEnum.Ignore,
                AnchorRight = 1, AnchorBottom = 1,
            };
            panel.AddChild(bg);
        }
        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_left", 22);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_right", 45);
        pad.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(pad);

        var vb = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vb.AddThemeConstantOverride("separation", 2);
        pad.AddChild(vb);

        var titleRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        titleRow.AddThemeConstantOverride("separation", 6);
        vb.AddChild(titleRow);

        var titleLbl = new Label
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        if (_cachedTitleFont != null) titleLbl.AddThemeFontOverride("font", _cachedTitleFont);
        titleLbl.AddThemeFontSizeOverride("font_size", 20);
        titleLbl.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        titleLbl.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25f));
        titleLbl.AddThemeConstantOverride("shadow_offset_x", 3);
        titleLbl.AddThemeConstantOverride("shadow_offset_y", 2);
        titleRow.AddChild(titleLbl);

        // Cards get the same styled icon used on the timeline row
        // (silhouette-clipped portrait + rarity ring + banner). Non-card
        // tips fall back to the plain pre-resolved texture.
        if (card != null)
        {
            titleRow.AddChild(CardIconBuilder.Build(card, 36f));
        }
        else if (icon != null)
        {
            titleRow.AddChild(new TextureRect
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(28, 28),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }

        if (!string.IsNullOrEmpty(descriptionBbcode))
        {
            var desc = new RichTextLabel
            {
                CustomMinimumSize = new Vector2(MaxDescriptionWidth, 0),
                BbcodeEnabled = true,
                FitContent = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = MouseFilterEnum.Ignore,
                Text = descriptionBbcode,
            };
            if (_cachedBodyFont != null) desc.AddThemeFontOverride("normal_font", _cachedBodyFont);
            if (_cachedTitleFont != null) desc.AddThemeFontOverride("bold_font", _cachedTitleFont);
            desc.AddThemeFontSizeOverride("normal_font_size", 16);
            desc.AddThemeColorOverride("default_color", new Color(1f, 0.965f, 0.886f));
            desc.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25f));
            desc.AddThemeConstantOverride("shadow_offset_x", 3);
            desc.AddThemeConstantOverride("shadow_offset_y", 2);
            vb.AddChild(desc);
        }

        return panel;
    }

    private static T? TryLoad<T>(string path) where T : Resource
    {
        try { return ResourceLoader.Load<T>(path); }
        catch { return null; }
    }
}
