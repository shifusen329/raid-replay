using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Rendering;

/// <summary>Draws one frame of a pull onto an <see cref="ArenaView"/> canvas.</summary>
public sealed class ReplayRenderer
{
    private readonly Configuration config;
    private readonly IGameData gameData;
    private readonly StringBuilder tooltip = new();
    private readonly List<Label> labels = [];
    private readonly List<int> order = [];
    private readonly List<(Vector2 Min, Vector2 Max)> placed = [];
    private readonly Dictionary<(string, int), string> merged = new();
    private readonly Comparison<int> byPriority;
    private PullReplay? legendFor;
    private readonly List<AoeCategory> legendCategories = [];
    private bool legendHasPads;
    private ImDrawListPtr dl;
    private ArenaView view = null!;

    public ReplayRenderer(Configuration config, IGameData gameData)
    {
        this.config = config;
        this.gameData = gameData;
        byPriority = (a, b) => labels[a].Priority != labels[b].Priority ? labels[b].Priority.CompareTo(labels[a].Priority) : a.CompareTo(b);
    }

    /// <summary>A text label queued for de-cluttered placement at the end of the frame.</summary>
    private struct Label
    {
        public Vector2 Pos;
        public Vector2 Size;
        public Vector2 Anchor;
        public string Text;
        public uint Color;
        public int Priority;
        public bool Keep;
        public int Count;
    }

    /// <summary>Incident whose expected positions are overlaid (from the wipe report).</summary>
    public Incident? Highlight { get; set; }

    /// <summary>Actor hovered on the last frame (for the inspector).</summary>
    public Actor? HoveredActor { get; private set; }

    public AoeInstance? HoveredAoe { get; private set; }

    private float Scale => ImGuiHelpers.GlobalScale;

    public void Draw(PullReplay r, ArenaView v, int t)
    {
        view = v;
        dl = ImGui.GetWindowDrawList();
        tooltip.Clear();
        labels.Clear();
        HoveredActor = null;
        HoveredAoe = null;
        dl.PushClipRect(v.CanvasMin, v.CanvasMax, true);
        dl.AddRectFilled(v.CanvasMin, v.CanvasMax, Palette.Background);

        DrawMap(r, t);
        DrawArena(r);
        DrawWaymarks(r, t);
        DrawAoes(r, t);
        if (config.ShowTethers)
            DrawTethers(r, t);
        if (config.ShowHitLines)
            DrawHitLines(r, t);
        DrawDeaths(r, t);
        DrawActors(r, t);
        if (config.ShowHeadMarkers)
            DrawHeadMarkers(r, t);
        DrawSigns(r, t);
        if (Highlight != null)
            DrawExpected(Highlight);
        FlushLabels();
        DrawCompass();
        if (config.ShowLegend)
            DrawLegend(r);

        dl.PopClipRect();
        if (v.Hovered && tooltip.Length > 0)
            ImGui.SetTooltip(tooltip.ToString().TrimEnd());
    }

    // ---- labels ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Queues a label. <paramref name="keep"/> labels (player names, expected-position labels) are never dropped;
    /// others are merged with identical nearby labels ("×3"), nudged up/down to avoid overlaps, and dropped if they
    /// still collide. In hover-only mode, non-keep labels only show near the mouse.
    /// </summary>
    private void QueueLabel(Vector2 pos, Vector2 size, Vector2 anchor, string text, uint color, int priority, bool keep)
    {
        if (config.LabelMode == 0)
        {
            Text(pos, text, color);
            return;
        }

        labels.Add(new Label { Pos = pos, Size = size, Anchor = anchor, Text = text, Color = color, Priority = priority, Keep = keep, Count = 1 });
    }

    private void FlushLabels()
    {
        if (labels.Count == 0)
            return;
        var s = Scale;
        var mergeDist = 140 * s;
        var hoverOnly = config.LabelMode == 2;
        var mouse = ImGui.GetMousePos();

        // 1. Merge identical labels that are close together.
        for (var i = 0; i < labels.Count; i++)
        {
            var li = labels[i];
            if (li.Count == 0)
                continue;
            for (var j = i + 1; j < labels.Count; j++)
            {
                var lj = labels[j];
                if (lj.Count == 0 || lj.Keep || li.Keep || !string.Equals(lj.Text, li.Text, StringComparison.Ordinal) ||
                    Vector2.Distance(lj.Anchor, li.Anchor) > mergeDist)
                    continue;
                li.Count++;
                lj.Count = 0;
                labels[j] = lj;
            }

            labels[i] = li;
        }

        // 2. Place by priority, nudging away from labels already placed.
        order.Clear();
        for (var i = 0; i < labels.Count; i++)
        {
            if (labels[i].Count > 0)
                order.Add(i);
        }

        order.Sort(byPriority);
        placed.Clear();
        foreach (var i in order)
        {
            var l = labels[i];
            if (hoverOnly && !l.Keep && !(view.Hovered && Vector2.Distance(mouse, l.Anchor) < 90 * s))
                continue;
            var text = l.Text;
            var size = l.Size;
            if (l.Count > 1)
            {
                if (!merged.TryGetValue((l.Text, l.Count), out text!))
                {
                    text = $"{l.Text} ×{l.Count}";
                    if (merged.Count > 512)
                        merged.Clear();
                    merged[(l.Text, l.Count)] = text;
                }

                size = ImGui.CalcTextSize(text);
            }

            var dy = size.Y + (1 * s);
            var found = false;
            var pos = l.Pos;
            for (var k = 0; k < 5 && !found; k++)
            {
                var off = k switch { 0 => 0, 1 => dy, 2 => -dy, 3 => 2 * dy, _ => -2 * dy };
                var p = l.Pos + new Vector2(0, off);
                if (!Overlaps(p, p + size))
                {
                    pos = p;
                    found = true;
                }
            }

            if (!found && !l.Keep)
                continue;
            placed.Add((pos, pos + size));
            Text(pos, text, l.Color);
        }
    }

    private bool Overlaps(Vector2 min, Vector2 max)
    {
        foreach (var (a, b) in placed)
        {
            if (min.X < b.X && max.X > a.X && min.Y < b.Y && max.Y > a.Y)
                return true;
        }

        return false;
    }

    // ---- legend ---------------------------------------------------------------------------------------------

