using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RaidReplay.Rendering;

/// <summary>World ↔ screen transform for the replay canvas with wheel zoom and drag pan. North (−Y) is up.</summary>
public sealed class ArenaView
{
    public Vector2 CanvasMin { get; private set; }
    public Vector2 CanvasMax { get; private set; }
    public Vector2 WorldCenter { get; set; } = new(100, 100);
    public float PixelsPerYalm { get; set; } = 12f;
    public bool Hovered { get; private set; }

    /// <summary>Mouse-wheel zoom (off for embedded snapshot maps inside scrolling panels).</summary>
    public bool WheelZoom { get; set; } = true;

    public Vector2 CanvasSize => CanvasMax - CanvasMin;
    public Vector2 CanvasCenter => (CanvasMin + CanvasMax) / 2;

    public Vector2 ToScreen(Vector2 world) => CanvasCenter + ((world - WorldCenter) * PixelsPerYalm);

    public Vector2 ToWorld(Vector2 screen) => WorldCenter + ((screen - CanvasCenter) / PixelsPerYalm);

    public float ToPixels(float yalms) => yalms * PixelsPerYalm;

    public void Fit(Vector2 center, float radius)
    {
        WorldCenter = center;
        var size = CanvasSize;
        if (size.X > 10 && size.Y > 10)
            PixelsPerYalm = Math.Min(size.X, size.Y) / (2 * radius);
    }

    /// <summary>Claims the canvas area (an invisible button) and processes zoom/pan input.</summary>
    public void Begin(string id, Vector2 size)
    {
        CanvasMin = ImGui.GetCursorScreenPos();
        CanvasMax = CanvasMin + Vector2.Max(size, new Vector2(50, 50));
        ImGui.InvisibleButton(id, CanvasMax - CanvasMin,
                              ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        Hovered = ImGui.IsItemHovered();
        if (WheelZoom)
            ImGuiP.SetItemUsingMouseWheel(); // keep the parent from scrolling while zooming
        var io = ImGui.GetIO();
        if (Hovered && WheelZoom && io.MouseWheel != 0)
        {
            var mouse = ImGui.GetMousePos();
            var before = ToWorld(mouse);
            PixelsPerYalm = Math.Clamp(PixelsPerYalm * MathF.Pow(1.15f, io.MouseWheel), 1f, 200f);
            var after = ToWorld(mouse);
            WorldCenter += before - after;
        }

        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                                     ImGui.IsMouseDragging(ImGuiMouseButton.Left)))
        {
            WorldCenter -= io.MouseDelta / PixelsPerYalm;
        }
    }

    public Vector2 MouseWorld => ToWorld(ImGui.GetMousePos());
}
