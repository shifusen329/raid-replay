using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Model;

namespace RaidReplay.Rendering;

public static class Palette
{
    public static uint Rgba(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, Math.Clamp(a, 0, 1)));

    public static Vector4 ForCategory(AoeCategory c) => c switch
    {
        AoeCategory.Danger => new Vector4(1f, 0.38f, 0.12f, 1),
        AoeCategory.HiddenDanger => new Vector4(0.95f, 0.2f, 0.85f, 1),
        AoeCategory.Fake => new Vector4(0.65f, 0.65f, 0.65f, 1),
        AoeCategory.Tower => new Vector4(0.2f, 0.8f, 1f, 1),
        AoeCategory.Stack => new Vector4(0.3f, 0.95f, 0.35f, 1),
        AoeCategory.Spread => new Vector4(0.95f, 0.45f, 0.85f, 1),
        AoeCategory.Tankbuster => new Vector4(1f, 0.15f, 0.15f, 1),
        AoeCategory.Raidwide => new Vector4(1f, 0.6f, 0.2f, 1),
        AoeCategory.Knockback => new Vector4(0.95f, 0.95f, 0.95f, 1),
        AoeCategory.Gaze => new Vector4(0.7f, 0.4f, 1f, 1),
        AoeCategory.Failure => new Vector4(0.7f, 0.05f, 0.05f, 1),
        AoeCategory.Bait => new Vector4(1f, 0.85f, 0.2f, 1),
        AoeCategory.Hazard => new Vector4(0.6f, 0.3f, 1f, 1),
        _ => new Vector4(0.5f, 0.65f, 0.85f, 1),
    };

    public static Vector4 Parse(string? hex, Vector4 fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || (hex.Length != 7 && hex.Length != 9))
            return fallback;
        if (!uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return fallback;
        if (hex.Length == 7)
            v = (v << 8) | 0xFF;
        return new Vector4(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    public static uint With(Vector4 c, float alpha) => Rgba(c.X, c.Y, c.Z, c.W * alpha);

    /// <summary>Role colors: tank blue, healer green, DPS red.</summary>
    public static Vector4 ForJob(byte job)
    {
        var info = Core.GameData.Jobs.Get(job);
        return info?.Role switch
        {
            1 => new Vector4(0.25f, 0.45f, 0.95f, 1),
            4 => new Vector4(0.25f, 0.8f, 0.35f, 1),
            2 or 3 => new Vector4(0.85f, 0.25f, 0.25f, 1),
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1),
        };
    }

    public static Vector4 ForIncident(IncidentKind k) => k switch
    {
        IncidentKind.Death or IncidentKind.FellOff => new Vector4(1f, 0.3f, 0.3f, 1),
        IncidentKind.Enrage => new Vector4(1f, 0.55f, 0.1f, 1),
        IncidentKind.FailureAction or IncidentKind.TowerUnderSoaked => new Vector4(1f, 0.75f, 0.2f, 1),
        _ => new Vector4(0.95f, 0.9f, 0.5f, 1),
    };

    public static Vector4 ForExpected(ExpectedSource s) => s switch
    {
        ExpectedSource.Learned => new Vector4(0.3f, 1f, 0.6f, 1),
        ExpectedSource.Soak => new Vector4(0.3f, 0.85f, 1f, 1),
        _ => new Vector4(1f, 1f, 0.4f, 1),
    };

    public static readonly uint Text = Rgba(1, 1, 1, 0.95f);
    public static readonly uint TextShadow = Rgba(0, 0, 0, 0.85f);
    public static readonly uint ArenaFill = Rgba(0.12f, 0.12f, 0.14f, 0.55f);
    public static readonly uint ArenaEdge = Rgba(0.9f, 0.85f, 0.7f, 0.8f);
    public static readonly uint Background = Rgba(0.06f, 0.06f, 0.07f, 1);
    public static readonly uint Grid = Rgba(1, 1, 1, 0.06f);
}
