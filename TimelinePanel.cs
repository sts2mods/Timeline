// Right-side timeline panel. Each row is laid out icon-first to read
// at a glance — "[cause] [amount + effect] [target]" — backed by the
// game's submenu_panel texture. Hovering a row produces a tooltip
// describing the event in prose.
//
// Rendering is append-only: every frame we add Controls for events
// that arrived since the last tick, so the per-frame cost is
// proportional to *new* events, not the whole log.
using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace Timeline;

public static class TimelinePanel
{
    public const string NodeName = "TimelineSidePanel";

    // Layout: anchor the panel from just below the top bar down to
    // roughly the vertical middle of the screen. This keeps the
    // panel out of the way of the hand / energy / end-turn UI in
    // the bottom half, and never tucks under the top bar.
    private const float TopInset = 100f;
    // Right-edge offset differs per mode: minimized sits closer to
    // the screen edge so the narrow icon column doesn't look like
    // it's floating in space, expanded pulls back a few pixels so
    // the parchment frame has breathing room from the screen edge.
    private const float MinimizedRightInset = 12f;
    private const float ExpandedRightInset = 18f;
    private const float BottomAnchor = 0.78f;
    private const float BottomOffset = -10f;

    // Both modes share the same row dimensions: minimized is just the
    // expanded panel clipped to a narrower width so only the cause
    // icon is visible. Keeping the row layout identical means the
    // scroll position survives a toggle, and the toggle itself is
    // just an animated panel-width / background-alpha tween — no
    // row rebuild.
    // Expanded width is computed dynamically from the widest row's
    // actual measured content. The base is "fits a single short
    // effect". Each new row we render is measured one frame after
    // it lands in the tree (so Godot has had time to lay it out)
    // and the panel grows to fit any row wider than what we've
    // accommodated so far. A hard cap keeps a pathological row from
    // pushing the panel off-screen.
    private const float BaseExpandedWidth = 250f;
    private const float MaxExpandedWidth = 560f;
    // vb has 24 left + 24 right padding inside the panel; the safety
    // buffer keeps the row content from kissing the parchment's
    // textured edge.
    private const float RowHorizontalPadding = 48f;
    private const float RowSafetyBuffer = 16f;
    // Sized so the cause icon (40px) is geometrically centred in the
    // narrow panel with 4px padding each side. Crucially, panel.width
    // is chosen so the icon's screen position matches where it sat
    // before this iteration (icon.center ≈ screen.right - 36) — the
    // user didn't want the icon to drift left when "centering" it.
    // The arrow beside the cause now lands fully off-panel (x=50,
    // panel ends at 48) so nothing peeks past the right edge.
    private const float MinimizedWidth = 48f;
    // vb inset shrinks from ExpandedVbInset to MinimizedVbInset
    // during the toggle so the cause icon ends up centred in the
    // minimised mode, while expanded mode keeps the wider padding
    // that visually sits inside the parchment frame.
    private const float MinimizedVbInset = 4f;
    private const float ExpandedVbInset = 24f;
    private const float RowHeight = 48f;
    private const float RowSpacing = 2f;
    private const float IconSize = 40f;
    private const float SmallIconSize = 26f;

    private const string PanelTexturePath = "res://images/packed/common_ui/submenu_panel.png";


    // Default to minimized — most of the time the timeline runs as a
    // sidebar; you click the toggle when you want the detailed rows.
    private static bool _minimized = true;

    private static Control? _root;
    private static VBoxContainer? _vb;
    private static VBoxContainer? _rowsContainer;
    private static ScrollContainer? _scroll;
    private static NinePatchRect? _background;
    private static Control? _titleLabel;
    // Bg alpha tweens with the toggle animation; also used as the
    // starting modulate for newly-built rows so they fade in (or stay
    // hidden) consistently with the existing panel state.
    private static float _bgAlpha = 0f;
    // Tracks the widest row seen so far — drives the expanded panel
    // width so the longest event (e.g. Tender's combined -1 Str /
    // -1 Dex) always fits without clipping.
    private static float _currentExpandedWidth = BaseExpandedWidth;
    private static int _renderedUpTo;
    private static Control? _lastRowNode;
    private static int _lastRowEventIndex = -1;
    private static int _lastRowExtraCount;
    // The most recent action row — gets a gold tint overlay so the
    // player can tell at a glance which entry just landed. Structural
    // separators (turn / combat boundaries) are skipped so the
    // highlight tracks the latest gameplay action, not the divider
    // that just got rendered between turns.
    private static Control? _highlightedRow;
    // Rows added on this tick get measured on the NEXT RenderTick.
    // Godot needs a layout pass between AddChild and a meaningful
    // GetCombinedMinimumSize() — measuring synchronously after
    // AddChild was returning 0, which is why the panel never grew.
    private static readonly System.Collections.Generic.List<Control> _rowsToMeasure = new();
    // Event indices whose row needs rebuilding because the lookback
    // merge folded a new effect into an event that wasn't the most
    // recent. Without this set, only the LAST row's extras would
    // refresh — older rows merged across (Thunderclap's vuln→E1
    // merging into damage→E1) would be left stale.
    private static readonly System.Collections.Generic.HashSet<int> _dirtyRowIndices = new();
    // Same story for the scroll-to-bottom: setting ScrollVertical to
    // (MaxValue - Page) in CallDeferred reads the OLD content height
    // because Godot's layout pass hasn't run yet. We process the flag
    // at the start of the next RenderTick, by which time layout for
    // the just-added row is applied and MaxValue is honest.
    private static bool _pendingScrollToBottom;
    private static TimelineTooltip? _tooltip;
    private static Tween? _toggleTween;
    private const float ToggleAnimSeconds = 0.25f;

