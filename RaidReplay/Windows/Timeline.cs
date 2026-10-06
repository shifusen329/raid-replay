using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Model;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>What the user did on the timeline this frame.</summary>
public struct TimelineInput
{
    /// <summary>Scrubbed to this time.</summary>
    public int? Seek;

    /// <summary>Clicked an incident marker.</summary>
    public Incident? Incident;

    /// <summary>Clicked a death marker.</summary>
    public DeathEvent? Death;
}

/// <summary>
/// Scrubbable, zoomable timeline: a time ruler, then lanes for phases, segments + mechanics, boss casts, and
/// wipe-report incidents + deaths, with lane labels, hover tooltips, clickable markers and an overview bar when zoomed.
/// Wheel zooms around the cursor, Shift+wheel or right/middle-drag pans, double-click the ruler to fit.
/// </summary>
public sealed class Timeline
{
    private static readonly Vector4[] PhaseColors =
    [
        new(0.3f, 0.45f, 0.8f, 0.55f), new(0.6f, 0.35f, 0.75f, 0.55f), new(0.3f, 0.65f, 0.5f, 0.55f),
        new(0.75f, 0.55f, 0.25f, 0.55f), new(0.7f, 0.3f, 0.35f, 0.55f),
    ];

    private static readonly string[] LaneNames = ["Phase", "Mech", "Casts", "Events"];

    private static readonly string[] LaneTips =
    [
        "Phases of the fight (from the encounter pack). Hover a block for its time range.",
        "Segments (grey blocks) and mechanic resolves (cyan ticks). Hover for names.",
        "Boss cast bars: each orange block is one cast, its length the cast time. Hover for the ability.",
        "Wipe-report incidents (triangles; brighter = root cause) and player deaths (red ×). Click one to jump to it.",
    ];

    public static readonly Vector4 MechanicColor = new(0.4f, 0.9f, 1, 0.85f);
    public static readonly Vector4 CastColor = new(1f, 0.6f, 0.2f, 0.6f);
    public static readonly Vector4 DeathColor = new(1, 0.35f, 0.35f, 0.9f);
    public static readonly Vector4 RootDeathColor = new(1, 0.1f, 0.1f, 1);

    private readonly Dictionary<int, string> tickLabels = new();
    private PullReplay? pull;
    private float v0;
    private float v1;
    private int lastT = int.MinValue;
    private object? pressed;
    private bool overviewDrag;

    /// <summary>Keep the playhead in view when it moves while zoomed in.</summary>
    public bool Follow { get; set; } = true;

    public float ViewStart => v0;
    public float ViewEnd => v1;
    public bool Zoomed => pull != null && v1 - v0 < (Math.Max(pull.LastMs, pull.FirstMs + 1000) - pull.FirstMs) - 1;

    private static float Scale => ImGuiHelpers.GlobalScale;
    private static float LaneH => Math.Max(14 * Scale, ImGui.GetTextLineHeight());
    private static float RulerH => ImGui.GetTextLineHeight() + (2 * Scale);
    private static float OverviewH => 6 * Scale;
    private static float Gap => 2 * Scale;

    /// <summary>Total height of the control.</summary>
    public static float Height => RulerH + (4 * (LaneH + Gap)) + OverviewH + (4 * Scale);

    private (float T0, float T1) Full => pull == null ? (0, 1000) : (pull.FirstMs, Math.Max(pull.LastMs, pull.FirstMs + 1000));

    public void Fit()
    {
        (v0, v1) = Full;
    }

    /// <summary>Zooms by <paramref name="factor"/> (&gt;1 = in) around <paramref name="pivotMs"/>.</summary>
    public void ZoomBy(float factor, float pivotMs)
    {
        var (t0, t1) = Full;
        var span = v1 - v0;
        var newSpan = Math.Clamp(span / factor, Math.Min(2000, t1 - t0), t1 - t0);
        var frac = span > 0 ? (pivotMs - v0) / span : 0.5f;
        v0 = pivotMs - (frac * newSpan);
        v1 = v0 + newSpan;
        Clamp();
    }

    public void ZoomAroundPlayhead(float factor, int t) => ZoomBy(factor, Math.Clamp(t, v0, v1));

    private void Pan(float ms)
    {
        v0 += ms;
        v1 += ms;
        Clamp();
    }

    private void Clamp()
    {
        var (t0, t1) = Full;
        var span = Math.Min(v1 - v0, t1 - t0);
        if (v0 < t0)
            v0 = t0;
        if (v0 + span > t1)
            v0 = t1 - span;
        v1 = v0 + span;
    }

