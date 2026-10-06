using System.Numerics;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Tests;

public class GeometryTests
{
    private static readonly Vector2 C = new(100, 100);

    [Fact]
    public void HeadingConvention()
    {
        // 0 = south (+Y), π/2 = east (+X), ±π = north.
        Assert.Equal(new Vector2(0, 1), Round(Angles.Dir(0)));
        Assert.Equal(new Vector2(1, 0), Round(Angles.Dir(MathF.PI / 2)));
        Assert.Equal(new Vector2(0, -1), Round(Angles.Dir(MathF.PI)));
        Assert.Equal(0, Angles.Toward(C, C + new Vector2(0, 5)), 0.001f);
        Assert.Equal(-MathF.PI / 2, Angles.Toward(C, C + new Vector2(-5, 0)), 0.001f);
    }

    [Fact]
    public void AngleLerpTakesShortestArc()
    {
        var mid = Angles.Lerp(3.0f, -3.0f, 0.5f);
        Assert.True(MathF.Abs(MathF.Abs(mid) - MathF.PI) < 0.01f, $"got {mid}");
    }

    [Fact]
    public void Circle()
    {
        var s = AoeShape.Circle(5);
        Assert.True(ShapeMath.Contains(s, C, 0, C + new Vector2(3, 3)));
        Assert.False(ShapeMath.Contains(s, C, 0, C + new Vector2(4, 4)));
        Assert.True(ShapeMath.Contains(s, C, 0, C + new Vector2(4, 4), 1f));
    }

    [Fact]
    public void Cone()
    {
        var s = AoeShape.Cone(40, 90);
        // Facing east: points within ±45° of +X.
        Assert.True(ShapeMath.Contains(s, C, MathF.PI / 2, C + new Vector2(10, 9)));
        Assert.False(ShapeMath.Contains(s, C, MathF.PI / 2, C + new Vector2(10, 11)));
        Assert.False(ShapeMath.Contains(s, C, MathF.PI / 2, C + new Vector2(-10, 0)));
    }

    [Fact]
    public void RectAndCenteredRect()
    {
        var fwd = AoeShape.Rect(40, 10);
        Assert.True(ShapeMath.Contains(fwd, C, 0, C + new Vector2(4.9f, 30)));
        Assert.False(ShapeMath.Contains(fwd, C, 0, C + new Vector2(5.1f, 30)));
        Assert.False(ShapeMath.Contains(fwd, C, 0, C + new Vector2(0, -1)));
        var centered = AoeShape.Rect(40, 10, 40);
        Assert.True(ShapeMath.Contains(centered, C, 0, C + new Vector2(0, -30)));
    }

    [Fact]
    public void DonutCrossHalfRoom()
    {
        var donut = AoeShape.Donut(6, 20);
        Assert.False(ShapeMath.Contains(donut, C, 0, C + new Vector2(3, 0)));
        Assert.True(ShapeMath.Contains(donut, C, 0, C + new Vector2(10, 0)));
        var cross = AoeShape.Cross(20, 6);
        Assert.True(ShapeMath.Contains(cross, C, 0, C + new Vector2(15, 1)));
        Assert.False(ShapeMath.Contains(cross, C, 0, C + new Vector2(10, 10)));
        var half = new AoeShape(ShapeType.HalfRoom, 40);
        Assert.True(ShapeMath.Contains(half, C, -MathF.PI / 2, C + new Vector2(-1, 5)));
        Assert.False(ShapeMath.Contains(half, C, -MathF.PI / 2, C + new Vector2(1, 5)));
    }

    [Fact]
    public void CastTypeMapping()
    {
        Assert.Equal(ShapeType.None, ShapeResolver.FromCastType(1));
        Assert.Equal(ShapeType.Circle, ShapeResolver.FromCastType(2));
        Assert.Equal(ShapeType.Cone, ShapeResolver.FromCastType(13));
        Assert.Equal(ShapeType.Rect, ShapeResolver.FromCastType(12));
        Assert.Equal(90f, ShapeResolver.OmenConeAngle("gl_fan090_1bf"));
        Assert.Null(ShapeResolver.OmenConeAngle("m0462fan_a_o0c"));
    }

    private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X, 4) + 0f, MathF.Round(v.Y, 4) + 0f);
}

public class TrackTests
{
    private static PositionTrack Track(params (int T, float X, float Y, float H, PosFlags F)[] s) =>
        new(s.Select(x => x.T).ToArray(), s.Select(x => x.X).ToArray(), s.Select(x => x.Y).ToArray(), new float[s.Length],
            s.Select(x => x.H).ToArray(), s.Select(x => x.F).ToArray());

    [Fact]
    public void InterpolatesPositionAndHeading()
    {
        var t = Track((0, 0, 0, 3.0f, PosFlags.None), (1000, 10, 0, -3.0f, PosFlags.None));
        Assert.True(t.TrySample(500, out var p, out var h));
        Assert.Equal(5, p.X, 0.01f);
        Assert.True(MathF.Abs(MathF.Abs(h) - MathF.PI) < 0.02f);
    }

    [Fact]
    public void ClampsBeforeAndAfter()
    {
        var t = Track((1000, 1, 1, 0, PosFlags.None), (2000, 2, 2, 0, PosFlags.None));
        t.TrySample(0, out var before, out _);
        t.TrySample(5000, out var after, out _);
        Assert.Equal(new Vector2(1, 1), before);
        Assert.Equal(new Vector2(2, 2), after);
    }

    [Fact]
    public void TeleportDoesNotInterpolate()
    {
        var t = Track((0, 0, 0, 0, PosFlags.None), (1000, 10, 0, 0, PosFlags.Teleport));
        t.TrySample(900, out var p, out _);
        Assert.Equal(0, p.X);
        t.TrySample(1000, out p, out _);
        Assert.Equal(10, p.X);
    }

    [Fact]
    public void LongGapHoldsThenMoves()
    {
        var t = Track((0, 0, 0, 0, PosFlags.None), (10000, 10, 0, 0, PosFlags.None));
        t.TrySample(5000, out var p, out _);
        Assert.Equal(0, p.X);
        t.TrySample(9500, out p, out _);
        Assert.Equal(5, p.X, 0.01f);
    }
}
