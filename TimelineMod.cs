// Timeline mod entry point. Harmony patches are loaded on init; the
// event store + overlay are wired up so each captured event lands in
// a single TimelineLog that the in-combat overlay reads from.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Timeline;

[ModInitializer("Initialize")]
public static class TimelineMod
{
    public const string Version = "0.1.0";
    public const string LogPrefix = "[Timeline] ";

    // Master toggle — settings UI + hotkey come next, like enemy_cycle.
    public static bool Enabled = true;

    private static Harmony? _harmony;

    public static void Initialize()
    {
        GD.Print($"{LogPrefix}v{Version} initializing...");
        try
        {
            _harmony = new Harmony("austin.timeline");
            ModHelpers.TryPatchAll(_harmony, typeof(TimelineMod).Assembly, LogPrefix);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix}Init harmony: {ex}");
        }
        // Even if a patch fails, still set up the lifecycle hook +
        // render tick — the panel can still appear with partial
        // event coverage.
        try { Callable.From(CombatLifecycle.Subscribe).CallDeferred(); }
        catch (Exception ex) { GD.PrintErr($"{LogPrefix}lifecycle: {ex.Message}"); }
        try { Callable.From(HookRenderTick).CallDeferred(); }
        catch (Exception ex) { GD.PrintErr($"{LogPrefix}tick: {ex.Message}"); }
    }

    private static void HookRenderTick()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;
            tree.ProcessFrame += TimelinePanel.RenderTick;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix}HookRenderTick: {ex.Message}");
        }
    }
}
