using System.Globalization;
using System.Text.RegularExpressions;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;

namespace RaidReplay.Core.Geometry;

/// <summary>Turns a pack shape override and/or Action sheet data into an <see cref="AoeShape"/>.</summary>
public static partial class ShapeResolver
{
    /// <summary>
    /// Provisional CastType mapping (validated empirically with `rr validate-shapes`):
    /// 1 single-target, 2 circle (target/location), 3/13 cone, 4 rect forward, 5 circle on caster (+hitbox),
    /// 6/7 circle (ground), 8 rect caster→target, 10 donut, 11 cross, 12 rect centered on caster.
    /// </summary>
    public static ShapeType FromCastType(byte castType) => castType switch
    {
        2 or 5 or 6 or 7 => ShapeType.Circle,
        3 or 13 => ShapeType.Cone,
        4 or 8 or 12 => ShapeType.Rect,
        10 => ShapeType.Donut,
        11 => ShapeType.Cross,
        _ => ShapeType.None,
    };

    public static AoeShape Resolve(ShapeDef? def, ActionInfo? info, float casterRadius, out string source)
    {
        var type = def != null ? Parse(def.Type) : info != null ? FromCastType(info.CastType) : ShapeType.None;
        source = def != null ? (info != null ? "pack+sheet" : "pack") : info != null ? $"sheet(ct{info.CastType})" : "none";
        if (type == ShapeType.None)
            return AoeShape.None;

        var range = info?.EffectRange ?? 0;
        if (info is { CastType: 5 } && def?.Radius == null)
            range += casterRadius;
        var width = info?.XAxisModifier ?? 0;
        var omen = info?.OmenPath;

        switch (type)
        {
            case ShapeType.Circle:
                return AoeShape.Circle(def?.Radius ?? range);
            case ShapeType.Donut:
            {
                var outer = def?.Radius ?? range;
                var inner = def?.InnerRadius ?? OmenDonutInner(omen, outer) ?? outer * 0.4f;
                return AoeShape.Donut(inner, outer);
            }
            case ShapeType.Cone:
                return AoeShape.Cone(def?.Radius ?? range, def?.AngleDeg ?? OmenConeAngle(omen) ?? 90f);
            case ShapeType.Rect:
            {
                var len = def?.Length ?? range;
                var back = def?.BackLength ?? (info is { CastType: 12 } ? len : 0);
                return AoeShape.Rect(len, def?.Width ?? (width > 0 ? width : 4f), back);
            }
            case ShapeType.Cross:
                return AoeShape.Cross(def?.Length ?? range, def?.Width ?? (width > 0 ? width : 4f));
            case ShapeType.HalfRoom:
                return new AoeShape(ShapeType.HalfRoom, def?.Radius ?? 40);
            case ShapeType.Knockback:
                return new AoeShape(ShapeType.Knockback, def?.Radius ?? range);
            case ShapeType.Gaze:
                return new AoeShape(ShapeType.Gaze, def?.Radius ?? 40);
            default:
                return AoeShape.None;
        }
    }

    public static ShapeType Parse(string type) => type switch
    {
        "circle" => ShapeType.Circle,
        "donut" => ShapeType.Donut,
        "cone" => ShapeType.Cone,
        "rect" => ShapeType.Rect,
        "cross" => ShapeType.Cross,
        "halfRoom" => ShapeType.HalfRoom,
        "knockback" or "arrow" => ShapeType.Knockback,
        "gaze" => ShapeType.Gaze,
        _ => ShapeType.None,
    };

    public static float? OmenConeAngle(string? omen)
    {
        if (string.IsNullOrEmpty(omen))
            return null;
        var m = FanRegex().Match(omen);
        return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public static float? OmenDonutInner(string? omen, float outer)
    {
        if (string.IsNullOrEmpty(omen))
            return null;
        var m = DonutRegex().Match(omen);
        if (!m.Success)
            return null;
        // e.g. "gl_sircle_5003bf": inner/outer ratio digits are not standardized; treat first two digits as inner radius
        // when plausible.
        var inner = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return inner > 0 && inner < outer ? inner : null;
    }

    [GeneratedRegex(@"fan(\d{3})")]
    private static partial Regex FanRegex();

    [GeneratedRegex(@"(?:sircle|donut)_?(\d{2})\d{2}")]
    private static partial Regex DonutRegex();
}