    private void DrawLegend(PullReplay r)
    {
        if (!ReferenceEquals(legendFor, r))
        {
            legendFor = r;
            legendCategories.Clear();
            legendHasPads = r.Aoes.Any(a => a.Shape.Type == ShapeType.ArrowPad);
            foreach (var a in r.Aoes)
            {
                if (!legendCategories.Contains(a.Category))
                    legendCategories.Add(a.Category);
            }

            legendCategories.Sort();
        }

        var s = Scale;
        var lineH = ImGui.GetTextLineHeight();
        var rowH = lineH + (2 * s);
        var sw = 18 * s;
        const int fixedRows = 10;
        var rows = fixedRows + legendCategories.Count + (legendHasPads ? 1 : 0);
        var maxRows = Math.Max(4, (int)((view.CanvasSize.Y - (60 * s)) / rowH));
        var cols = (rows + maxRows - 1) / maxRows;
        var perCol = (rows + cols - 1) / cols;
        var colW = 190 * s;
        var pad = 8 * s;
        var size = new Vector2((cols * colW) + (2 * pad), (perCol * rowH) + (2 * pad));
        var origin = new Vector2(view.CanvasMin.X + (10 * s), view.CanvasMax.Y - size.Y - (10 * s));
        dl.AddRectFilled(origin, origin + size, Palette.Rgba(0.04f, 0.05f, 0.07f, 0.82f), 8 * s);
        dl.AddRect(origin, origin + size, Palette.Rgba(1, 1, 1, 0.08f), 8 * s);
        var text = Theme.U32(Theme.Text);
        var idx = 0;

        Vector2 Cell(int i) => origin + new Vector2(pad + (i / perCol * colW), pad + (i % perCol * rowH));

        void Row(string label)
        {
            var c = Cell(idx++);
            dl.AddText(c + new Vector2(sw + (6 * s), 0), text, label);
        }

        // Fixed entries.
        var c0 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        dl.AddCircleFilled(c0, 6 * s, Palette.Rgba(0.25f, 0.45f, 0.95f, 1), 16);
        dl.PathArcTo(c0, 8 * s, -MathF.PI / 2, MathF.PI, 16);
        dl.PathStroke(Palette.Rgba(0.3f, 1, 0.4f, 0.9f), ImDrawFlags.None, 2 * s);
        Row("Player (ring = HP)");
        var c1 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        dl.AddCircle(c1, 7 * s, Palette.Rgba(1f, 0.45f, 0.2f, 0.9f), 16, 2);
        dl.AddLine(c1, c1 + new Vector2(7 * s, 0), Palette.Rgba(1f, 0.45f, 0.2f, 0.9f), 2);
        Row("Boss / enemy (line = facing)");
        var c2 = Cell(idx) + new Vector2(0, (lineH / 2) - (2 * s));
        dl.AddRectFilled(c2, c2 + new Vector2(sw, 4 * s), Palette.Rgba(1f, 0.6f, 0.2f, 0.95f));
        Row("Cast bar");
        var c3 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        dl.AddLine(c3 - new Vector2(5 * s, 5 * s), c3 + new Vector2(5 * s, 5 * s), Palette.Rgba(1, 0.25f, 0.25f, 0.95f), 2.5f);
        dl.AddLine(c3 - new Vector2(5 * s, -5 * s), c3 + new Vector2(5 * s, -5 * s), Palette.Rgba(1, 0.25f, 0.25f, 0.95f), 2.5f);
        Row("Death");
        var c4 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        var k = 5 * s;
        dl.AddQuadFilled(c4 + new Vector2(0, -k), c4 + new Vector2(k, 0), c4 + new Vector2(0, k), c4 + new Vector2(-k, 0), Palette.Rgba(1, 0.9f, 0.3f, 0.95f));
        Row("Head marker");

        // Stack, spread and cone markers have their own icons; this row is wider than a swatch, so it lays itself out.
        var icons = Cell(idx++);
        var iconSize = lineH;
        var ix = 0f;
        foreach (var name in (string[])["stack", "spread", "cone"])
        {
            if (MarkerIcon(name) is { } icon)
                dl.AddImage(icon, icons + new Vector2(ix, 0), icons + new Vector2(ix + iconSize, iconSize));
            ix += iconSize + (2 * s);
        }

        dl.AddText(icons + new Vector2(ix + (4 * s), 0), text, "Stack / spread / cone");
        var c5 = Cell(idx) + new Vector2(0, lineH / 2);
        dl.AddLine(c5, c5 + new Vector2(sw, 0), Palette.Rgba(0.75f, 0.45f, 1f, 0.85f), 2.5f);
        Row("Tether");
        var c6 = Cell(idx) + new Vector2(0, lineH / 2);
        dl.AddLine(c6, c6 + new Vector2(sw, 0), Palette.Rgba(1, 0.3f, 0.3f, 0.6f), 1.5f);
        Row("Enemy hit (buster/auto)");
        var c7 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        dl.AddCircle(c7, 6 * s, Palette.With(Palette.ForExpected(ExpectedSource.Soak), 0.9f), 16, 2);
        Row("Should have been here");
        var c8 = Cell(idx) + new Vector2(sw / 2, lineH / 2);
        dl.AddCircle(c8, 7 * s, Palette.Rgba(1, 1, 1, 0.55f), 16, 1);
        Row("Thin outline = inferred");

        if (legendHasPads)
        {
            var padAoe = r.Aoes.First(a => a.Shape.Type == ShapeType.ArrowPad);
            DrawArrowPadAt(Cell(idx) + new Vector2(sw / 2, lineH / 2), 7 * s, MathF.PI / 2, Palette.Parse(padAoe.Color, Palette.ForCategory(padAoe.Category)), 1f);
            Row("Arrow teleporter (chevron = where it sends you)");
        }

        foreach (var cat in legendCategories)
        {
            var c = Cell(idx);
            var col = Palette.ForCategory(cat);
            dl.AddRectFilled(c + new Vector2(1 * s, 2 * s), c + new Vector2(sw - (1 * s), lineH - (2 * s)), Palette.With(col, 0.45f), 3 * s);
            dl.AddRect(c + new Vector2(1 * s, 2 * s), c + new Vector2(sw - (1 * s), lineH - (2 * s)), Palette.With(col, 0.95f), 3 * s);
            Row(CategoryLabel(cat));
        }
    }