    public static void EnsureAttached()
    {
        try
        {
            var combat = NCombatRoom.Instance;
            if (combat == null) return;
            // Look first under CombatUi (our preferred parent), then
            // fall back to CombatRoom for older attachments still
            // hanging around if the host swapped between runs.
            var existing = ((Control?)combat.Ui)?.GetNodeOrNull<Control>(NodeName)
                ?? combat.GetNodeOrNull<Control>(NodeName);
            if (existing != null) return;
            BuildPanel(combat);
            _renderedUpTo = 0;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}EnsureAttached: {ex.Message}");
        }
    }

    public static void Detach()
    {
        try
        {
            var combat = NCombatRoom.Instance;
            ((Control?)combat?.Ui)?.GetNodeOrNull<Control>(NodeName)?.QueueFree();
            combat?.GetNodeOrNull<Control>(NodeName)?.QueueFree();
            _root = null;
            _vb = null;
            _rowsContainer = null;
            _scroll = null;
            _background = null;
            _titleLabel = null;
            _renderedUpTo = 0;
            _lastRowNode = null;
            _lastRowEventIndex = -1;
            _lastRowExtraCount = 0;
            _highlightedRow = null;
            _rowsToMeasure.Clear();
            _dirtyRowIndices.Clear();
            _pendingScrollToBottom = false;
            _tooltip = null;
            _toggleTween = null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}Detach: {ex.Message}");
        }
    }

    // Flips between minimized and expanded modes. Width/alpha animation
    // is delegated to ApplyPanelLayout so the dynamic-width grow path
    // uses the exact same tween mechanics.
    private static void ToggleMode()
    {
        if (_root == null) return;
        _minimized = !_minimized;
        ApplyPanelLayout();
    }

    // The entire panel is the hitbox: any left-click anywhere inside
    // the root's rect — minimized icon column, expanded parchment,
    // even between rows — toggles the open/closed state. Wheel
    // events are ignored so the user can still scroll without
    // accidentally collapsing the timeline.
    private static void OnRootGuiInput(InputEvent ev)
    {
        if (ev is not InputEventMouseButton mb) return;
        if (!mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left) return;
        ToggleMode();
        _root?.AcceptEvent();
    }

    // Single source of truth for "what should the panel look like
    // right now?" — reads _minimized + _currentExpandedWidth and
    // tweens the panel/background/title/rows to match.
    //
    //   • Scroll position survives the toggle automatically (no
    //     rebuild).
    //   • The "slide" animation falls out for free: the cause icon
    //     is positioned at the left edge of the row, so as the panel
    //     widens leftward the cause icon screen-position moves left
    //     and the effect / target portions become visible to its
    //     right as they un-clip.
    //   • Called from both the user-driven toggle AND from the
    //     dynamic-width grow path so they share the same tween code.
    private static void ApplyPanelLayout()
    {
        if (_root == null) return;
        _toggleTween?.Kill();
        _toggleTween = _root.CreateTween().SetParallel(true);
        float targetWidth = _minimized ? MinimizedWidth : _currentExpandedWidth;
        float targetAlpha = _minimized ? 0f : 1f;
        float targetRight = _minimized ? MinimizedRightInset : ExpandedRightInset;
        float targetVbInset = _minimized ? MinimizedVbInset : ExpandedVbInset;
        _toggleTween.TweenProperty(_root, "offset_left", -(targetWidth + targetRight), ToggleAnimSeconds)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _toggleTween.TweenProperty(_root, "offset_right", -targetRight, ToggleAnimSeconds)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (_vb != null)
        {
            _toggleTween.TweenProperty(_vb, "offset_left", targetVbInset, ToggleAnimSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            _toggleTween.TweenProperty(_vb, "offset_right", -targetVbInset, ToggleAnimSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        if (_background != null)
        {
            var bgTarget = new Color(_background.Modulate.R, _background.Modulate.G, _background.Modulate.B, targetAlpha);
            _toggleTween.TweenProperty(_background, "modulate", bgTarget, ToggleAnimSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        if (_titleLabel != null)
        {
            var titleTarget = new Color(_titleLabel.Modulate.R, _titleLabel.Modulate.G, _titleLabel.Modulate.B, targetAlpha);
            _toggleTween.TweenProperty(_titleLabel, "modulate", titleTarget, ToggleAnimSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        if (_rowsContainer != null)
        {
            foreach (var node in _rowsContainer.GetChildren())
            {
                if (node is not Control rowControl) continue;
                var rowBg = rowControl.GetNodeOrNull<ColorRect>("RowBg");
                if (rowBg == null) continue;
                _toggleTween.TweenProperty(rowBg, "modulate", new Color(1, 1, 1, targetAlpha), ToggleAnimSeconds)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            }
        }
        _bgAlpha = targetAlpha;
    }

// Single panel structure used in both modes — the row content is
    // always the full expanded layout (cause → effect → target).
    // Minimized mode is just this same panel clipped to a narrower
    // width via offset_left so only the cause icon is visible.
    //
    // Hierarchy:
    //   root: Control (anchored to screen right, ClipContents=true)
    //     bg:   NinePatchRect (panel frame, modulated 0/1 alpha)
    //     vb:   VBoxContainer (full size of root, offset 1 row from top)
    //       scroll: ScrollContainer
    //         rows: VBoxContainer of rows
    //     toggle: floating Button (top-left, beside the first icon)
    private static void BuildPanel(NCombatRoom combat)
    {
        float initialWidth = _minimized ? MinimizedWidth : _currentExpandedWidth;
        // Root holds everything but does NOT clip — that way the
        // toggle button can sit at OffsetLeft=-24 (outside the panel
        // area) without being chopped off. The inner contentClip
        // does the actual clipping for the row content so the
        // minimized look (narrow panel, only the cause icon visible)
        // still works.
        var root = new Control
        {
            Name = NodeName,
            AnchorLeft = 1, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = BottomAnchor,
            OffsetLeft = -(initialWidth + (_minimized ? MinimizedRightInset : ExpandedRightInset)),
            OffsetRight = -(_minimized ? MinimizedRightInset : ExpandedRightInset),
            OffsetTop = TopInset,
            OffsetBottom = BottomOffset,
            GrowHorizontal = Control.GrowDirection.Begin,
            // Stop so root receives gui_input; children with
            // MouseFilter=Pass still bubble their click events up
            // here, which means clicking ANYWHERE on the panel
            // (minimized icon column or expanded parchment) toggles
            // the open/closed state.
            MouseFilter = Control.MouseFilterEnum.Stop,
            // ZIndex stays at 0 so tree order decides — see the
            // MoveChild call below that places us before the player
            // hand / confirm button sibling.
            ZIndex = 0,
        };
        root.GuiInput += OnRootGuiInput;
        // Parent under CombatUi as its FIRST child rather than under
        // CombatRoom directly. CombatRoom's tree is
        // CombatSceneContainer (creatures) → CombatUi (HUD, hand,
        // confirm-button, etc.); attaching to CombatRoom puts us on
        // top of the entire UI, blocking the SelectModeConfirmButton
        // that pops up in the same screen quadrant during exhaust /
        // discard selections. As the first child of CombatUi we
        // render after the creature scene but before any of the
        // sibling UI widgets, so hand cards and the confirm tick
        // sit visually on top of the timeline while the timeline
        // still sits on top of the enemies.
        var uiHost = (Control?)combat.Ui ?? (Control)combat;
        uiHost.AddChild(root);
        if (uiHost == (Control?)combat.Ui)
        {
            uiHost.MoveChild(root, 0);
        }
        _root = root;

        var contentClip = new Control
        {
            Name = "ContentClip",
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 1,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        root.AddChild(contentClip);

        _bgAlpha = _minimized ? 0f : 1f;
        BuildBackground(contentClip);
        BuildTitle(contentClip);

        // Push the scroll content down by one row's worth so the
        // first icon clears the toggle button row above it. Generous
        // horizontal insets keep the icons (and the zebra stripes)
        // off the parchment's textured edge.
        float vbInset = _minimized ? MinimizedVbInset : ExpandedVbInset;
        var vb = new VBoxContainer
        {
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 1,
            OffsetTop = RowHeight,
            OffsetLeft = vbInset,
            OffsetRight = -vbInset,
            // Pull the scroll bottom up by ~3/4 of a row so the last
            // event sits comfortably above the parchment's textured
            // bottom frame instead of bleeding into it.
            OffsetBottom = -(RowHeight * 0.75f + 12f),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        contentClip.AddChild(vb);
        _vb = vb;

        BuildScroll(vb);

        _tooltip = TimelineTooltip.Create(_root);
    }

    private static void BuildBackground(Control parent)
    {
        var tex = TryLoad<Texture2D>(PanelTexturePath);
        var bg = new NinePatchRect
        {
            Name = "Background",
            Texture = tex,
            RegionRect = tex != null ? new Rect2(0, 0, tex.GetWidth(), tex.GetHeight()) : new Rect2(0, 0, 1, 1),
            PatchMarginLeft = 48, PatchMarginRight = 48,
            PatchMarginTop = 48, PatchMarginBottom = 48,
            AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Stretch,
            AxisStretchVertical = NinePatchRect.AxisStretchMode.Stretch,
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(0.4f, 0.4f, 0.42f, _minimized ? 0f : 1f),
        };
        parent.AddChild(bg);
        _background = bg;
    }

    private static T? TryLoad<T>(string path) where T : Resource
    {
        try { return ResourceLoader.Load<T>(path); }
        catch { return null; }
    }

    private static void BuildTitle(Control parent)
    {
        // RichTextLabel so we can use [u]...[/u] for the underline —
        // plain Label has no underline support.
        var label = new RichTextLabel
        {
            Text = "[center][u]Timeline[/u][/center]",
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 0,
            // Push the title down inside the top band so it sits
            // closer to the first row instead of pinned at the top.
            OffsetTop = 18, OffsetBottom = RowHeight + 4,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, _bgAlpha),
        };
        var titleFont = TryLoad<Font>("res://themes/kreon_bold_glyph_space_one.tres");
        if (titleFont != null) label.AddThemeFontOverride("normal_font", titleFont);
        label.AddThemeFontSizeOverride("normal_font_size", 20);
        label.AddThemeColorOverride("default_color", StsColors.gold);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 2);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        parent.AddChild(label);
        _titleLabel = label;
    }

    private static void BuildScroll(VBoxContainer parent)
    {
        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        parent.AddChild(_scroll);

        _rowsContainer = new VBoxContainer
        {
            Name = "Content",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _rowsContainer.AddThemeConstantOverride("separation", (int)RowSpacing);
        _scroll.AddChild(_rowsContainer);
    }

    // Called every frame from the SceneTree.ProcessFrame hook.
    // Append-only — only new events do work, so the per-frame cost is
    // proportional to incoming events rather than the whole log.
    //
    // Plus one extra responsibility: when the most recently rendered
    // event grows its Extra list (because TimelineEmit.Leaf folded a
    // mergeable follow-up into it after we'd already built the row),
    // replace that row so the new effect items show up. Without this,
    // Tender's -1 Dex would silently update the event object but the
    // visible row would still only show -1 Strength.
    public static void RenderTick()
    {
        if (!TimelineMod.Enabled) return;
        if (_rowsContainer == null) return;
        var events = TimelineLog.Events;

        // Pending scroll from the previous tick — layout has now
        // applied so MaxValue / Page are honest. Always run BEFORE
        // we add new rows this tick, so each new event eventually
        // pulls the viewport to the bottom even if events arrive
        // across many frames.
        if (_pendingScrollToBottom && _scroll != null)
        {
            _pendingScrollToBottom = false;
            DoScrollToBottom();
        }

        // Keep the tooltip stuck to its hovered row even while the
        // panel is mid-toggle — rebuilding its position each frame
        // is cheap and tracks the tween smoothly.
        _tooltip?.Reposition();

        // Measure rows that were added on previous ticks — by now
        // they've had a layout pass and GetCombinedMinimumSize is
        // honest.
        if (_rowsToMeasure.Count > 0)
        {
            foreach (var row in _rowsToMeasure)
                if (GodotObject.IsInstanceValid(row))
                    MeasureAndGrowPanelWidth(row);
            _rowsToMeasure.Clear();
        }

        bool rowsChanged = false;
        // Process rows marked dirty by lookback merges (an older
        // event grew an extra because something matched it across
        // intervening events — Thunderclap's vuln→E1 finding
        // damage→E1 past damage→E2 etc.). Always include the most
        // recent row's index if its Extra count drifted, since
        // legacy paths (Tender's -Str+-Dex back-to-back) only
        // mutate the last event.
        if (_lastRowEventIndex >= 0 && _lastRowEventIndex < events.Count
            && events[_lastRowEventIndex].Extra.Count != _lastRowExtraCount)
        {
            _dirtyRowIndices.Add(_lastRowEventIndex);
        }
        if (_dirtyRowIndices.Count > 0)
        {
            foreach (var idx in _dirtyRowIndices)
            {
                if (idx < 0 || idx >= events.Count) continue;
                if (idx >= _rowsContainer.GetChildCount()) continue;
                RebuildRowAt(idx, events[idx]);
            }
            _dirtyRowIndices.Clear();
            ScrollToBottomDeferred();
            rowsChanged = true;
        }

        if (_renderedUpTo < events.Count)
        {
            for (int i = _renderedUpTo; i < events.Count; i++)
            {
                int actionRowIndex = CountActionRowsBeforeSlot(_rowsContainer.GetChildCount());
                var row = BuildRow(events[i], actionRowIndex);
                _rowsContainer.AddChild(row);
                _rowsToMeasure.Add(row);
                _lastRowNode = row;
                _lastRowEventIndex = i;
                _lastRowExtraCount = events[i].Extra.Count;
            }
            _renderedUpTo = events.Count;
            ScrollToBottomDeferred();
            rowsChanged = true;
        }

        if (rowsChanged) SetHighlightedRow(FindLatestActionRow());
    }

    // Apply the "latest event" gold tint to `row`, removing it from
    // whichever row carried it last. The overlay sits between the
    // zebra-stripe RowBg and the HBox content so the icons stay
    // legible on top while the tint shows through behind them. The
    // overlay has its own colour alpha (instead of using Modulate)
    // so it remains visible in minimized mode where RowBg's modulate
    // is tweened to alpha 0.
    private static void SetHighlightedRow(Control? row)
    {
        if (_highlightedRow == row) return;
        if (_highlightedRow != null && GodotObject.IsInstanceValid(_highlightedRow))
            _highlightedRow.GetNodeOrNull<ColorRect>("HighlightOverlay")?.QueueFree();
        _highlightedRow = row;
        if (row == null) return;

        var overlay = new ColorRect
        {
            Name = "HighlightOverlay",
            Color = new Color(0.937f, 0.784f, 0.318f, 0.32f),
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(overlay);
        row.MoveChild(overlay, 1);
    }

    // Walks the row container back-to-front to find the latest
    // action row (one with a "Content" HBox). Structural separators
    // are skipped, and so are rows queued for deletion — when
    // RebuildRowAt is in flight, the old row is QueueFree'd but
    // still sits in the tree (at a higher slot than the new row)
    // until end of frame, so a naive walk would attach the highlight
    // to the dying old row.
    private static Control? FindLatestActionRow()
    {
        if (_rowsContainer == null) return null;
        var children = _rowsContainer.GetChildren();
        for (int i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is Control row && !row.IsQueuedForDeletion() && row.HasNode("Content"))
                return row;
        }
        return null;
    }

    // Count how many action rows (the ones with a "Content" HBox)
    // already live in _rowsContainer before the given slot. Used to
    // drive zebra-striping parity without letting structural turn /
    // combat separators flip the pattern.
    private static int CountActionRowsBeforeSlot(int slot)
    {
        if (_rowsContainer == null) return 0;
        int count = 0;
        var children = _rowsContainer.GetChildren();
        int limit = System.Math.Min(slot, children.Count);
        for (int i = 0; i < limit; i++)
        {
            if (children[i] is Control row && row.HasNode("Content"))
                count++;
        }
        return count;
    }

    // Grow the expanded panel width to fit a row's actual measured
    // content. Catches every "row is wider than usual" shape —
    // multi-hit attacks (the "Nx{amount}" label is wider than a
    // single number), Tender's merged effect list, future event
    // layouts — without me having to special-case each one.
    //
    // The row must have laid out at least once (i.e. this runs on
    // the RenderTick AFTER the row was added) so the HBox's
    // GetCombinedMinimumSize returns a real value.
    private static void MeasureAndGrowPanelWidth(Control row)
    {
        var hb = row.GetNodeOrNull<HBoxContainer>("Content");
        if (hb == null) return;
        float contentWidth = hb.GetCombinedMinimumSize().X;
        if (contentWidth <= 0f) return;
        float needed = contentWidth + RowHorizontalPadding + RowSafetyBuffer;
        if (needed > MaxExpandedWidth) needed = MaxExpandedWidth;
        if (needed <= _currentExpandedWidth) return;
        _currentExpandedWidth = needed;
        ApplyPanelLayout();
        // Belt-and-suspenders: the tween created from ProcessFrame
        // wasn't always taking — set the final offset directly so
        // the panel ends up at the right width regardless.
        if (!_minimized && _root != null)
        {
            _root.OffsetLeft = -(_currentExpandedWidth + ExpandedRightInset);
            _root.OffsetRight = -ExpandedRightInset;
        }
    }

    // Belt-and-suspenders scroll-to-bottom:
    //   • CallDeferred handles the fast case (layout settles before
    //     end of frame).
    //   • _pendingScrollToBottom = true handles everything else by
    //     running DoScrollToBottom at the start of the next tick,
    //     after Godot's layout pass has applied. This is what fixes
    //     potions / heals — without the next-tick path the scroll
    //     would lock onto the previous bottom because MaxValue is
    //     stale inside CallDeferred.
    private static void ScrollToBottomDeferred()
    {
        if (_scroll == null) return;
        _pendingScrollToBottom = true;
        Callable.From(DoScrollToBottom).CallDeferred();
    }

    private static void DoScrollToBottom()
    {
        var scroll = _scroll;
        if (scroll == null || !GodotObject.IsInstanceValid(scroll)) return;
        try
        {
            var vbar = scroll.GetVScrollBar();
            if (vbar != null)
                scroll.ScrollVertical = (int)(vbar.MaxValue - vbar.Page);
        }
        catch { /* ignore */ }
    }


    // Called by TimelineEmit.Leaf when a lookback merge folded a
    // new effect into an older event. RenderTick reads the set on
    // its next pass and replaces the row in place so the freshly-
    // merged extras render.
    public static void MarkRowDirty(int eventIndex)
    {
        if (eventIndex < 0) return;
        _dirtyRowIndices.Add(eventIndex);
    }

    private static void RebuildRowAt(int slot, TimelineEvent ev)
    {
        if (_rowsContainer == null) return;
        if (slot < 0 || slot >= _rowsContainer.GetChildCount()) return;
        var oldRow = _rowsContainer.GetChild(slot);
        if (oldRow is not Control oldRowControl) return;
        int actionRowIndex = CountActionRowsBeforeSlot(slot);
        var newRow = BuildRow(ev, actionRowIndex);
        _rowsContainer.AddChild(newRow);
        _rowsContainer.MoveChild(newRow, slot);
        oldRowControl.QueueFree();
        _rowsToMeasure.Add(newRow);
        if (_lastRowNode == oldRowControl)
        {
            _lastRowNode = newRow;
            _lastRowExtraCount = ev.Extra.Count;
        }
        if (_highlightedRow == oldRowControl) _highlightedRow = null;
    }

    // One row layout for both modes: full expanded layout
    // [cause → effect → target]. The "minimized" look is just the
    // same row clipped on the right by a narrower panel.
    //
    // `actionRowIndex` drives the zebra striping. It's a count of
    // ACTION rows preceding this one (structural separators are
    // skipped) so a turn-line in the middle doesn't break the
    // alternating pattern. Ignored for structural rows.
    private static Control BuildRow(TimelineEvent ev, int actionRowIndex)
    {
        // Structural rows (combat / turn boundaries) collapse to a
        // hairline divider — keeps them visible without claiming a
        // full action-row's worth of vertical space.
        if (IsStructural(ev.Effect))
            return BuildStructuralSeparator(ev, height: 10f);

        var row = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, RowHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        row.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

        var bg = new ColorRect
        {
            Name = "RowBg",
            Color = actionRowIndex % 2 == 0
                ? new Color(0f, 0f, 0f, 0.18f)
                : new Color(1f, 1f, 1f, 0.06f),
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, _bgAlpha),
        };
        row.AddChild(bg);

        var (title, detail) = TimelineNarrator.Describe(ev);
        row.MouseEntered += () => _tooltip?.Show(ev, title, detail, row);
        row.MouseExited += () => _tooltip?.Hide();

        var hb = new HBoxContainer
        {
            Name = "Content",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hb.AddThemeConstantOverride("separation", 6);

        if (ev.IndentDepth > 0)
        {
            hb.AddChild(new Control
            {
                CustomMinimumSize = new Vector2(12f * ev.IndentDepth, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }

        // Three alignment groups: [cause →] left, [amount + icon]
        // centered, [→ target] right. Two equal ExpandFill spacers
        // split the leftover space symmetrically between left/center
        // and center/right, so the effect chunk lands in the middle
        // regardless of row width.
        // Shift the cause icon a few px right within its 40-wide
        // cell so it sits visibly INSIDE the highlight overlay
        // rather than flush against the row's left edge (and the
        // overlay's left edge). Same shift in both modes — the
        // overlay anchors to the row, the icon is offset within.
        hb.AddChild(BuildActorIcon(ev.Cause, IconSize, fallbackForEmpty: TimelineIcons.SystemIcon(), xShift: 3f));
        hb.AddChild(BuildArrow());
        hb.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        hb.AddChild(BuildEffectChunk(ev));
        hb.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        hb.AddChild(BuildArrow());
        hb.AddChild(BuildActorIcon(ev.Target, IconSize, fallbackForEmpty: null));

        row.AddChild(hb);
        return row;
    }

    private static bool IsStructural(EffectKind kind) => kind switch
    {
        EffectKind.CombatStart or EffectKind.CombatEnd or
        EffectKind.TurnStart or EffectKind.TurnEnd => true,
        _ => false,
    };

    // Whether to render the numeric prefix on a given effect item.
    // For amount==1 PowerApplied/Affliction events the "1" is
    // redundant — "Tender" / "+1 Strength" reads identically with or
    // without the leading 1, and players reach for the icon first.
    // The hover tooltip still carries the exact amount.
    private static bool ShouldShowAmountValue(EffectKind effect, int? amount, int? hitCount)
    {
        if (!amount.HasValue) return false;
        if (hitCount.HasValue && hitCount > 1) return true;
        int a = amount.Value;
        return effect switch
        {
            EffectKind.PowerApplied or
            EffectKind.PowerRemoved or
            EffectKind.AfflictionApplied => a != 1 && a != 0,
            _ => true,
        };
    }

    // Compact hairline divider used for combat / turn boundaries.
    // Turn lines are coloured by side so a glance tells you whose
    // turn just started: blue for player, red for enemy. Combat
    // boundaries stay gold.
    private static Control BuildStructuralSeparator(TimelineEvent ev, float height)
    {
        var row = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, height),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        row.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var (title, detail) = TimelineNarrator.Describe(ev);
        row.MouseEntered += () => _tooltip?.Show(ev, title, detail, row);
        row.MouseExited += () => _tooltip?.Hide();

        var line = new ColorRect
        {
            Color = StructuralColor(ev),
            AnchorLeft = 0.5f, AnchorRight = 0.5f,
            AnchorTop = 0.5f, AnchorBottom = 0.5f,
            // Narrow, centred. Width in px (60), height 2.
            OffsetLeft = -30, OffsetRight = 30,
            OffsetTop = -1, OffsetBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(line);
        return row;
    }

    private static Color StructuralColor(TimelineEvent ev)
    {
        switch (ev.Effect)
        {
            case EffectKind.CombatStart:
            case EffectKind.CombatEnd:
                return new Color(0.937f, 0.784f, 0.318f, 0.95f); // gold
            case EffectKind.TurnStart:
            case EffectKind.TurnEnd:
                // Detail holds "Player" or "Enemy" — see CombatLifecycle.
                bool isPlayer = string.Equals(ev.Detail, "Player", System.StringComparison.OrdinalIgnoreCase);
                return isPlayer
                    ? new Color(0.529f, 0.808f, 0.922f, 0.85f) // StsColors.blue
                    : new Color(1.000f, 0.333f, 0.333f, 0.85f); // StsColors.red
            default:
                return new Color(1, 1, 1, 0.4f);
        }
    }

    private static Control BuildActorIcon(TimelineActor actor, float size, Texture2D? fallbackForEmpty, float xShift = 0f)
    {
        var cell = new Control
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (actor.IsEmpty)
        {
            if (fallbackForEmpty != null)
            {
                cell.AddChild(new TextureRect
                {
                    Texture = fallbackForEmpty,
                    AnchorRight = 1, AnchorBottom = 1,
                    OffsetLeft = xShift, OffsetRight = xShift,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Modulate = new Color(1, 1, 1, 0.55f),
                });
            }
            return cell;
        }

        // Cards get the styled icon (portrait clipped to type silhouette,
        // rarity-tinted border ring, rarity banner on top) — same look as
        // the Run Table. Falls through to the plain texture
        // path for non-card actors or when the CardModel reference is
        // missing (e.g. the multi-target placeholder).
        if (actor.Kind == ActorKind.Card && actor.Model is CardModel cardModel)
        {
            var icon = CardIconBuilder.Build(cardModel, size);
            icon.AnchorLeft = 0; icon.AnchorRight = 1;
            icon.AnchorTop = 0; icon.AnchorBottom = 1;
            icon.OffsetLeft = xShift; icon.OffsetRight = xShift;
            cell.AddChild(icon);
        }
        else if (TimelineIcons.ForActor(actor) is { } tex)
        {
            var tr = new TextureRect
            {
                Texture = tex,
                AnchorRight = 1, AnchorBottom = 1,
                OffsetLeft = xShift, OffsetRight = xShift,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            cell.AddChild(tr);
        }
        else
        {
            // Anything without a resolved icon falls back to the game's
            // app icon — looks far better than a colored square with a
            // single capital letter. Applies to System causes (e.g.
            // "Hand draw") and anywhere actor icon resolution fails.
            var fallback = TimelineIcons.SystemIcon();
            if (fallback != null)
            {
                var tr = new TextureRect
                {
                    Texture = fallback,
                    AnchorRight = 1, AnchorBottom = 1,
                    OffsetLeft = xShift, OffsetRight = xShift,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                cell.AddChild(tr);
            }
            else
            {
                // Truly nothing — preserve the colored-letter fallback
                // as last resort so the row isn't blank.
                var placeholder = new ColorRect
                {
                    Color = ColorForActorKind(actor.Kind),
                    AnchorRight = 1, AnchorBottom = 1,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                cell.AddChild(placeholder);
                var letter = new Label
                {
                    Text = ShortLabel(actor),
                    AnchorRight = 1, AnchorBottom = 1,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                letter.AddThemeFontSizeOverride("font_size", (int)(size * 0.45f));
                letter.AddThemeColorOverride("font_color", StsColors.cream);
                cell.AddChild(letter);
            }
        }
        // Multi-target count badge — overlay an "xN" label in the
        // bottom-right corner so the icon reads as "5 of these"
        // rather than just "one of these". Drawn on top of either
        // the real texture or the letter placeholder.
        if (actor.Count > 1)
        {
            var badge = new Label
            {
                Text = $"x{actor.Count}",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                AnchorLeft = 0, AnchorRight = 1,
                AnchorTop = 0, AnchorBottom = 1,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            badge.AddThemeFontSizeOverride("font_size", 14);
            badge.AddThemeColorOverride("font_color", new Color(1, 1, 1));
            badge.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.85f));
            badge.AddThemeConstantOverride("shadow_offset_x", 2);
            badge.AddThemeConstantOverride("shadow_offset_y", 2);
            cell.AddChild(badge);
        }
        return cell;
    }

    private static Control BuildEffectChunk(TimelineEvent ev)
    {
        var hb = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hb.AddThemeConstantOverride("separation", 6);

        AppendEffectItem(hb, ev.Effect, ev.Amount, ev.EffectIcon, ev.HitCount);
        foreach (var extra in ev.Extra)
        {
            // EffectEntry carries its own EffectKind now — Shrug It
            // Off's Block primary + CardDrawn extra each render with
            // the right colour and (default) icon. Fall back to the
            // parent effect if an old EffectEntry lacks one.
            var extraEffect = extra.Effect == EffectKind.None ? ev.Effect : extra.Effect;
            AppendEffectItem(hb, extraEffect, extra.Amount, extra.Icon, extra.HitCount);
        }

        return hb;
    }

    // Renders one {amount + icon} pair into the effect column. Called
    // once for the main event and once per merged extra effect so
    // Tender's combined "-1 Strength + -1 Dex" reads inline.
    //
    // Multi-hit attacks split the amount into "Nx" (small) + value
    // (full size) so it matches the game's own attack-intent labels
    // (the "x4" multiplier is rendered smaller than the damage value
    // everywhere else in the UI).
    private static void AppendEffectItem(HBoxContainer hb, EffectKind effect, int? amount, Texture2D? icon, int? hitCount)
    {
        if (ShouldShowAmountValue(effect, amount, hitCount))
        {
            var color = ColorForEffect(effect);
            if (hitCount.HasValue && hitCount > 1)
            {
                // "4x5" — damage-per-hit big, "x{hits}" small and
                // tight against it, matching the game's own attack
                // intent label. The inner HBox is separation=0 so
                // the two parts read as one number with no gap.
                var mini = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                mini.AddThemeConstantOverride("separation", 0);
                if (amount.HasValue)
                    mini.AddChild(MakeNumberLabel(amount.Value.ToString(), color, fontSize: 22));
                mini.AddChild(MakeNumberLabel($"x{hitCount}", color, fontSize: 14));
                hb.AddChild(mini);
            }
            else
            {
                hb.AddChild(MakeNumberLabel(amount!.Value.ToString(), color, fontSize: 22));
            }
        }
        var tex = icon ?? TimelineIcons.ForEffect(effect);
        if (tex != null)
        {
            hb.AddChild(new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(SmallIconSize, SmallIconSize),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        else
        {
            var lbl = new Label
            {
                Text = ShortEffect(effect),
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            lbl.AddThemeColorOverride("font_color", ColorForEffect(effect));
            hb.AddChild(lbl);
        }
    }

    private static Font? _cachedNumberFont;
    private static Font? GetNumberFont()
    {
        if (_cachedNumberFont != null) return _cachedNumberFont;
        try { _cachedNumberFont = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres"); }
        catch { /* fall back to default */ }
        return _cachedNumberFont;
    }

    private static Label MakeNumberLabel(string text, Color color, int fontSize)
    {
        var lbl = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var font = GetNumberFont();
        if (font != null) lbl.AddThemeFontOverride("font", font);
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 2);
        lbl.AddThemeConstantOverride("shadow_offset_y", 2);
        return lbl;
    }

    private static Control BuildArrow()
    {
        var arrow = new Label
        {
            Text = "→",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        arrow.AddThemeFontSizeOverride("font_size", 16);
        arrow.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.55f));
        return arrow;
    }

    private static Color ColorForActorKind(ActorKind kind) => kind switch
    {
        ActorKind.Card => new Color(0.45f, 0.30f, 0.15f),
        ActorKind.Relic => new Color(0.40f, 0.28f, 0.12f),
        ActorKind.Enemy => new Color(0.40f, 0.15f, 0.15f),
        ActorKind.Player => new Color(0.15f, 0.30f, 0.45f),
        ActorKind.Power => new Color(0.40f, 0.20f, 0.45f),
        ActorKind.Potion => new Color(0.25f, 0.55f, 0.30f),
        _ => new Color(0.18f, 0.18f, 0.20f),
    };

    private static Color ColorForEffect(EffectKind kind) => kind switch
    {
        EffectKind.Damage => new Color(1f, 0.55f, 0.4f),
        EffectKind.Block => StsColors.blue,
        EffectKind.PowerApplied => StsColors.purple,
        EffectKind.PowerRemoved => new Color(0.7f, 0.5f, 0.7f),
        EffectKind.CardAdded or EffectKind.CardDrawn => StsColors.green,
        EffectKind.CardRemoved => new Color(0.8f, 0.4f, 0.4f),
        EffectKind.EnergyGained => StsColors.orange,
        EffectKind.EnergySpent => StsColors.gray,
        EffectKind.AfflictionApplied => StsColors.pink,
        EffectKind.HealApplied => StsColors.green,
        EffectKind.CardUpgraded => StsColors.gold,
        EffectKind.Stunned => StsColors.purple,
        EffectKind.RelicTrigger => StsColors.gold,
        _ => StsColors.cream,
    };

    private static string ShortLabel(TimelineActor a)
    {
        if (!string.IsNullOrEmpty(a.Name)) return char.ToUpperInvariant(a.Name![0]).ToString();
        return a.Kind switch
        {
            ActorKind.Card => "C",
            ActorKind.Relic => "R",
            ActorKind.Enemy => "E",
            ActorKind.Player => "P",
            ActorKind.Power => "*",
            ActorKind.Potion => "U",
            _ => "?",
        };
    }

    private static string ShortEffect(EffectKind e) => e switch
    {
        EffectKind.Damage => "DMG",
        EffectKind.Block => "BLK",
        EffectKind.PowerApplied => "PWR",
        EffectKind.PowerRemoved => "-PWR",
        EffectKind.CardAdded => "CARD",
        EffectKind.CardRemoved => "-CARD",
        EffectKind.CardDrawn => "DRAW",
        EffectKind.EnergyGained => "ENG",
        EffectKind.EnergySpent => "-ENG",
        EffectKind.AfflictionApplied => "AFL",
        EffectKind.HealApplied => "HEAL",
        EffectKind.RelicTrigger => "RELIC",
        _ => "",
    };

}