    /// <summary>Draws the timeline. <paramref name="selected"/> is outlined.</summary>
    public TimelineInput Draw(PullReplay r, WipeReport? report, int t, float width, Incident? selected)
    {
        var input = default(TimelineInput);
        if (!ReferenceEquals(r, pull))
        {
            pull = r;
            tickLabels.Clear();
            Fit();
        }

        // Follow the playhead when it moves (playback or seeks) while zoomed in.
        if (Follow && t != lastT && Zoomed)
        {
            var span = v1 - v0;
            if (t < v0 + (span * 0.02f) || t > v0 + (span * 0.9f))
            {
                v0 = t - (span * 0.25f);
                v1 = v0 + span;
                Clamp();
            }
        }

        lastT = t;

        var s = Scale;
        var height = Height;
        var gutter = GutterWidth();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var lanesMinX = min.X + gutter;
        var lanesW = Math.Max(10, width - gutter);
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        ImGui.InvisibleButton("##timeline", new Vector2(width, height - OverviewH - (3 * s)),
                              ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        ImGuiP.SetItemUsingMouseWheel();
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, new Vector2(max.X, max.Y - OverviewH - (3 * s)), Theme.U32(Theme.PanelBg), 6 * s);

        float X(float ms) => lanesMinX + ((ms - v0) / (v1 - v0) * lanesW);
        float Ms(float x) => v0 + ((x - lanesMinX) / lanesW * (v1 - v0));
        var inLanes = mouse.X >= lanesMinX;
        var full = Full;

        // Wheel zoom / pan.
        if (hovered && io.MouseWheel != 0)
        {
            if (io.KeyShift)
                Pan(-io.MouseWheel * (v1 - v0) * 0.15f);
            else
                ZoomBy(MathF.Pow(1.25f, io.MouseWheel), inLanes ? Ms(mouse.X) : t);
        }

        if (active && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0)))
            Pan(-io.MouseDelta.X / lanesW * (v1 - v0));

        string? tip = null;
        object? hit = null;

        // Ruler.
        var y = min.Y + (2 * s);
        DrawRuler(dl, lanesMinX, lanesW, y);
        var rulerHovered = hovered && mouse.Y < y + RulerH;
        if (rulerHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Fit();
        y += RulerH;

        // Gutter labels.
        var laneH = LaneH;
        for (var i = 0; i < 4; i++)
        {
            var ly = y + (i * (laneH + Gap));
            dl.AddText(new Vector2(min.X + (6 * s), ly + ((laneH - ImGui.GetTextLineHeight()) / 2)), Theme.U32(Theme.TextDim), LaneNames[i]);
            if (hovered && !inLanes && mouse.Y >= ly && mouse.Y < ly + laneH)
                tip = LaneTips[i];
        }

        dl.PushClipRect(new Vector2(lanesMinX, min.Y), max, true);

        // Lane 1: phases.
        var phaseIdx = 0;
        foreach (var p in r.Phases)
        {
            if (p.IsSegment)
                continue;
            var a = new Vector2(X(Math.Max(p.StartMs, full.T0)), y);
            var b = new Vector2(X(Math.Min(p.EndMs, full.T1)), y + laneH);
            dl.AddRectFilled(a, b, ImGui.ColorConvertFloat4ToU32(PhaseColors[phaseIdx++ % PhaseColors.Length]), 3 * s);
            var visA = Math.Max(a.X, lanesMinX);
            if (b.X - visA > ImGui.CalcTextSize(p.Name).X + 6)
                dl.AddText(new Vector2(visA + (4 * s), y), Palette.Text, p.Name);
            if (hovered && inLanes && mouse.X >= a.X && mouse.X <= b.X && mouse.Y >= a.Y && mouse.Y <= b.Y)
                tip = $"Phase: {p.Name}\n{Fmt(p.StartMs)} – {Fmt(p.EndMs)} ({Fmt(p.EndMs - p.StartMs)})";
        }

        // Lane 2: segments + mechanics.
        y += laneH + Gap;
        foreach (var sg in r.Phases)
        {
            if (!sg.IsSegment)
                continue;
            var a = new Vector2(X(Math.Max(sg.StartMs, full.T0)), y);
            var b = new Vector2(X(Math.Min(sg.EndMs, full.T1)), y + laneH);
            dl.AddRectFilled(a, b, Palette.Rgba(1, 1, 1, 0.08f), 2 * s);
            dl.AddLine(a, a + new Vector2(0, laneH), Palette.Rgba(1, 1, 1, 0.35f));
            var visA = Math.Max(a.X, lanesMinX);
            if (b.X - visA > ImGui.CalcTextSize(sg.Name).X + 6)
                dl.AddText(new Vector2(visA + (3 * s), y), Palette.Rgba(1, 1, 1, 0.7f), sg.Name);
            if (hovered && inLanes && mouse.X >= a.X && mouse.X <= b.X && mouse.Y >= a.Y && mouse.Y <= b.Y)
                tip = $"Segment: {sg.Name}\n{Fmt(sg.StartMs)} – {Fmt(sg.EndMs)}";
        }

        foreach (var m in r.Mechanics)
        {
            var x = X(m.T);
            if (x < lanesMinX - 4 || x > max.X + 4)
                continue;
            dl.AddLine(new Vector2(x, y), new Vector2(x, y + laneH), Theme.U32(MechanicColor), 1.5f * s);
            if (hovered && inLanes && Math.Abs(mouse.X - x) < 3 * s && mouse.Y >= y && mouse.Y <= y + laneH)
                tip = $"Mechanic: {m.Label}\nresolves at {Fmt(m.T)}";
        }

        // Lane 3: boss casts.
        y += laneH + Gap;
        foreach (var c in r.Casts)
        {
            if (c.Source.Kind != ActorKind.Boss)
                continue;
            var a = new Vector2(X(c.StartMs), y + (2 * s));
            var b = new Vector2(Math.Max(a.X + (2 * s), X(c.EndMs)), y + laneH - (2 * s));
            if (b.X < lanesMinX || a.X > max.X)
                continue;
            dl.AddRectFilled(a, b, Theme.U32(CastColor), 2 * s);
            if (hovered && inLanes && mouse.X >= a.X - 1 && mouse.X <= b.X + 1 && mouse.Y >= y && mouse.Y <= y + laneH)
                tip = $"Boss cast: {c.Name}\n{c.Source.DisplayName} · {Fmt(c.StartMs)} · {c.DurationMs / 1000f:0.0}s";
        }

        // Lane 4: incidents + deaths.
        y += laneH + Gap;
        var hitR = 5 * s;
        if (report != null && ReferenceEquals(report.Pull, r))
        {
            foreach (var inc in report.Incidents)
            {
                if (inc.Kind is IncidentKind.Death or IncidentKind.FellOff)
                    continue;
                var x = X(inc.T);
                if (x < lanesMinX - 6 || x > max.X + 6)
                    continue;
                var col = Palette.With(Theme.Severity(inc), inc.IsRootCause ? 1 : 0.75f);
                var k = inc.IsRootCause ? 5.5f * s : 4 * s;
                dl.AddTriangleFilled(new Vector2(x, y + laneH - (1 * s)), new Vector2(x - k, y + (2 * s)), new Vector2(x + k, y + (2 * s)), col);
                if (ReferenceEquals(inc, selected))
                    dl.AddRect(new Vector2(x - k - (2 * s), y), new Vector2(x + k + (2 * s), y + laneH), Theme.U32(Theme.Accent), 2 * s, ImDrawFlags.None, 1.5f);
                if (hovered && inLanes && Math.Abs(mouse.X - x) < hitR && mouse.Y >= y && mouse.Y <= y + laneH)
                {
                    tip = $"{Fmt(inc.T)} {Theme.KindLabel(inc.Kind)}{(inc.IsRootCause ? " · ROOT CAUSE" : "")}\n{inc.Title}\n(click to select)";
                    hit = inc;
                }
            }
        }

        foreach (var d in r.Deaths)
        {
            if (!d.Victim.IsPlayer)
                continue;
            var x = X(d.T);
            if (x < lanesMinX - 6 || x > max.X + 6)
                continue;
            var cy = y + (laneH / 2);
            var root = report?.RootCause?.Death == d;
            var col = Theme.U32(root ? RootDeathColor : DeathColor);
            var k = 3.5f * s;
            dl.AddLine(new Vector2(x - k, cy - k), new Vector2(x + k, cy + k), col, 2 * s);
            dl.AddLine(new Vector2(x - k, cy + k), new Vector2(x + k, cy - k), col, 2 * s);
            if (selected?.Death == d)
                dl.AddRect(new Vector2(x - k - (3 * s), y), new Vector2(x + k + (3 * s), y + laneH), Theme.U32(Theme.Accent), 2 * s, ImDrawFlags.None, 1.5f);
            if (hovered && inLanes && Math.Abs(mouse.X - x) < hitR && mouse.Y >= y && mouse.Y <= y + laneH)
            {
                tip = $"{Fmt(d.T)} {d.Victim.Name} died{(d.RaisedMs >= 0 ? $" (raised {Fmt(d.RaisedMs)})" : "")}\n{d.Cause}\n(click to jump)";
                hit = d;
            }
        }

        // Pull start/end, hover cursor and playhead.
        var lanesTop = min.Y + (2 * s) + RulerH;
        var lanesBottom = y + laneH;
        dl.AddLine(new Vector2(X(0), lanesTop), new Vector2(X(0), lanesBottom), Palette.Rgba(0.3f, 1, 0.3f, 0.4f), 1);
        dl.AddLine(new Vector2(X(r.EndMs), lanesTop), new Vector2(X(r.EndMs), lanesBottom), Palette.Rgba(1, 0.2f, 0.2f, 0.5f), 1);
        if (hovered && inLanes)
            dl.AddLine(new Vector2(mouse.X, min.Y), new Vector2(mouse.X, lanesBottom), Palette.Rgba(1, 1, 1, 0.25f), 1);
        var px = X(t);
        dl.AddLine(new Vector2(px, min.Y + (2 * s)), new Vector2(px, lanesBottom), Palette.Rgba(1, 1, 1, 0.95f), 2 * s);
        dl.AddTriangleFilled(new Vector2(px - (5 * s), min.Y + (2 * s)), new Vector2(px + (5 * s), min.Y + (2 * s)),
                             new Vector2(px, min.Y + (8 * s)), Palette.Rgba(1, 1, 1, 0.95f));
        dl.PopClipRect();

        // Clicks: a press on a marker selects it on release; a press elsewhere scrubs.
        if (hovered && inLanes && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            pressed = hit ?? (object)"scrub";
        if (pressed != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (pressed is Incident pi && ReferenceEquals(hit, pi))
                input.Incident = pi;
            else if (pressed is DeathEvent pd && ReferenceEquals(hit, pd))
                input.Death = pd;
            pressed = null;
        }

        if (active && pressed is string && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            input.Seek = (int)Math.Clamp(Ms(mouse.X), Full.T0, Full.T1);

        if (hovered && !active)
            ImGui.SetTooltip(tip ?? (inLanes ? $"{Fmt((int)Ms(mouse.X))}\nClick/drag to scrub · wheel to zoom · Shift+wheel or right-drag to pan" : "Timeline lanes"));
        if (hovered && hit != null)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        DrawOverview(min, width, lanesMinX, lanesW, t);
        return input;
    }

    private void DrawRuler(ImDrawListPtr dl, float x0, float w, float y)
    {
        var s = Scale;
        var span = v1 - v0;
        float X(float ms) => x0 + ((ms - v0) / span * w);
        ReadOnlySpan<int> steps = [100, 250, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000];
        var step = steps[^1];
        foreach (var st in steps)
        {
            if (st / span * w >= 64 * s)
            {
                step = st;
                break;
            }
        }

        var first = (int)Math.Ceiling(v0 / step) * step;
        var col = Theme.U32(Theme.TextFaint);
        for (var ms = first; ms <= v1; ms += step)
        {
            var x = X(ms);
            dl.AddLine(new Vector2(x, y + RulerH - (4 * s)), new Vector2(x, y + RulerH), col, 1);
            if (!tickLabels.TryGetValue(ms, out var label))
            {
                label = step < 1000 ? Fmt(ms) : Fmt(ms)[..^2];
                if (tickLabels.Count < 4096)
                    tickLabels[ms] = label;
            }

            dl.AddText(new Vector2(x + (3 * s), y - (1 * s)), col, label);
        }
    }

    private void DrawOverview(Vector2 min, float width, float lanesMinX, float lanesW, int t)
    {
        var s = Scale;
        var y = min.Y + Height - OverviewH;
        ImGui.SetCursorScreenPos(new Vector2(lanesMinX, y));
        ImGui.InvisibleButton("##tloverview", new Vector2(lanesW, OverviewH));
        var dl = ImGui.GetWindowDrawList();
        var (t0, t1) = Full;
        float X(float ms) => lanesMinX + ((ms - t0) / (t1 - t0) * lanesW);
        dl.AddRectFilled(new Vector2(lanesMinX, y), new Vector2(lanesMinX + lanesW, y + OverviewH), Palette.Rgba(1, 1, 1, 0.05f), OverviewH / 2);
        if (Zoomed)
        {
            dl.AddRectFilled(new Vector2(X(v0), y), new Vector2(Math.Max(X(v0) + (4 * s), X(v1)), y + OverviewH),
                             Theme.U32(Theme.With(Theme.Accent, ImGui.IsItemHovered() || overviewDrag ? 0.6f : 0.4f)), OverviewH / 2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Visible range — drag to pan, double-click to fit");
            if (ImGui.IsItemActive())
            {
                overviewDrag = true;
                var center = t0 + ((ImGui.GetMousePos().X - lanesMinX) / lanesW * (t1 - t0));
                Pan(center - ((v0 + v1) / 2));
            }
            else
            {
                overviewDrag = false;
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Fit();
        }

        dl.AddLine(new Vector2(X(t), y - (1 * s)), new Vector2(X(t), y + OverviewH + (1 * s)), Palette.Rgba(1, 1, 1, 0.8f), 1.5f * s);
        _ = width;
    }

    private static float GutterWidth()
    {
        var w = 0f;
        foreach (var n in LaneNames)
            w = Math.Max(w, ImGui.CalcTextSize(n).X);
        return w + (12 * Scale);
    }

    // ---- legend ------------------------------------------------------------------------------------------------

    /// <summary>Legend of every timeline lane and marker (for the help tooltip).</summary>
    public static void DrawLegend()
    {
        var s = Scale;
        var h = ImGui.GetTextLineHeight();
        var w = 22 * s;

        void Row(Action<ImDrawListPtr, Vector2, Vector2> swatch, string text)
        {
            var p = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            swatch(ImGui.GetWindowDrawList(), p, p + new Vector2(w, h));
            ImGui.SameLine();
            ImGui.TextUnformatted(text);
        }

        Row((dl, a, b) => dl.AddRectFilled(a + new Vector2(0, 2 * s), b - new Vector2(0, 2 * s), ImGui.ColorConvertFloat4ToU32(PhaseColors[0]), 3 * s),
            "Phase (lane 1)");
        Row((dl, a, b) =>
        {
            dl.AddRectFilled(a + new Vector2(0, 2 * s), b - new Vector2(0, 2 * s), Palette.Rgba(1, 1, 1, 0.12f), 2 * s);
            dl.AddLine(a + new Vector2(0, 2 * s), new Vector2(a.X, b.Y - (2 * s)), Palette.Rgba(1, 1, 1, 0.4f));
        }, "Segment / sub-phase (lane 2)");
        Row((dl, a, b) => dl.AddLine(new Vector2((a.X + b.X) / 2, a.Y + (1 * s)), new Vector2((a.X + b.X) / 2, b.Y - (1 * s)), Theme.U32(MechanicColor), 2 * s),
            "Mechanic resolve (lane 2)");
        Row((dl, a, b) => dl.AddRectFilled(a + new Vector2(2 * s, 4 * s), b - new Vector2(2 * s, 4 * s), Theme.U32(CastColor), 2 * s),
            "Boss cast; length = cast time (lane 3)");
        Row((dl, a, b) =>
        {
            var c = (a.X + b.X) / 2;
            dl.AddTriangleFilled(new Vector2(c, b.Y - (2 * s)), new Vector2(c - (5 * s), a.Y + (2 * s)), new Vector2(c + (5 * s), a.Y + (2 * s)),
                                 Theme.U32(Theme.SevCritical));
        }, "Incident; red = severe / root cause, amber = mechanic error, grey-blue = minor (lane 4)");
        Row((dl, a, b) =>
        {
            var c = (a + b) / 2;
            var k = 4 * s;
            dl.AddLine(c - new Vector2(k, k), c + new Vector2(k, k), Theme.U32(DeathColor), 2 * s);
            dl.AddLine(c - new Vector2(k, -k), c + new Vector2(k, -k), Theme.U32(DeathColor), 2 * s);
        }, "Player death; bright red = the root-cause death (lane 4)");
        Row((dl, a, b) => dl.AddLine(new Vector2((a.X + b.X) / 2, a.Y), new Vector2((a.X + b.X) / 2, b.Y), Palette.Rgba(1, 1, 1, 0.95f), 2 * s),
            "Playhead");
        Row((dl, a, b) =>
        {
            dl.AddLine(new Vector2(a.X + (6 * s), a.Y), new Vector2(a.X + (6 * s), b.Y), Palette.Rgba(0.3f, 1, 0.3f, 0.6f), 1.5f);
            dl.AddLine(new Vector2(b.X - (6 * s), a.Y), new Vector2(b.X - (6 * s), b.Y), Palette.Rgba(1, 0.2f, 0.2f, 0.7f), 1.5f);
        }, "Pull start (green) and end (red)");
    }

    public static string Fmt(int ms)
    {
        var neg = ms < 0;
        ms = Math.Abs(ms);
        var s = $"{ms / 60000}:{ms / 1000 % 60:00}.{ms / 100 % 10}";
        return neg ? "-" + s : s;
    }
}