    private static string CategoryLabel(AoeCategory c) => c switch
    {
        AoeCategory.Danger => "AoE: avoid",
        AoeCategory.HiddenDanger => "AoE: hidden danger",
        AoeCategory.Fake => "AoE: fake",
        AoeCategory.Tower => "Tower (n = soakers)",
        AoeCategory.Stack => "Stack",
        AoeCategory.Spread => "Spread",
        AoeCategory.Tankbuster => "Tankbuster",
        AoeCategory.Raidwide => "Raidwide",
        AoeCategory.Knockback => "Knockback (arrow)",
        AoeCategory.Gaze => "Gaze (look away)",
        AoeCategory.Failure => "Failure (mechanic failed)",
        AoeCategory.Bait => "Bait",
        AoeCategory.Hazard => "Hazard (lingering)",
        AoeCategory.Info => "Info",
        _ => c.ToString(),
    };

    /// <summary>
    /// A lightweight snapshot of one incident for the wipe card: arena, waymarks, the incident's AoEs (faint), bosses,
    /// players as job-icon dots (involved ones ringed) and ghosts with arrows to where players should have been.
    /// Positions come from the incident snapshot, so this does no track sampling for players.
    /// </summary>
    public void DrawMiniMap(PullReplay r, ArenaView v, Incident? inc)
    {
        view = v;
        dl = ImGui.GetWindowDrawList();
        tooltip.Clear();
        labels.Clear();
        HoveredActor = null;
        HoveredAoe = null;
        var t = inc?.T ?? r.EndMs;
        dl.PushClipRect(v.CanvasMin, v.CanvasMax, true);
        dl.AddRectFilled(v.CanvasMin, v.CanvasMax, Palette.Background);
        DrawMap(r, t);
        DrawArena(r);
        DrawWaymarks(r, t);

        var any = false;
        if (inc != null)
        {
            foreach (var aoe in inc.Aoes)
            {
                DrawFaintAoe(aoe, t);
                any = true;
            }
        }

        if (!any)
        {
            foreach (var aoe in r.Aoes)
            {
                if (aoe.Category != AoeCategory.Fake && Math.Abs(aoe.ResolveMs - t) <= 1000)
                    DrawFaintAoe(aoe, t);
            }
        }

        var mouse = ImGui.GetMousePos();
        foreach (var a in r.Actors)
        {
            if (a.Kind != ActorKind.Boss || !a.Render || !a.IsPresent(t) || a.IsHidden(t) || !a.Track.TrySample(t, out var bp, out var bh))
                continue;
            var p = view.ToScreen(bp);
            var radius = Math.Max(5 * Scale, view.ToPixels(a.Radius > 0 ? a.Radius : 1));
            var col = Palette.Rgba(1f, 0.45f, 0.2f, 0.8f);
            dl.AddCircle(p, radius, col, Segments(radius), 1.5f);
            dl.AddLine(p, p + (Angles.Dir(bh) * radius), col, 1.5f);
            if (v.Hovered && Vector2.Distance(mouse, p) <= radius)
                tooltip.AppendLine(a.DisplayName);
        }

        if (inc != null && inc.Snapshot.Count > 0)
        {
            DrawExpected(inc, true);
            foreach (var s in inc.Snapshot)
                DrawSnapshotPlayer(s, mouse, v.Hovered);
        }
        else
        {
            foreach (var p in r.Party)
            {
                if (p.Track.TrySample(t, out var pos, out _))
                    DrawDot(p.Job, view.ToScreen(pos), 1f);
            }
        }

        FlushLabels();

        dl.PopClipRect();
        dl.AddRect(v.CanvasMin, v.CanvasMax, Palette.Rgba(1, 1, 1, 0.08f));
        if (v.Hovered && tooltip.Length > 0)
            ImGui.SetTooltip(tooltip.ToString().TrimEnd());
    }

    private void DrawFaintAoe(AoeInstance aoe, int t)
    {
        var (origin, heading) = aoe.Placement(t);
        var baseColor = Palette.Parse(aoe.Color, Palette.ForCategory(aoe.Category));
        if (aoe.Shape.Type == ShapeType.ArrowPad)
            DrawArrowPad(origin, heading, aoe.Shape.Radius, baseColor, 0.5f);
        else
            DrawShape(aoe.Shape, origin, heading, Palette.With(baseColor, 0.12f), Palette.With(baseColor, 0.5f), true);
    }

    /// <summary>
    /// A directional pad as the game draws Tele-trouncing teleporters: an amber disc with a darker rim and a chevron
    /// pointing the way it sends you. Persistent objects, so a fixed opacity rather than a telegraph fade.
    /// </summary>
    private void DrawArrowPad(Vector2 origin, float heading, float radius, Vector4 color, float alpha) =>
        DrawArrowPadAt(view.ToScreen(origin), Math.Max(view.ToPixels(radius), 6 * Scale), heading, color, alpha);

    private void DrawArrowPadAt(Vector2 c, float r, float heading, Vector4 color, float alpha)
    {
        var seg = Segments(r);
        var dark = new Vector4(color.X * 0.6f, color.Y * 0.42f, color.Z * 0.25f, color.W);
        var light = new Vector4(Math.Min(1, color.X + 0.08f), Math.Min(1, color.Y + 0.12f), Math.Min(1, color.Z + 0.1f), color.W);
        dl.AddCircleFilled(c, r * 1.22f, Palette.With(color, 0.16f * alpha), seg);
        dl.AddCircleFilled(c, r, Palette.With(color, 0.85f * alpha), seg);
        dl.AddCircleFilled(c, r * 0.7f, Palette.With(light, 0.35f * alpha), seg);
        dl.AddCircle(c, r, Palette.With(dark, 0.95f * alpha), seg, Math.Max(1.5f, r * 0.09f));

        // Chevron: world heading (sin h, cos h) maps straight onto screen space (north up).
        var dir = Angles.Dir(heading);
        var perp = new Vector2(-dir.Y, dir.X);
        var tip = c + (dir * r * 0.42f);
        var back = c - (dir * r * 0.18f);
        dl.PathClear();
        dl.PathLineTo(back + (perp * r * 0.46f));
        dl.PathLineTo(tip);
        dl.PathLineTo(back - (perp * r * 0.46f));
        dl.PathStroke(Palette.With(dark, alpha), ImDrawFlags.None, Math.Max(2f, r * 0.2f));
    }

