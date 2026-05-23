// Compact "card icon": portrait art clipped to the card-type silhouette,
// the rarity-tinted PortraitBorder ring on top, and the small ribbon
// banner perched above. Same construction as the Run Table mod — see
// that copy for the design notes.
//
// Two textures, two roles:
//   • CardModel.PortraitBorder — the visible ring around the art
//     window. Recoloured by rarity via a luminance shader so the
//     teal/gold base doesn't muddy the tint.
//   • run_history/<type>_portrait.png — a standalone (non-atlas) PNG
//     of the silhouette, used only as a `ClipChildren = Only` mask.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Timeline;

public static class CardIconBuilder
{
    private const string AttackShapePath = "res://images/packed/run_history/attack_portrait.png";
    private const string SkillShapePath  = "res://images/packed/run_history/skill_portrait.png";
    private const string PowerShapePath  = "res://images/packed/run_history/power_portrait.png";
    private const string BannerPath      = "res://images/packed/run_history/banner.png";

    // Banner pennant overhangs the icon's sides.
    private const float BannerSideOverhang = 0.18f;
    private const float BannerTop          = 0.00f;
    private const float BannerBottom       = 0.36f;

    // run_history silhouette is tighter than the PortraitBorder ring,
    // so the clipped portrait lands well inside the ring with empty
    // air between. Vertical overscale runs hotter than horizontal
    // because the silhouette is shorter than it is narrow.
    private const float ClipOverscaleH = 0.15f;
    private const float ClipOverscaleV = 0.30f;

    public static Control Build(CardModel? card, float size)
    {
        var root = new Control
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (card == null) return root;

        var maskTex = LoadShapeTexture(GetCardType(card));
        var rarity = ColorForRarity(card.Rarity);

        if (card.Portrait != null && maskTex != null)
        {
            var clipper = new TextureRect
            {
                Texture = maskTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ClipChildren = CanvasItem.ClipChildrenMode.Only,
            };
            clipper.AnchorLeft = -ClipOverscaleH; clipper.AnchorRight  = 1f + ClipOverscaleH;
            clipper.AnchorTop  = -ClipOverscaleV; clipper.AnchorBottom = 1f + ClipOverscaleV;
            clipper.OffsetLeft = 0; clipper.OffsetRight = 0;
            clipper.OffsetTop  = 0; clipper.OffsetBottom = 0;

            float clipperW = 1f + 2f * ClipOverscaleH;
            float clipperH = 1f + 2f * ClipOverscaleV;
            var portrait = new TextureRect
            {
                Texture = card.Portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            portrait.AnchorLeft  = ClipOverscaleH / clipperW;
            portrait.AnchorRight = (1f + ClipOverscaleH) / clipperW;
            portrait.AnchorTop   = ClipOverscaleV / clipperH;
            portrait.AnchorBottom = (1f + ClipOverscaleV) / clipperH;
            portrait.OffsetLeft = 0; portrait.OffsetRight = 0;
            portrait.OffsetTop  = 0; portrait.OffsetBottom = 0;

            clipper.AddChild(portrait);
            root.AddChild(clipper);
        }
        else if (card.Portrait != null)
        {
            var portrait = new TextureRect
            {
                Texture = card.Portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            FillRect(portrait);
            root.AddChild(portrait);
        }

        if (card.PortraitBorder != null)
        {
            var border = new TextureRect
            {
                Texture = card.PortraitBorder,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = BuildRecolorMaterial(rarity, brightness: 1.6f),
            };
            FillRect(border);
            root.AddChild(border);
        }

        var bannerTex = ResourceLoader.Load<Texture2D>(BannerPath);
        if (bannerTex != null)
        {
            var banner = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = rarity,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            banner.AnchorLeft = 0; banner.AnchorRight = 1;
            banner.AnchorTop = 0; banner.AnchorBottom = 0;
            banner.OffsetLeft  = -size * BannerSideOverhang;
            banner.OffsetRight =  size * BannerSideOverhang;
            banner.OffsetTop    = size * BannerTop;
            banner.OffsetBottom = size * BannerBottom;
            root.AddChild(banner);
        }

        return root;
    }

    private static void FillRect(Control c)
    {
        c.AnchorLeft = 0; c.AnchorRight = 1;
        c.AnchorTop = 0; c.AnchorBottom = 1;
        c.OffsetLeft = 0; c.OffsetRight = 0;
        c.OffsetTop = 0; c.OffsetBottom = 0;
    }

    private static ShaderMaterial BuildRecolorMaterial(Color tint, float brightness)
    {
        var shader = new Shader();
        shader.Code = @"
shader_type canvas_item;
uniform vec3 tint : source_color = vec3(1.0);
uniform float brightness : hint_range(0.0, 4.0) = 1.0;
void fragment() {
    vec4 tex = texture(TEXTURE, UV);
    float lum = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    COLOR = vec4(tint * lum * brightness, tex.a);
}
";
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        mat.SetShaderParameter("brightness", brightness);
        return mat;
    }

    private enum CardTypeShape { Attack, Skill, Power }

    private static CardTypeShape GetCardType(CardModel card)
    {
        try
        {
            var prop = card.GetType().GetProperty("Type",
                BindingFlags.Instance | BindingFlags.Public);
            if (prop != null)
            {
                var val = prop.GetValue(card)?.ToString() ?? "";
                if (val.Contains("Attack")) return CardTypeShape.Attack;
                if (val.Contains("Power"))  return CardTypeShape.Power;
            }
        }
        catch { }
        return CardTypeShape.Skill;
    }

    private static Texture2D? LoadShapeTexture(CardTypeShape t) => t switch
    {
        CardTypeShape.Attack => ResourceLoader.Load<Texture2D>(AttackShapePath),
        CardTypeShape.Power  => ResourceLoader.Load<Texture2D>(PowerShapePath),
        _                    => ResourceLoader.Load<Texture2D>(SkillShapePath),
    };

    private static Color ColorForRarity(CardRarity rarity) => rarity switch
    {
        CardRarity.Common   => new Color(0.82f, 0.82f, 0.82f),
        CardRarity.Basic    => new Color(0.82f, 0.82f, 0.82f),
        CardRarity.Uncommon => new Color(0.40f, 0.65f, 0.95f),
        CardRarity.Rare     => new Color(1.00f, 0.85f, 0.30f),
        CardRarity.Curse    => new Color(0.70f, 0.40f, 0.85f),
        CardRarity.Status   => new Color(0.85f, 0.75f, 0.55f),
        CardRarity.Event    => new Color(0.50f, 0.85f, 0.50f),
        CardRarity.Quest    => new Color(0.95f, 0.65f, 0.30f),
        CardRarity.Ancient  => new Color(0.95f, 0.85f, 0.55f),
        _                   => Colors.White,
    };
}
