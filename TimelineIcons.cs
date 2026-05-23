// Texture lookup for the timeline row icons. Reuses the game's
// existing assets so the panel reads as a real part of the UI:
//   • Card portraits  → CardModel.Portrait (resolved at emission time)
//   • Relic icons     → RelicModel.IconPath
//   • Power icons     → PowerModel.Icon
//   • Player          → Character.IconTexture
//   • Enemy           → generic skull
//   • Effect icons    → reuse intent atlas / combat UI textures
//
// Anything we can't resolve returns null and the row builder falls
// back to a neutral placeholder.
using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Timeline;

public static class TimelineIcons
{
    private const string EnemyIconPath = "res://images/ui/emote/skull.png";
    // App icon (used as a "system / game-driven action" cause icon) —
    // the 1024px game icon, not the title-screen logo.
    private const string SystemIconPath = "res://images/icon_1024.png";

    public static Texture2D? SystemIcon() => LoadOrWarn(SystemIconPath);

    // Direct paths to the game's actual sprites — each effect picks the
    // visual that reads at a glance: shield for block, swirl for power,
    // card for card-shape events, etc.
    private static readonly Dictionary<EffectKind, string> EffectPaths = new()
    {
        { EffectKind.Damage,            "res://images/atlases/intent_atlas.sprites/attack/intent_attack_5.tres" },
        { EffectKind.Block,             "res://images/ui/combat/block.png" },
        { EffectKind.PowerApplied,      "res://images/atlases/intent_atlas.sprites/intent_buff.tres" },
        { EffectKind.PowerRemoved,      "res://images/atlases/intent_atlas.sprites/intent_debuff.tres" },
        { EffectKind.CardAdded,         "res://images/packed/sprite_fonts/card_icon.png" },
        { EffectKind.CardDrawn,         "res://images/packed/sprite_fonts/card_icon.png" },
        { EffectKind.CardRemoved,       "res://images/atlases/intent_atlas.sprites/intent_card_debuff.tres" },
        { EffectKind.EnergyGained,      "res://images/packed/sprite_fonts/colorless_energy_icon.png" },
        { EffectKind.EnergySpent,       "res://images/packed/sprite_fonts/colorless_energy_icon.png" },
        { EffectKind.AfflictionApplied, "res://images/atlases/intent_atlas.sprites/intent_card_debuff.tres" },
        { EffectKind.HealApplied,       "res://images/atlases/intent_atlas.sprites/intent_heal.tres" },
        { EffectKind.CardUpgraded,      "res://images/packed/sprite_fonts/star_icon.png" },
        { EffectKind.Stunned,           "res://images/atlases/intent_atlas.sprites/intent_stun.tres" },
        { EffectKind.RelicTrigger,      "res://images/atlases/intent_atlas.sprites/intent_buff.tres" },
        { EffectKind.EnemyMove,         "res://images/atlases/intent_atlas.sprites/intent_unknown.tres" },
        { EffectKind.CardPlayed,        "res://images/packed/sprite_fonts/card_icon.png" },
        { EffectKind.CreatureKilled,    "res://images/ui/emote/skull.png" },
    };

    // ---------- Render-side: pull the cached icon off the actor ------
    public static Texture2D? ForActor(TimelineActor a) => a.Icon;

    public static Texture2D? ForEffect(EffectKind effect)
    {
        // Energy icons are character-coloured (red for Ironclad, etc.)
        // — pull the local player's prefix and load the matching atlas
        // sprite. Falls back to the colourless icon if the prefix
        // isn't available yet (e.g. event fired pre-character-pick).
        if (effect == EffectKind.EnergyGained || effect == EffectKind.EnergySpent)
        {
            var colored = TryLoadEnergyIconForLocalCharacter();
            if (colored != null) return colored;
        }
        if (!EffectPaths.TryGetValue(effect, out var path)) return null;
        return LoadOrWarn(path);
    }

    private static Texture2D? TryLoadEnergyIconForLocalCharacter()
    {
        try
        {
            var prefix = RunManager.Instance?.GetLocalCharacterEnergyIconPrefix();
            if (string.IsNullOrEmpty(prefix)) return null;
            var path = $"res://images/atlases/ui_atlas.sprites/card/energy_{prefix!.ToLowerInvariant()}.tres";
            return LoadOrWarn(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}energy icon lookup: {ex.Message}");
            return null;
        }
    }

    // ---------- Emission-side: pre-resolve icons from live models ----
    public static Texture2D? TryLoad(Func<Texture2D?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    public static Texture2D? RelicIcon(RelicModel relic)
    {
        if (relic == null) return null;
        try
        {
            // Prefer the raw `relics/<id>.png` (transparent, no frame)
            // over the atlas sprite which has the relic's display
            // frame baked into the texture. The timeline column reads
            // cleaner against the combat scene without that frame.
            var id = TryGetId(relic);
            if (id != null)
            {
                var raw = LoadOrWarn($"res://images/relics/{id}.png");
                if (raw != null) return raw;
            }

            // Fallback: atlas-framed icon if the raw file is missing
            // (some relics may only ship the framed version).
            var iconProp = typeof(RelicModel).GetProperty("Icon",
                BindingFlags.Instance | BindingFlags.Public);
            if (iconProp?.GetValue(relic) is Texture2D tex) return tex;
            var pathProp = typeof(RelicModel).GetProperty("IconPath",
                BindingFlags.Instance | BindingFlags.Public);
            if (pathProp?.GetValue(relic) is string p && !string.IsNullOrEmpty(p))
                return LoadOrWarn(p);
            if (id != null)
                return LoadOrWarn($"res://images/atlases/relic_atlas.sprites/{id}.tres");
            return null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}relic icon ({relic.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    public static Texture2D? CreatureIcon(Creature c)
    {
        if (c == null) return null;
        try
        {
            if (c.IsPlayer)
            {
                var ch = c.Player?.Character;
                if (ch != null)
                {
                    try { return ch.IconTexture; }
                    catch { /* fall through */ }
                }
                return LoadOrWarn("res://images/ui/top_panel/character_icon_ironclad.png");
            }
            return LoadOrWarn(EnemyIconPath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}creature icon ({c.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    private static string? TryGetId(AbstractModel m)
    {
        try { return m.Id?.Entry?.ToLowerInvariant(); }
        catch { return null; }
    }

    // Public soft-load: returns null silently if the path is missing
    // (no log spam). Used by callers like the multi-target icon
    // resolver where we attempt several fallback paths.
    public static Texture2D? LoadOrNull(string path)
    {
        try
        {
            if (!ResourceLoader.Exists(path)) return null;
            return ResourceLoader.Load<Texture2D>(path);
        }
        catch { return null; }
    }

    private static Texture2D? LoadOrWarn(string path)
    {
        try
        {
            if (!ResourceLoader.Exists(path))
            {
                GD.PrintErr($"{TimelineMod.LogPrefix}icon missing: {path}");
                return null;
            }
            return ResourceLoader.Load<Texture2D>(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{TimelineMod.LogPrefix}icon load ({path}): {ex.Message}");
            return null;
        }
    }
}