    private void DrawDot(byte job, Vector2 p, float alpha)
    {
        var radius = 7 * Scale;
        dl.AddCircleFilled(p, radius, Palette.Rgba(0, 0, 0, 0.65f * alpha), 20);
        var icon = Jobs.Get(job)?.Icon ?? 0;
        if (icon != 0)
        {
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
            dl.AddImage(tex.Handle, p - new Vector2(radius), p + new Vector2(radius), Vector2.Zero, Vector2.One, Palette.Rgba(1, 1, 1, alpha));
        }
        else
        {
            dl.AddCircleFilled(p, radius * 0.8f, Palette.With(Palette.ForJob(job), alpha), 20);
        }
    }

    private void DrawSnapshotPlayer(PlayerSnapshot s, Vector2 mouse, bool hovered)
    {
        var p = view.ToScreen(s.Pos);
        var radius = 7 * Scale;
        var dir = Angles.Dir(s.Heading);
        var perp = new Vector2(-dir.Y, dir.X);
        var role = Palette.ForJob(s.Player.Job);
        if (s.Alive)
        {
            dl.AddTriangleFilled(p + (dir * (radius + (5 * Scale))), p + (dir * radius) + (perp * 3 * Scale), p + (dir * radius) - (perp * 3 * Scale),
                                 Palette.With(role, s.Involved ? 1f : 0.7f));
        }

        if (s.Involved)
            dl.AddCircle(p, radius + (3 * Scale), Theme.U32(Theme.SevCritical), 24, 2.5f * Scale);
        DrawDot(s.Player.Job, p, !s.Alive ? 0.35f : s.Involved ? 1f : 0.75f);
        if (!s.Alive)
        {
            var k = 4 * Scale;
            var col = Palette.Rgba(1, 0.3f, 0.3f, 0.95f);
            dl.AddLine(p - new Vector2(k, k), p + new Vector2(k, k), col, 2);
            dl.AddLine(p - new Vector2(k, -k), p + new Vector2(k, -k), col, 2);
        }

        if (hovered && Vector2.Distance(mouse, p) <= radius + (2 * Scale))
        {
            tooltip.AppendLine($"{s.Slot} {DisplayName(s.Player)}{(s.Alive ? "" : " (dead)")}{(s.HpPct >= 0 ? $"  HP {s.HpPct:0}%" : "")}");
            if (s.MissDistance is { } miss)
                tooltip.AppendLine($"  {miss:0.0}y from {s.ExpectedNote ?? s.ExpectedSource.ToString()}");
        }
    }

    // ---- background -----------------------------------------------------------------------------------------

    private void DrawMap(PullReplay r, int t)
    {
        if (!config.ShowMapTexture || r.Encounter?.Def.Arena is { UseMapTexture: false })
            return;
        var map = gameData.GetMap((uint)r.MapIdAt(t));
        if (map == null)
            return;
        var tex = Plugin.TextureProvider.GetFromGame(map.TexturePath).GetWrapOrEmpty();
        if (tex.Width <= 1)
            return;
        // Texture pixel = (world + offset) * sizeFactor / 100 + 1024 (verified on map 79 against the arena).
        var size = (float)tex.Width;
        Vector2 Uv(Vector2 w) => new((((w.X + map.OffsetX) * map.SizeFactor / 100f) + (size / 2)) / size,
                                     (((w.Y + map.OffsetY) * map.SizeFactor / 100f) + (size / 2)) / size);
        var wMin = view.ToWorld(view.CanvasMin);
        var wMax = view.ToWorld(view.CanvasMax);
        dl.AddImage(tex.Handle, view.CanvasMin, view.CanvasMax, Uv(wMin), Uv(wMax), Palette.Rgba(1, 1, 1, 0.55f));
    }

    private void DrawArena(PullReplay r)
    {
        var arena = r.Encounter?.Def.Arena;
        if (arena == null)
        {
            // Light grid every 5 yalms.
            var wMin = view.ToWorld(view.CanvasMin);
            var wMax = view.ToWorld(view.CanvasMax);
            for (var x = MathF.Floor(wMin.X / 5) * 5; x <= wMax.X; x += 5)
                dl.AddLine(view.ToScreen(new Vector2(x, wMin.Y)), view.ToScreen(new Vector2(x, wMax.Y)), Palette.Grid);
            for (var y = MathF.Floor(wMin.Y / 5) * 5; y <= wMax.Y; y += 5)
                dl.AddLine(view.ToScreen(new Vector2(wMin.X, y)), view.ToScreen(new Vector2(wMax.X, y)), Palette.Grid);
            return;
        }

        var c = view.ToScreen(new Vector2(arena.Center[0], arena.Center[1]));
        if (arena.Shape == "circle")
        {
            dl.AddCircleFilled(c, view.ToPixels(arena.Radius), Palette.ArenaFill, 96);
            dl.AddCircle(c, view.ToPixels(arena.Radius), Palette.ArenaEdge, 96, 2f);
        }
        else
        {
            var hw = arena.Width > 0 ? arena.Width / 2 : arena.Radius;
            var hh = arena.Height > 0 ? arena.Height / 2 : arena.Radius;
            var min = view.ToScreen(new Vector2(arena.Center[0] - hw, arena.Center[1] - hh));
            var max = view.ToScreen(new Vector2(arena.Center[0] + hw, arena.Center[1] + hh));
            dl.AddRectFilled(min, max, Palette.ArenaFill);
            dl.AddRect(min, max, Palette.ArenaEdge, 0, ImDrawFlags.None, 2f);
        }

        // Cardinal ticks.
        foreach (var (dx, dy, label) in new[] { (0f, -1f, "N"), (1f, 0f, "E"), (0f, 1f, "S"), (-1f, 0f, "W") })
        {
            var p = view.ToScreen(new Vector2(arena.Center[0] + (dx * (arena.Radius + 1.5f)), arena.Center[1] + (dy * (arena.Radius + 1.5f))));
            Text(p - (ImGui.CalcTextSize(label) / 2), label, Palette.Rgba(1, 1, 1, 0.5f));
        }
    }

    private void DrawCompass()
    {
        var p = view.CanvasMin + new Vector2(18, 18) * Scale;
        dl.AddTriangleFilled(p + new Vector2(0, -10), p + new Vector2(-5, 4), p + new Vector2(5, 4), Palette.Rgba(1, 1, 1, 0.6f));
        Text(p + new Vector2(-4, 6), "N", Palette.Rgba(1, 1, 1, 0.6f));
    }

