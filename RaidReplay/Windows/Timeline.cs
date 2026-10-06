using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Model;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>Scrubbable timeline with lanes for phases, mechanics, boss casts, deaths and wipe-report incidents.</summary>
public static class Timeline
{
    private static readonly Vector4[] PhaseColors =
    [
        new(0.3f, 0.45f, 0.8f, 0.55f), new(0.6f, 0.35f, 0.75f, 0.55f), new(0.3f, 0.65f, 0.5f, 0.55f),
        new(0.75f, 0.55f, 0.25f, 0.55f), new(0.7f, 0.3f, 0.35f, 0.55f),
    ];

    /// <summary>Draws the timeline; returns a new time if the user scrubbed.</summary>
    public static int? Draw(PullReplay r, WipeReport? report, int t, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = 74 * scale;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        ImGui.InvisibleButton("##timeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, Palette.Rgba(0.08f, 0.08f, 0.1f, 1), 4);

        var t0 = r.FirstMs;
        var t1 = Math.Max(r.LastMs, t0 + 1000);
        float X(int ms) => min.X + ((float)(ms - t0) / (t1 - t0) * width);
        int Ms(float x) => t0 + (int)((x - min.X) / width * (t1 - t0));
        var mouse = ImGui.GetMousePos();
        string? tip = null;

        // Lane 1: phases.
        var laneH = 14 * scale;
        var y = min.Y + (2 * scale);
        var i = 0;
        foreach (var p in r.Phases.Where(p => !p.IsSegment))
        {
            var a = new Vector2(X(Math.Max(p.StartMs, t0)), y);
            var b = new Vector2(X(Math.Min(p.EndMs, t1)), y + laneH);
            dl.AddRectFilled(a, b, ImGui.ColorConvertFloat4ToU32(PhaseColors[i++ % PhaseColors.Length]), 3);
            if (b.X - a.X > ImGui.CalcTextSize(p.Name).X + 6)
                dl.AddText(a + new Vector2(4, 0), Palette.Text, p.Name);
            if (hovered && mouse.X >= a.X && mouse.X <= b.X && mouse.Y >= a.Y && mouse.Y <= b.Y)
                tip = $"{p.Name} {Fmt(p.StartMs)}–{Fmt(p.EndMs)}";
        }

        // Lane 2: segments + mechanics.
        y += laneH + (2 * scale);
        foreach (var s in r.Phases.Where(p => p.IsSegment))
        {
            var a = new Vector2(X(Math.Max(s.StartMs, t0)), y);
            var b = new Vector2(X(Math.Min(s.EndMs, t1)), y + laneH);
            dl.AddRectFilled(a, b, Palette.Rgba(1, 1, 1, 0.08f), 2);
            dl.AddLine(a, a + new Vector2(0, laneH), Palette.Rgba(1, 1, 1, 0.35f));
            if (b.X - a.X > ImGui.CalcTextSize(s.Name).X + 6)
                dl.AddText(a + new Vector2(3, 0), Palette.Rgba(1, 1, 1, 0.7f), s.Name);
        }

        foreach (var m in r.Mechanics)
        {
            var x = X(m.T);
            dl.AddLine(new Vector2(x, y), new Vector2(x, y + laneH), Palette.Rgba(0.4f, 0.9f, 1, 0.8f), 1.5f);
            if (hovered && Math.Abs(mouse.X - x) < 3 && mouse.Y >= y && mouse.Y <= y + laneH)
                tip = $"{Fmt(m.T)} {m.Label}";
        }

        // Lane 3: boss casts.
        y += laneH + (2 * scale);
        foreach (var c in r.Casts)
        {
            if (c.Source.Kind != ActorKind.Boss)
                continue;
            var a = new Vector2(X(c.StartMs), y + 2);
            var b = new Vector2(Math.Max(a.X + 2, X(c.EndMs)), y + laneH - 2);
            dl.AddRectFilled(a, b, Palette.Rgba(1f, 0.6f, 0.2f, 0.6f), 2);
            if (hovered && mouse.X >= a.X && mouse.X <= b.X && mouse.Y >= y && mouse.Y <= y + laneH)
                tip = $"{Fmt(c.StartMs)} {c.Name} ({c.DurationMs / 1000f:0.0}s)";
        }

        // Lane 4: deaths + incidents.
        y += laneH + (2 * scale);
        if (report != null)
        {
            foreach (var inc in report.Incidents)
            {
                if (inc.Kind is IncidentKind.Death or IncidentKind.FellOff)
                    continue;
                var x = X(inc.T);
                var col = Palette.With(Palette.ForIncident(inc.Kind), inc.IsRootCause ? 1 : 0.7f);
                dl.AddTriangleFilled(new Vector2(x, y + laneH), new Vector2(x - 4, y + 2), new Vector2(x + 4, y + 2), col);
                if (hovered && Math.Abs(mouse.X - x) < 4 && mouse.Y >= y && mouse.Y <= y + laneH)
                    tip = $"{Fmt(inc.T)} {inc.Title}";
            }
        }

        foreach (var d in r.Deaths.Where(d => d.Victim.IsPlayer))
        {
            var x = X(d.T);
            var cy = y + (laneH / 2);
            var col = report?.RootCause?.Death == d ? Palette.Rgba(1, 0.1f, 0.1f, 1) : Palette.Rgba(1, 0.35f, 0.35f, 0.9f);
            dl.AddLine(new Vector2(x - 3, cy - 3), new Vector2(x + 3, cy + 3), col, 2);
            dl.AddLine(new Vector2(x - 3, cy + 3), new Vector2(x + 3, cy - 3), col, 2);
            if (hovered && Math.Abs(mouse.X - x) < 4 && mouse.Y >= y && mouse.Y <= y + laneH)
                tip = $"{Fmt(d.T)} {d.Victim.Name} died: {d.Cause}";
        }

        // Pull end and playhead.
        dl.AddLine(new Vector2(X(r.EndMs), min.Y), new Vector2(X(r.EndMs), max.Y), Palette.Rgba(1, 0.2f, 0.2f, 0.5f), 1);
        dl.AddLine(new Vector2(X(0), min.Y), new Vector2(X(0), max.Y), Palette.Rgba(0.3f, 1, 0.3f, 0.4f), 1);
        var px = X(t);
        dl.AddLine(new Vector2(px, min.Y), new Vector2(px, max.Y), Palette.Rgba(1, 1, 1, 0.95f), 2);

        if (hovered)
        {
            ImGui.SetTooltip(tip ?? Fmt(Ms(mouse.X)));
        }

        if (active)
            return Math.Clamp(Ms(mouse.X), t0, t1);
        return null;
    }

    public static string Fmt(int ms)
    {
        var neg = ms < 0;
        ms = Math.Abs(ms);
        var s = $"{ms / 60000}:{ms / 1000 % 60:00}.{ms / 100 % 10}";
        return neg ? "-" + s : s;
    }
}
