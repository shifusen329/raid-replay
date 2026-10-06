using System.Numerics;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Geometry;

public enum ShapeType : byte
{
    None,
    Circle,
    Donut,
    Cone,
    Rect,
    Cross,
    HalfRoom,
    Knockback,
    Gaze,
}

/// <summary>
/// Shape dimensions. Rect extends <see cref="Length"/> forward and <see cref="BackLength"/> backward from the
/// origin along the heading, <see cref="Width"/> wide. Cone angle is the full opening angle. HalfRoom is the
/// half-plane in front of the origin (bounded by <see cref="Radius"/> for drawing).
/// </summary>
public readonly record struct AoeShape(
    ShapeType Type, float Radius = 0, float InnerRadius = 0, float AngleRad = 0, float Length = 0, float Width = 0,
    float BackLength = 0)
{
    public static readonly AoeShape None = new(ShapeType.None);

    public static AoeShape Circle(float r) => new(ShapeType.Circle, r);
    public static AoeShape Donut(float inner, float outer) => new(ShapeType.Donut, outer, inner);
    public static AoeShape Cone(float r, float angleDeg) => new(ShapeType.Cone, r, AngleRad: angleDeg * MathF.PI / 180f);
    public static AoeShape Rect(float length, float width, float back = 0) => new(ShapeType.Rect, Length: length, Width: width, BackLength: back);
    public static AoeShape Cross(float length, float width) => new(ShapeType.Cross, Length: length, Width: width);

    public bool IsArea => Type is not (ShapeType.None or ShapeType.Knockback or ShapeType.Gaze);

    public override string ToString() => Type switch
    {
        ShapeType.Circle => $"circle r={Radius:0.#}",
        ShapeType.Donut => $"donut {InnerRadius:0.#}-{Radius:0.#}",
        ShapeType.Cone => $"cone r={Radius:0.#} {AngleRad * 180 / MathF.PI:0}°",
        ShapeType.Rect => $"rect {Length:0.#}x{Width:0.#}{(BackLength > 0 ? $" back {BackLength:0.#}" : "")}",
        ShapeType.Cross => $"cross {Length:0.#}x{Width:0.#}",
        ShapeType.HalfRoom => "half-room",
        ShapeType.Knockback => $"knockback {Radius:0.#}",
        ShapeType.Gaze => "gaze",
        _ => "none",
    };
}

public static class ShapeMath
{
    /// <summary>True if point p (expanded by tolerance, e.g. a player hitbox) intersects the shape.</summary>
    public static bool Contains(in AoeShape s, Vector2 origin, float heading, Vector2 p, float tolerance = 0f)
    {
        var d = p - origin;
        switch (s.Type)
        {
            case ShapeType.Circle:
                return d.Length() <= s.Radius + tolerance;
            case ShapeType.Donut:
            {
                var len = d.Length();
                return len <= s.Radius + tolerance && len >= s.InnerRadius - tolerance;
            }
            case ShapeType.Cone:
            {
                var len = d.Length();
                if (len > s.Radius + tolerance)
                    return false;
                if (len <= tolerance)
                    return true;
                var angle = MathF.Abs(Angles.Wrap(MathF.Atan2(d.X, d.Y) - heading));
                if (angle <= s.AngleRad / 2)
                    return true;
                // Distance from the point to the nearest cone edge ray.
                var edge = heading + (MathF.Sign(Angles.Wrap(MathF.Atan2(d.X, d.Y) - heading)) * s.AngleRad / 2);
                var dir = Angles.Dir(edge);
                var along = Vector2.Dot(d, dir);
                var perp = along > 0 ? MathF.Abs((d.X * dir.Y) - (d.Y * dir.X)) : len;
                return perp <= tolerance;
            }
            case ShapeType.Rect:
            {
                var fwd = Angles.Dir(heading);
                var along = Vector2.Dot(d, fwd);
                var side = MathF.Abs((d.X * fwd.Y) - (d.Y * fwd.X));
                return along >= -s.BackLength - tolerance && along <= s.Length + tolerance &&
                       side <= (s.Width / 2) + tolerance;
            }
            case ShapeType.Cross:
            {
                var fwd = Angles.Dir(heading);
                var along = MathF.Abs(Vector2.Dot(d, fwd));
                var side = MathF.Abs((d.X * fwd.Y) - (d.Y * fwd.X));
                var halfW = (s.Width / 2) + tolerance;
                var len = s.Length + tolerance;
                return (along <= len && side <= halfW) || (side <= len && along <= halfW);
            }
            case ShapeType.HalfRoom:
                return Vector2.Dot(d, Angles.Dir(heading)) >= -tolerance;
            default:
                return false;
        }
    }
}