    private void DrawWaymarks(PullReplay r, int t)
    {
        var state = new Dictionary<int, Vector3>();
        foreach (var w in r.InitialWaymarks)
            state[w.Slot] = new Vector3(w.X, w.Y, w.Z);
        foreach (var e in r.Waymarks)
        {
            if (e.T > t)
                break;
            if (e.Add)
                state[e.Slot] = e.Pos;
            else
                state.Remove(e.Slot);
        }

        foreach (var (slot, pos) in state)
        {
            var p = view.ToScreen(new Vector2(pos.X, pos.Y));
            var size = Math.Max(16 * Scale, view.ToPixels(1.6f));
            var icon = gameData.WaymarkIcon(slot);
            if (icon != 0)
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
                dl.AddImage(tex.Handle, p - new Vector2(size / 2), p + new Vector2(size / 2), Vector2.Zero, Vector2.One,
                            Palette.Rgba(1, 1, 1, 0.85f));
            }
            else
            {
                var color = slot switch { 0 or 4 => Palette.Rgba(1, 0.3f, 0.3f), 1 or 5 => Palette.Rgba(1, 1, 0.3f),
                                          2 or 6 => Palette.Rgba(0.3f, 0.6f, 1), _ => Palette.Rgba(0.8f, 0.4f, 1) };
                if (slot < 4)
                    dl.AddCircle(p, size / 2, color, 24, 2);
                else
                    dl.AddRect(p - new Vector2(size / 2), p + new Vector2(size / 2), color, 0, ImDrawFlags.None, 2);
                var label = slot < 4 ? ((char)('A' + slot)).ToString() : (slot - 3).ToString();
                Text(p - (ImGui.CalcTextSize(label) / 2), label, color);
            }
        }
    }

    // ---- AoEs -----------------------------------------------------------------------------------------------

    private void DrawAoes(PullReplay r, int t)
    {
        var mouse = view.MouseWorld;
        foreach (var aoe in r.Aoes)
        {
            if (t < aoe.StartMs || t > aoe.EndMs)
                continue;
            if (aoe.Category == AoeCategory.Fake && !config.ShowFakeAoes)
                continue;
            if (aoe.Inferred && !config.ShowInferredTelegraphs && aoe.Category != AoeCategory.Hazard && aoe.Category != AoeCategory.Tower)
                continue;

            var (origin, heading) = aoe.Placement(t);
            var baseColor = Palette.Parse(aoe.Color, Palette.ForCategory(aoe.Category));
            float fill;
            if (t <= aoe.ResolveMs)
            {
                var span = Math.Max(1, aoe.ResolveMs - aoe.StartMs);
                var progress = Math.Clamp((float)(t - aoe.StartMs) / span, 0, 1);
                fill = config.AoeOpacity * (0.35f + (0.65f * progress));
            }
            else
            {
                var span = Math.Max(1, aoe.EndMs - aoe.ResolveMs);
                fill = Math.Min(0.85f, config.AoeOpacity * 2.2f) * (1 - ((float)(t - aoe.ResolveMs) / span));
            }

            if (aoe.Category == AoeCategory.Fake)
                fill *= 0.45f;
            if (!config.FillAoes)
                fill = 0;
            else if (config.OutlineRoomWideAoes && IsRoomWide(r, aoe))
                fill = Math.Min(fill, 0.05f);
            var outlineAlpha = aoe.Inferred ? 0.55f : 0.9f;
            if (aoe.Shape.Type == ShapeType.ArrowPad)
                DrawArrowPad(origin, heading, aoe.Shape.Radius, baseColor, 1f);
            else
                DrawShape(aoe.Shape, origin, heading, Palette.With(baseColor, fill), Palette.With(baseColor, outlineAlpha), aoe.Inferred);

            if (aoe.Category == AoeCategory.Tower && aoe.Soakers > 0)
            {
                var c = view.ToScreen(origin);
                Text(c - (ImGui.CalcTextSize(aoe.Soakers.ToString()) / 2), aoe.Soakers.ToString(), Palette.With(baseColor, 1));
            }

            if (view.Hovered && aoe.Shape.IsArea && ShapeMath.Contains(aoe.Shape, origin, heading, mouse))
            {
                HoveredAoe ??= aoe;
                var remain = aoe.ResolveMs - t;
                tooltip.AppendLine($"{aoe.Label} [{aoe.Category}] {aoe.Shape}" +
                                   (aoe.Shape.Type == ShapeType.ArrowPad ? $", sends you {Core.Analysis.ArrowSquare.Compass(heading)}" : "") +
                                   (remain > 0 ? $"  resolves in {remain / 1000f:0.0}s" : "") +
                                   (aoe.Inferred ? "  (inferred)" : ""));
                tooltip.AppendLine($"  0x{aoe.ActionId:X4} · {aoe.ShapeSource}{(aoe.Conf != null ? $" · conf {aoe.Conf}" : "")}");
            }
        }
    }

    /// <summary>Raidwides and shapes that cover most of the arena (they would hide everything under them).</summary>
    private static bool IsRoomWide(PullReplay r, AoeInstance aoe)
    {
        if (aoe.Category == AoeCategory.Raidwide)
            return true;
        var arena = r.Encounter?.Def.Arena;
        if (arena == null || arena.Radius <= 0)
            return false;
        return aoe.Shape.Type switch
        {
            ShapeType.Circle => aoe.Shape.Radius >= arena.Radius * 0.85f,
            _ => false,
        };
    }

    private void DrawShape(AoeShape s, Vector2 origin, float heading, uint fill, uint outline, bool dashed)
    {
        var c = view.ToScreen(origin);
        var thickness = dashed ? 1f : 1.6f;
        switch (s.Type)
        {
            case ShapeType.Circle:
            {
                var r = view.ToPixels(s.Radius);
                dl.AddCircleFilled(c, r, fill, Segments(r));
                dl.AddCircle(c, r, outline, Segments(r), thickness);
                break;
            }
            case ShapeType.Donut:
            {
                var ro = view.ToPixels(s.Radius);
                var ri = view.ToPixels(s.InnerRadius);
                var n = Segments(ro);
                for (var i = 0; i < n; i++)
                {
                    var a0 = 2 * MathF.PI * i / n;
                    var a1 = 2 * MathF.PI * (i + 1) / n;
                    dl.AddQuadFilled(c + Polar(a0, ri), c + Polar(a0, ro), c + Polar(a1, ro), c + Polar(a1, ri), fill);
                }

                dl.AddCircle(c, ro, outline, n, thickness);
                dl.AddCircle(c, ri, outline, n, thickness);
                break;
            }
            case ShapeType.Cone:
            {
                var r = view.ToPixels(s.Radius);
                // Screen angle of a world heading h (dir = (sin h, cos h), screen y down) is π/2 − h.
                var mid = (MathF.PI / 2) - heading;
                var start = mid - (s.AngleRad / 2);
                var end = mid + (s.AngleRad / 2);
                var pieces = Math.Max(1, (int)MathF.Ceiling(s.AngleRad / (MathF.PI / 2)));
                for (var i = 0; i < pieces; i++)
                {
                    var a0 = start + ((end - start) * i / pieces);
                    var a1 = start + ((end - start) * (i + 1) / pieces);
                    dl.PathClear();
                    dl.PathLineTo(c);
                    dl.PathArcTo(c, r, a0, a1, Math.Max(6, Segments(r) / 4));
                    dl.PathFillConvex(fill);
                }

                dl.PathClear();
                dl.PathLineTo(c);
                dl.PathArcTo(c, r, start, end, Math.Max(8, Segments(r) / 2));
                dl.PathStroke(outline, ImDrawFlags.Closed, thickness);
                break;
            }
            case ShapeType.Rect:
                Quad(origin, heading, s.Length, s.BackLength, s.Width, fill, outline, thickness);
                break;
            case ShapeType.Cross:
                Quad(origin, heading, s.Length, s.Length, s.Width, fill, outline, thickness);
                Quad(origin, heading + (MathF.PI / 2), s.Length, s.Length, s.Width, fill, outline, thickness);
                break;
            case ShapeType.HalfRoom:
                Quad(origin, heading, s.Radius, 0, s.Radius * 2, fill, outline, thickness);
                break;
            case ShapeType.Knockback:
            {
                var dir = Angles.Dir(heading);
                var len = Math.Max(view.ToPixels(s.Radius > 0 ? s.Radius : 3), 14 * Scale);
                var tip = c + (new Vector2(dir.X, dir.Y) * len);
                dl.AddLine(c, tip, outline, 2.5f);
                var perp = new Vector2(-dir.Y, dir.X);
                dl.AddTriangleFilled(tip, tip - (dir * 8 * Scale) + (perp * 5 * Scale), tip - (dir * 8 * Scale) - (perp * 5 * Scale), outline);
                break;
            }
            case ShapeType.Gaze:
            {
                var r = 10 * Scale;
                dl.AddCircle(c, r, outline, 24, 2);
                dl.AddCircleFilled(c, r * 0.4f, outline, 12);
                break;
            }
        }
    }

    private void Quad(Vector2 origin, float heading, float fwd, float back, float width, uint fill, uint outline, float thickness)
    {
        var f = Angles.Dir(heading);
        var p = new Vector2(-f.Y, f.X) * (width / 2);
        var a = view.ToScreen(origin + (f * fwd) + p);
        var b = view.ToScreen(origin + (f * fwd) - p);
        var c = view.ToScreen(origin - (f * back) - p);
        var d = view.ToScreen(origin - (f * back) + p);
        dl.AddQuadFilled(a, b, c, d, fill);
        dl.PathClear();
        dl.PathLineTo(a);
        dl.PathLineTo(b);
        dl.PathLineTo(c);
        dl.PathLineTo(d);
        dl.PathStroke(outline, ImDrawFlags.Closed, thickness);
    }

    private static Vector2 Polar(float angle, float r) => new(MathF.Cos(angle) * r, MathF.Sin(angle) * r);

    private static int Segments(float pixels) => Math.Clamp((int)(pixels / 3), 16, 128);

    // ---- relations ------------------------------------------------------------------------------------------

    private void DrawTethers(PullReplay r, int t)
    {
        foreach (var te in r.Tethers)
        {
            if (te.StartMs > t)
                break;
            if (t > te.EndMs || !te.Source.Track.TrySample(t, out var a, out _) || !te.Target.Track.TrySample(t, out var b, out _))
                continue;
            var col = Palette.Rgba(0.75f, 0.45f, 1f, 0.85f);
            if (te.Label != null && te.Label.Contains("Yellow", StringComparison.OrdinalIgnoreCase))
                col = Palette.Rgba(1f, 0.9f, 0.3f, 0.85f);
            dl.AddLine(view.ToScreen(a), view.ToScreen(b), col, 2.5f);
            if (te.Label != null)
            {
                var mid = view.ToScreen((a + b) / 2);
                QueueLabel(mid, ImGui.CalcTextSize(te.Label), mid, te.Label, col, 40, false);
            }
        }
    }

    private void DrawHitLines(PullReplay r, int t)
    {
        // Single-target enemy hits (tankbusters, autos) in the last 400 ms.
        foreach (var a in r.Actions)
        {
            if (a.T > t)
                break;
            if (t - a.T > 400 || a.Source.IsPlayer || a.Source.Kind == ActorKind.Pet || a.Hits.Count == 0)
                continue;
            if (a.Hits.Count > 2 || !a.Source.Track.TrySample(a.T, out var sp, out _))
                continue;
            foreach (var h in a.Hits)
            {
                if (!h.Target.IsPlayer)
                    continue;
                var alpha = 1 - ((t - a.T) / 400f);
                dl.AddLine(view.ToScreen(sp), view.ToScreen(h.TargetPos), Palette.Rgba(1, 0.3f, 0.3f, 0.6f * alpha), 1.5f);
            }
        }
    }

    private void DrawDeaths(PullReplay r, int t)
    {
        foreach (var d in r.Deaths)
        {
            if (d.T > t || (d.RaisedMs >= 0 && d.RaisedMs <= t) || !d.Victim.IsPlayer)
                continue;
            var p = view.ToScreen(d.Pos);
            var s = 6 * Scale;
            var col = Palette.Rgba(1, 0.25f, 0.25f, 0.95f);
            dl.AddLine(p - new Vector2(s, s), p + new Vector2(s, s), col, 3);
            dl.AddLine(p - new Vector2(s, -s), p + new Vector2(s, -s), col, 3);
        }
    }

    // ---- actors ---------------------------------------------------------------------------------------------

    private void DrawActors(PullReplay r, int t)
    {
        var mouse = ImGui.GetMousePos();
        foreach (var a in r.Actors)
        {
            if (!a.IsPresent(t) || !a.Render)
                continue;
            switch (a.Kind)
            {
                case ActorKind.Helper when !config.ShowHelpers:
                case ActorKind.Pet when !config.ShowPets:
                case ActorKind.EventObject:
                case ActorKind.Other:
                    continue;
            }

            if (a.Kind is ActorKind.Boss or ActorKind.Enemy && a.IsHidden(t) && !config.ShowHelpers)
                continue;
            if (!a.Track.TrySample(t, out var pos, out var heading))
                continue;
            var p = view.ToScreen(pos);
            if (a.IsPlayer)
                DrawPlayer(r, a, t, p, heading);
            else
                DrawEnemy(r, a, t, p, heading);

            var hitR = a.IsPlayer ? 10 * Scale : Math.Max(8 * Scale, view.ToPixels(a.Radius));
            if (view.Hovered && Vector2.Distance(mouse, p) <= hitR && HoveredActor == null)
            {
                HoveredActor = a;
                var hp = a.Hp.At(t);
                tooltip.AppendLine($"{DisplayName(a)} {(a.Job != 0 ? Jobs.Abbrev(a.Job) : a.Kind.ToString())}" +
                                   (a.MaxHp > 0 && hp >= 0 ? $"  HP {100f * hp / a.MaxHp:0.0}% ({hp:N0})" : ""));
                tooltip.AppendLine($"  ({pos.X:0.0}, {pos.Y:0.0}) facing {heading * 180 / MathF.PI:0}°");
                foreach (var s in r.Statuses.Where(s => s.Target == a && s.Active(t)).Take(10))
                {
                    var stacks = s.AppliedAt(t).Stacks;
                    var rem = s.ExpiresAt(t) is { } end ? $" {(end - t) / 1000f:0.0}s" : "";
                    tooltip.AppendLine($"  · {s.Name}{(stacks > 1 ? $" x{stacks}" : "")}{rem}");
                }
            }
        }
    }

    private void DrawPlayer(PullReplay r, Actor a, int t, Vector2 p, float heading)
    {
        var dead = r.Deaths.Any(d => d.Victim == a && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t));
        var role = Palette.ForJob(a.Job);
        var radius = Math.Max(7 * Scale, view.ToPixels(0.5f));

        if (config.ShowTrails && !dead)
        {
            var trailMs = (int)(config.TrailSeconds * 1000);
            Vector2? prev = null;
            for (var tt = t - trailMs; tt <= t; tt += 100)
            {
                if (!a.Track.TrySample(tt, out var tp, out _))
                    continue;
                var sp = view.ToScreen(tp);
                if (prev is { } pv)
                    dl.AddLine(pv, sp, Palette.With(role, 0.15f + (0.35f * (1 - ((float)(t - tt) / trailMs)))), 1.5f);
                prev = sp;
            }
        }

        // Facing indicator.
        var dir = Angles.Dir(heading);
        var tip = p + (dir * (radius + (7 * Scale)));
        var perp = new Vector2(-dir.Y, dir.X);
        dl.AddTriangleFilled(tip, p + (dir * radius) + (perp * 4 * Scale), p + (dir * radius) - (perp * 4 * Scale),
                             dead ? Palette.Rgba(0.5f, 0.5f, 0.5f, 0.6f) : Palette.With(role, 1));

        var icon = config.ShowJobIcons ? Jobs.Get(a.Job)?.Icon ?? 0 : 0;
        if (icon != 0)
        {
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
            dl.AddCircleFilled(p, radius, Palette.Rgba(0, 0, 0, 0.6f), 20);
            dl.AddImage(tex.Handle, p - new Vector2(radius), p + new Vector2(radius), Vector2.Zero, Vector2.One,
                        dead ? Palette.Rgba(1, 1, 1, 0.35f) : Palette.Rgba(1, 1, 1, 1));
        }
        else
        {
            dl.AddCircleFilled(p, radius, dead ? Palette.Rgba(0.4f, 0.4f, 0.4f, 0.7f) : Palette.With(role, 0.95f), 20);
        }

        // HP ring.
        var hp = a.Hp.At(t);
        if (a.MaxHp > 0 && hp >= 0 && !dead)
        {
            var frac = Math.Clamp((float)hp / a.MaxHp, 0, 1);
            var col = frac > 0.5f ? Palette.Rgba(0.3f, 1, 0.4f, 0.9f) : frac > 0.25f ? Palette.Rgba(1, 0.85f, 0.2f, 0.9f) : Palette.Rgba(1, 0.25f, 0.2f, 0.95f);
            dl.PathClear();
            dl.PathArcTo(p, radius + (2 * Scale), -MathF.PI / 2, (-MathF.PI / 2) + (2 * MathF.PI * frac), 24);
            dl.PathStroke(col, ImDrawFlags.None, 2.5f * Scale);
        }

        if (config.ShowNames)
        {
            var name = ShortName(a);
            var size = ImGui.CalcTextSize(name);
            QueueLabel(p + new Vector2(-size.X / 2, radius + (3 * Scale)), size, p, name, dead ? Palette.Rgba(1, 0.5f, 0.5f, 0.9f) : Palette.Text, 100, true);
        }
    }

    private void DrawEnemy(PullReplay r, Actor a, int t, Vector2 p, float heading)
    {
        var boss = a.Kind == ActorKind.Boss;
        var col = boss ? Palette.Rgba(1f, 0.45f, 0.2f, 0.9f) : Palette.Rgba(1f, 0.7f, 0.3f, 0.75f);
        if (a.Kind is ActorKind.Helper or ActorKind.Pet)
            col = Palette.Rgba(0.6f, 0.6f, 0.6f, 0.5f);
        var radius = Math.Max(6 * Scale, view.ToPixels(a.Radius > 0 ? a.Radius : 1));
        var targetable = a.IsTargetable(t);
        dl.AddCircle(p, radius, col, Segments(radius), targetable ? 2f : 1f);
        var dir = Angles.Dir(heading);
        dl.AddLine(p, p + (dir * radius), col, 2f);
        dl.AddCircleFilled(p, 3 * Scale, col, 8);

        if (a.Kind is ActorKind.Boss or ActorKind.Enemy)
        {
            var label = DisplayName(a);
            var hp = a.Hp.At(t);
            if (a.MaxHp > 0 && hp >= 0)
                label += $" {100f * hp / a.MaxHp:0.0}%";
            var size = ImGui.CalcTextSize(label);
            var lp = p + new Vector2(-size.X / 2, -radius - size.Y - (2 * Scale));
            QueueLabel(lp, size, p, label, Palette.Text, boss ? 80 : 50, false);

            if (config.ShowCastBars)
            {
                var cast = r.Casts.LastOrDefault(c => c.Source == a && c.StartMs <= t && t < c.EndMs);
                if (cast != null)
                {
                    var frac = Math.Clamp((float)(t - cast.StartMs) / Math.Max(1, cast.DurationMs), 0, 1);
                    var w = Math.Max(90 * Scale, size.X);
                    var bMin = p + new Vector2(-w / 2, radius + (4 * Scale));
                    var bMax = bMin + new Vector2(w, 5 * Scale);
                    dl.AddRectFilled(bMin, bMax, Palette.Rgba(0, 0, 0, 0.7f));
                    dl.AddRectFilled(bMin, new Vector2(bMin.X + (w * frac), bMax.Y), Palette.Rgba(1f, 0.6f, 0.2f, 0.95f));
                    var cs = ImGui.CalcTextSize(cast.Name);
                    QueueLabel(new Vector2(p.X - (cs.X / 2), bMax.Y + Scale), cs, p, cast.Name, Palette.Rgba(1f, 0.85f, 0.6f, 1), 60, false);
                }
            }
        }
    }

    private void DrawHeadMarkers(PullReplay r, int t)
    {
        foreach (var m in r.HeadMarkers)
        {
            if (m.T > t)
                break;
            if (t > m.T + m.DurationMs || !m.Target.Track.TrySample(t, out var pos, out _))
                continue;
            var p = view.ToScreen(pos) - new Vector2(0, 24 * Scale);
            var label = m.Label ?? $"{m.MarkerId:X4}";
            var def = r.Encounter?.HeadMarkers.GetValueOrDefault(m.MarkerId);
            var col = Palette.Parse(def?.Color, new Vector4(1, 0.9f, 0.3f, 1));
            float s;
            if (MarkerIcon(def?.Icon) is { } icon)
            {
                s = 11 * Scale;
                p -= new Vector2(0, 4 * Scale);
                dl.AddImage(icon, p - new Vector2(s), p + new Vector2(s));
            }
            else
            {
                s = 6 * Scale;
                dl.AddQuadFilled(p + new Vector2(0, -s), p + new Vector2(s, 0), p + new Vector2(0, s), p + new Vector2(-s, 0), Palette.With(col, 0.95f));
            }

            var size = ImGui.CalcTextSize(label);
            QueueLabel(p + new Vector2(-size.X / 2, -s - size.Y), size, p, label, Palette.With(col, 1), 70, false);
        }
    }

    /// <summary>Embedded resource of each head-marker icon (Resources/Markers), by its pack name.</summary>
    private static readonly Dictionary<string, string> MarkerIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stack"] = "RaidReplay.Markers.stack.png",
        ["stackGround"] = "RaidReplay.Markers.stack_ground.png",
        ["spread"] = "RaidReplay.Markers.spread.png",
        ["fireSpread"] = "RaidReplay.Markers.fire_spread.png",
        ["cone"] = "RaidReplay.Markers.cone.png",
    };

    /// <summary>The texture of a head-marker icon, or null when the marker has none.</summary>
    private static ImTextureID? MarkerIcon(string? name) =>
        name != null && MarkerIcons.TryGetValue(name, out var resource)
            ? Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, resource).GetWrapOrEmpty().Handle
            : null;

    private void DrawSigns(PullReplay r, int t)
    {
        var state = new Dictionary<int, Actor?>();
        foreach (var s in r.Signs)
        {
            if (s.T > t)
                break;
            if (s.Add)
                state[s.Marker] = s.Target;
            else
                state.Remove(s.Marker);
        }

        foreach (var (marker, target) in state)
        {
            if (target == null || !target.Track.TrySample(t, out var pos, out _))
                continue;
            var icon = gameData.SignIcon(marker);
            if (icon == 0)
                continue;
            var p = view.ToScreen(pos) + new Vector2(12 * Scale, -22 * Scale);
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
            var size = 16 * Scale;
            dl.AddImage(tex.Handle, p - new Vector2(size / 2), p + new Vector2(size / 2));
        }
    }

    private void DrawExpected(Incident inc, bool compact = false)
    {
        foreach (var s in inc.Snapshot)
        {
            if (s.Expected is not { } e)
                continue;
            if (compact && !s.Involved && s.MissDistance < 1.5f)
                continue;
            var col = Palette.ForExpected(s.ExpectedSource);
            var a = view.ToScreen(s.Pos);
            var b = view.ToScreen(e);
            var radius = Math.Max(7 * Scale, view.ToPixels(0.5f));
            dl.AddCircle(b, radius, Palette.With(col, 0.9f), 20, 2f);
            dl.AddCircleFilled(b, radius, Palette.With(col, 0.2f), 20);
            dl.AddLine(a, b, Palette.With(col, s.Involved ? 0.95f : 0.5f), s.Involved ? 2.5f : 1.5f);
            var d = Vector2.Normalize(b - a);
            if (float.IsFinite(d.X))
            {
                var perp = new Vector2(-d.Y, d.X);
                dl.AddTriangleFilled(b - (d * radius), b - (d * (radius + (8 * Scale))) + (perp * 5 * Scale),
                                     b - (d * (radius + (8 * Scale))) - (perp * 5 * Scale), Palette.With(col, 0.95f));
            }

            if (compact && !s.Involved)
                continue;
            var label = $"{ShortName(s.Player)} {s.MissDistance:0.0}y";
            QueueLabel(b + new Vector2(radius + (2 * Scale), -6 * Scale), ImGui.CalcTextSize(label), b, label, Palette.With(col, 1), 90, true);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private void Text(Vector2 p, string text, uint col)
    {
        dl.AddText(p + new Vector2(1, 1), Palette.TextShadow, text);
        dl.AddText(p, col, text);
    }

    public string DisplayName(Actor a) =>
        a.IsPlayer && config.AnonymizeNames ? Jobs.Abbrev(a.Job) : a.DisplayName;

    private string ShortName(Actor a)
    {
        if (config.AnonymizeNames)
            return Jobs.Abbrev(a.Job);
        var first = a.Name.Split(' ')[0];
        return first.Length > 10 ? first[..10] : first;
    }
}
