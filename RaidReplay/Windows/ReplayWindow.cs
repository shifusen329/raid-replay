using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;
using RaidReplay.Rendering;
using RaidReplay.Services;

namespace RaidReplay.Windows;

public sealed class ReplayWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ReplayService service;
    private readonly Configuration config;
    private readonly ArenaView view = new();
    private readonly ReplayRenderer renderer;
    private readonly ReportView reportView;
    private PullReplay? shown;
    private float timeMs;
    private bool playing;
    private bool fitPending = true;
    private string zoneFilter = "All";
    private DeathEvent? selectedDeath;
    private int eventFilter = 0b1111;
    private List<PullSummary> filtered = [];
    private List<string> zones = ["All"];
    private (int, bool, int, string) filterState;

    public ReplayWindow(Plugin plugin, ReplayService service, Configuration config)
        : base("Raid Replay###RaidReplayMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.service = service;
        this.config = config;
        renderer = new ReplayRenderer(config, service.GameData);
        reportView = new ReportView(config);
        Size = new Vector2(1400, 860);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(900, 560), MaximumSize = new Vector2(float.MaxValue) };
    }

    private static float Scale => ImGuiHelpers.GlobalScale;

    public void Dispose() { }

    public void Seek(int ms)
    {
        timeMs = ms;
        playing = false;
    }

    public void SelectIncident(Incident? inc)
    {
        reportView.Selected = inc;
        renderer.Highlight = inc;
        if (inc != null)
            Seek(inc.T);
    }

    public override void Draw()
    {
        var current = service.Current;
        if (!ReferenceEquals(current, shown))
        {
            shown = current;
            fitPending = true;
            selectedDeath = null;
            renderer.Highlight = null;
            reportView.Selected = null;
            timeMs = current?.FirstMs ?? 0;
        }

        if (service.PendingSeekMs is { } seek && current != null)
        {
            service.PendingSeekMs = null;
            reportView.Selected = service.CurrentReport?.RootCause;
            renderer.Highlight = reportView.Selected;
            Seek(Math.Max(current.FirstMs, seek - 1500));
        }

        DrawToolbar(current);
        var avail = ImGui.GetContentRegionAvail();
        var left = 340 * Scale;
        var right = 430 * Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (var child = ImRaii.Child("##pulls", new Vector2(left, avail.Y), true))
        {
            if (child.Success)
                DrawPullList();
        }

        ImGui.SameLine();
        using (var child = ImRaii.Child("##main", new Vector2(avail.X - left - right - (2 * spacing), avail.Y), false,
                                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
                DrawMain(current);
        }

        ImGui.SameLine();
        using (var child = ImRaii.Child("##side", new Vector2(right, avail.Y), true))
        {
            if (child.Success && current != null)
                DrawSide(current);
        }

        HandleKeys(current);
    }

    // ---- toolbar ---------------------------------------------------------------------------------------------

    private void DrawToolbar(PullReplay? current)
    {
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
        ImGui.SameLine();
        if (ImGui.Button("Rescan logs"))
            service.RequestRefresh();
        ImGui.SameLine();
        using (ImRaii.Disabled(service.LastLiveReport == null))
        {
            if (ImGui.Button("Last wipe report"))
                plugin.ShowLiveReport(service.LastLiveReport!);
        }

        ImGui.SameLine();
        var lib = service.Library;
        var indexing = lib.Files.FirstOrDefault(f => f.Status == FileIndexStatus.Indexing);
        var pending = lib.Files.Count(f => f.Status is FileIndexStatus.Pending or FileIndexStatus.Indexing);
        if (indexing != null)
            ImGui.TextDisabled($"Indexing {indexing.FileName} {indexing.Progress:P0} ({pending} files left)");
        else
            ImGui.TextDisabled($"{lib.Files.Count} logs · {lib.AllPulls.Count} pulls");

        ImGui.SameLine();
        var live = service.ActiveLivePull;
        if (!config.LiveEnabled)
            ImGui.TextDisabled("· live off");
        else if (live != null)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"· LIVE pull #{live.Ordinal} in progress");
        else if (service.LiveFile != null)
            ImGui.TextDisabled($"· watching {System.IO.Path.GetFileName(service.LiveFile)}");

        if (service.Loading is { } loading)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0.9f, 0.4f, 1), $"· loading #{loading.Ordinal}…");
        }

        if (service.LoadError is { } err)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"· {err}");
        }
    }

    // ---- pull list -------------------------------------------------------------------------------------------

    private void DrawPullList()
    {
        var lib = service.Library;
        var all = lib.AllPulls;
        var filterKey = (lib.Version, config.InstancedOnly, config.MinPullSeconds, zoneFilter);
        if (filterKey != filterState)
        {
            filterState = filterKey;
            var shown = all.Where(Visible).ToList();
            zones = ["All", ..shown.Select(p => p.ZoneName).Distinct().OrderBy(z => z)];
            filtered = shown.Where(p => zoneFilter == "All" || p.ZoneName == zoneFilter).ToList();
        }

        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##zone", zoneFilter))
        {
            if (combo.Success)
            {
                foreach (var z in zones)
                {
                    if (ImGui.Selectable(z, z == zoneFilter))
                    {
                        zoneFilter = z;
                    }
                }
            }
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.BordersInnerV;
        using var table = ImRaii.Table("##pulltable", 5, flags, ImGui.GetContentRegionAvail());
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("When");
        ImGui.TableSetupColumn("Dur");
        ImGui.TableSetupColumn("Result");
        ImGui.TableSetupColumn("Phase", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("†");
        ImGui.TableHeadersRow();

        // Manual clipping: rows above/below the viewport are replaced by one tall row.
        var rowH = ImGui.GetTextLineHeightWithSpacing();
        var first = Math.Max(0, (int)(ImGui.GetScrollY() / rowH) - 1);
        var visible = (int)(ImGui.GetContentRegionAvail().Y / rowH) + 3;
        var last = Math.Min(filtered.Count, first + visible);
        if (first > 0)
            ImGui.TableNextRow(ImGuiTableRowFlags.None, first * rowH);
        var currentKey = service.Current?.Summary.Key;
        DateTime? lastDay = null;
        for (var i = first; i < last; i++)
        {
            var p = filtered[i];
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
            ImGui.TableSetColumnIndex(0);
            var day = p.StartLocal.Date;
            var when = lastDay == day ? p.StartLocal.ToString("HH:mm") : p.StartLocal.ToString("MM-dd HH:mm");
            lastDay = day;
            if (ImGui.Selectable($"{when}##{p.Key}", p.Key == currentKey, ImGuiSelectableFlags.SpanAllColumns))
                service.Load(p);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"#{p.Ordinal} {p.ZoneName}\n{System.IO.Path.GetFileName(p.FilePath)}\n" +
                                 $"{string.Join(", ", p.Party.Select(m => Jobs.Abbrev(m.Job)))}" +
                                 (p.BossHpPct >= 0 ? $"\n{p.BossName} {p.BossHpPct:0.0}%" : "") +
                                 (p.StartTruncated ? "\n(log starts mid-pull)" : "") + (p.EndTruncated ? "\n(in progress / truncated)" : ""));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(Timeline.Fmt(p.DurationMs)[..^2]);
            ImGui.TableSetColumnIndex(2);
            var outcomeColor = p.Outcome switch
            {
                PullOutcome.Clear => new Vector4(0.4f, 1, 0.5f, 1),
                PullOutcome.Wipe => new Vector4(1, 0.5f, 0.45f, 1),
                _ => new Vector4(0.7f, 0.7f, 0.7f, 1),
            };
            ImGui.TextColored(outcomeColor, p.Outcome.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(p.FurthestPhase ?? (p.BossHpPct >= 0 ? $"{p.BossHpPct:0}%" : ""));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(p.Deaths > 0 ? p.Deaths.ToString() : "");
        }

        if (last < filtered.Count)
            ImGui.TableNextRow(ImGuiTableRowFlags.None, (filtered.Count - last) * rowH);
    }

    private bool Visible(PullSummary p) =>
        (!config.InstancedOnly || p.HasDirector) && p.DurationMs >= config.MinPullSeconds * 1000;

    // ---- main canvas -----------------------------------------------------------------------------------------

    private void DrawMain(PullReplay? r)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (r == null)
        {
            ImGui.TextWrapped("Select a pull on the left. Pulls are indexed from your ACT network logs in the background; " +
                              "while you raid, each wipe is analyzed as soon as it ends.");
            return;
        }

        var controlsH = ImGui.GetFrameHeightWithSpacing();
        var timelineH = (74 * Scale) + ImGui.GetStyle().ItemSpacing.Y;
        var canvas = new Vector2(avail.X, avail.Y - controlsH - timelineH);
        view.Begin("##canvas", canvas);
        if (fitPending && view.CanvasSize.X > 10)
        {
            var arena = r.Encounter?.Def.Arena;
            if (arena != null)
                view.Fit(new Vector2(arena.Center[0], arena.Center[1]), arena.ViewRadius > 0 ? arena.ViewRadius : arena.Radius * 1.25f);
            else
                FitToPlayers(r);
            fitPending = false;
        }

        if (playing)
        {
            timeMs += ImGui.GetIO().DeltaTime * 1000 * config.PlaybackSpeed;
            if (timeMs >= r.LastMs)
            {
                timeMs = r.LastMs;
                playing = false;
            }
        }

        var t = (int)timeMs;
        renderer.Draw(r, view, t);
        if (view.Hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            fitPending = true;

        DrawControls(r, t);
        var scrub = Timeline.Draw(r, service.CurrentReport, t, avail.X);
        if (scrub is { } s)
        {
            timeMs = s;
            playing = false;
        }
    }

    private void FitToPlayers(PullReplay r)
    {
        var pts = new List<Vector2>();
        foreach (var p in r.Party)
        {
            for (var t = 0; t < r.EndMs; t += 5000)
            {
                if (p.Track.TrySample(t, out var pos, out _))
                    pts.Add(pos);
            }
        }

        if (pts.Count == 0)
            return;
        var min = pts.Aggregate(Vector2.Min);
        var max = pts.Aggregate(Vector2.Max);
        view.Fit((min + max) / 2, Math.Max(10, Math.Max(max.X - min.X, max.Y - min.Y) / 2 * 1.2f));
    }

    private void DrawControls(PullReplay r, int t)
    {
        if (ImGui.Button(playing ? "Pause" : "Play", new Vector2(60 * Scale, 0)))
        {
            if (!playing && timeMs >= r.LastMs)
                timeMs = r.FirstMs;
            playing = !playing;
        }

        ImGui.SameLine();
        if (ImGui.Button("-5s"))
            Seek(Math.Max(r.FirstMs, t - 5000));
        ImGui.SameLine();
        if (ImGui.Button("-1s"))
            Seek(Math.Max(r.FirstMs, t - 1000));
        ImGui.SameLine();
        if (ImGui.Button("+1s"))
            Seek(Math.Min(r.LastMs, t + 1000));
        ImGui.SameLine();
        if (ImGui.Button("+5s"))
            Seek(Math.Min(r.LastMs, t + 5000));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * Scale);
        var speeds = new[] { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
        var idx = Math.Max(0, Array.IndexOf(speeds, config.PlaybackSpeed));
        using (var combo = ImRaii.Combo("##speed", $"{config.PlaybackSpeed:0.##}x"))
        {
            if (combo.Success)
            {
                foreach (var sp in speeds)
                {
                    if (ImGui.Selectable($"{sp:0.##}x", sp == speeds[idx]))
                    {
                        config.PlaybackSpeed = sp;
                        config.Save();
                    }
                }
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Timeline.Fmt(t)} / {Timeline.Fmt(r.EndMs)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"{r.PhaseAt(t)} {r.Phases.LastOrDefault(p => p.IsSegment && p.StartMs <= t && t < p.EndMs)?.Name}");
        ImGui.SameLine();
        var mech = r.Mechanics.LastOrDefault(m => m.T <= t && t - m.T < Math.Max(8000, m.DurationMs));
        if (mech != null)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1, 1), mech.Label);
        if (renderer.Highlight != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear overlay"))
                SelectIncident(null);
        }
    }

    private void HandleKeys(PullReplay? r)
    {
        if (r == null || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput)
            return;
        var step = ImGui.GetIO().KeyShift ? 5000 : 1000;
        if (ImGui.IsKeyPressed(ImGuiKey.Space))
            playing = !playing;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            Seek(Math.Max(r.FirstMs, (int)timeMs - step));
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            Seek(Math.Min(r.LastMs, (int)timeMs + step));
    }

    // ---- side tabs -------------------------------------------------------------------------------------------

    private void DrawSide(PullReplay r)
    {
        using var bar = ImRaii.TabBar("##sidetabs");
        if (!bar.Success)
            return;
        using (var tab = ImRaii.TabItem("Wipe report"))
        {
            if (tab.Success)
            {
                if (service.CurrentReport is { } report && ReferenceEquals(report.Pull, r))
                {
                    var clicked = reportView.Draw(report);
                    if (clicked != null)
                        SelectIncident(clicked);
                    else
                        renderer.Highlight = reportView.Selected;
                }
                else
                {
                    ImGui.TextDisabled("Analyzing…");
                }
            }
        }

        using (var tab = ImRaii.TabItem("Deaths"))
        {
            if (tab.Success)
                DrawDeaths(r);
        }

        using (var tab = ImRaii.TabItem("Events"))
        {
            if (tab.Success)
                DrawEvents(r);
        }

        using (var tab = ImRaii.TabItem("Party"))
        {
            if (tab.Success)
                DrawParty(r);
        }

        using (var tab = ImRaii.TabItem("Info"))
        {
            if (tab.Success)
                DrawInfo(r);
        }
    }

    private void DrawDeaths(PullReplay r)
    {
        var deaths = r.Deaths.Where(d => d.Victim.IsPlayer).ToList();
        if (deaths.Count == 0)
        {
            ImGui.TextDisabled("No deaths.");
            return;
        }

        using (var child = ImRaii.Child("##deathlist", new Vector2(0, 150 * Scale), true))
        {
            if (child.Success)
            {
                foreach (var d in deaths)
                {
                    var label = $"{Timeline.Fmt(d.T),7}  {renderer.DisplayName(d.Victim)}{(d.RaisedMs >= 0 ? " (raised)" : "")}##{d.GetHashCode()}";
                    if (ImGui.Selectable(label, d == selectedDeath))
                    {
                        selectedDeath = d;
                        Seek(Math.Max(r.FirstMs, d.T - 3000));
                    }
                }
            }
        }

        if (selectedDeath is not { } sel)
            return;
        ImGui.TextColored(new Vector4(1, 0.5f, 0.45f, 1), $"{renderer.DisplayName(sel.Victim)} — {sel.Cause}");
        ImGui.TextDisabled("Damage taken in the last 15s (HP before each hit):");
        using var table = ImRaii.Table("##recap", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                                       new Vector2(0, ImGui.GetContentRegionAvail().Y * 0.6f));
        if (table.Success)
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("Ability", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableHeadersRow();
            var rows = r.Actions.Where(a => a.T <= sel.T + 100 && a.T >= sel.T - 15000)
                        .SelectMany(a => a.Hits.Where(h => h.Target == sel.Victim && (h.Damage > 0 || h.Heal > 0))
                                              .Select(h => (h.T, Name: a.Name, Src: a.Source, Dmg: h.Damage, Heal: h.Heal, Hp: h.HpBefore, Max: h.MaxHp)))
                        .Concat(r.Ticks.Where(k => k.Target == sel.Victim && k.T <= sel.T + 100 && k.T >= sel.T - 15000)
                                 .Select(k => (k.T, Name: k.IsHeal ? "HoT" : "DoT", Src: k.Source!, Dmg: k.IsHeal ? 0 : k.Amount,
                                               Heal: k.IsHeal ? k.Amount : 0, Hp: sel.Victim.Hp.At(k.T - 1), Max: sel.Victim.MaxHp)))
                        .OrderBy(x => x.T).ToList();
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{(row.T - sel.T) / 1000f:0.0}s");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Src != null && !row.Src.IsPlayer ? row.Name : $"{row.Name} ({(row.Src != null ? renderer.DisplayName(row.Src) : "?")})");
                ImGui.TableSetColumnIndex(2);
                if (row.Dmg > 0)
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.45f, 1), $"-{row.Dmg:N0}");
                else
                    ImGui.TextColored(new Vector4(0.45f, 1, 0.5f, 1), $"+{row.Heal:N0}");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.Max > 0 ? $"{100f * row.Hp / row.Max:0}%" : "");
            }
        }

        ImGui.TextDisabled("Statuses at death:");
        foreach (var s in r.Statuses.Where(s => s.Target == sel.Victim && s.Active(sel.T - 50)).Take(16))
            ImGui.BulletText($"{s.Name}{(s.Source is { IsPlayer: true } src ? $" ({renderer.DisplayName(src)})" : "")}");
    }

    private void DrawEvents(PullReplay r)
    {
        var casts = (eventFilter & 1) != 0;
        var deaths = (eventFilter & 2) != 0;
        var markers = (eventFilter & 4) != 0;
        var mechs = (eventFilter & 8) != 0;
        if (ImGui.Checkbox("Casts", ref casts))
            eventFilter ^= 1;
        ImGui.SameLine();
        if (ImGui.Checkbox("Deaths", ref deaths))
            eventFilter ^= 2;
        ImGui.SameLine();
        if (ImGui.Checkbox("Markers", ref markers))
            eventFilter ^= 4;
        ImGui.SameLine();
        if (ImGui.Checkbox("Mechanics", ref mechs))
            eventFilter ^= 8;

        var events = new List<(int T, string Text, Vector4 Color)>();
        if (casts)
        {
            foreach (var c in r.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Enemy))
                events.Add((c.StartMs, $"{c.Source.DisplayName} casts {c.Name} ({c.DurationMs / 1000f:0.0}s)", new Vector4(1, 0.7f, 0.4f, 1)));
        }

        if (deaths)
        {
            foreach (var d in r.Deaths.Where(d => d.Victim.IsPlayer))
                events.Add((d.T, $"{renderer.DisplayName(d.Victim)} died — {d.Cause}", new Vector4(1, 0.45f, 0.45f, 1)));
        }

        if (markers)
        {
            foreach (var m in r.HeadMarkers)
                events.Add((m.T, $"Marker {m.Label ?? $"{m.MarkerId:X4}"} on {renderer.DisplayName(m.Target)}", new Vector4(1, 0.9f, 0.4f, 1)));
            foreach (var te in r.Tethers)
                events.Add((te.StartMs, $"Tether {te.Label ?? $"{te.TetherId:X4}"}: {renderer.DisplayName(te.Source)} → {renderer.DisplayName(te.Target)}",
                            new Vector4(0.8f, 0.6f, 1, 1)));
        }

        if (mechs)
        {
            foreach (var m in r.Mechanics)
                events.Add((m.T, $"== {m.Label}", new Vector4(0.4f, 0.9f, 1, 1)));
        }

        events.Sort((a, b) => a.T.CompareTo(b.T));
        using var child = ImRaii.Child("##events", Vector2.Zero, true);
        if (!child.Success)
            return;
        var t = (int)timeMs;
        foreach (var e in events)
        {
            var past = e.T <= t;
            using (ImRaii.PushColor(ImGuiCol.Text, past ? e.Color : e.Color * new Vector4(1, 1, 1, 0.55f)))
            {
                if (ImGui.Selectable($"{Timeline.Fmt(e.T),7}  {e.Text}##{e.T}{e.Text.GetHashCode()}"))
                    Seek(e.T);
            }
        }
    }

    private void DrawParty(PullReplay r)
    {
        var t = (int)timeMs;
        foreach (var p in r.Party)
        {
            var hp = p.Hp.At(t);
            var dead = r.Deaths.Any(d => d.Victim == p && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t));
            ImGui.TextColored(Palette.ForJob(p.Job), Jobs.Abbrev(p.Job));
            ImGui.SameLine();
            ImGui.TextUnformatted(renderer.DisplayName(p));
            ImGui.SameLine();
            ImGui.ProgressBar(dead ? 0 : p.MaxHp > 0 && hp >= 0 ? (float)hp / p.MaxHp : 0, new Vector2(-1, 0),
                              dead ? "dead" : p.MaxHp > 0 ? $"{100f * hp / p.MaxHp:0}%" : "");
            var debuffs = r.Statuses.Where(s => s.Target == p && s.Active(t) && s.Source is not { IsPlayer: true }).Select(s => s.Name).Distinct();
            var text = string.Join(", ", debuffs);
            if (text.Length > 0)
                ImGui.TextDisabled("   " + text);
        }
    }

    private void DrawInfo(PullReplay r)
    {
        var s = r.Summary;
        ImGui.TextUnformatted($"{s.ZoneName} · pull #{s.Ordinal} · {s.Outcome}");
        ImGui.TextDisabled($"{System.IO.Path.GetFileName(s.FilePath)} @ {s.StartOffset:N0}");
        ImGui.TextDisabled($"Encounter pack: {r.Encounter?.Def.Name ?? "none (generic)"}");
        ImGui.TextDisabled($"Actors {r.Actors.Count} · casts {r.Casts.Count} · actions {r.Actions.Count} · AoEs {r.Aoes.Count}");
        ImGui.Separator();
        ImGui.TextUnformatted("Display");
        var changed = false;
        var v = config.ShowMapTexture;
        if (ImGui.Checkbox("Map texture", ref v)) { config.ShowMapTexture = v; changed = true; }
        v = config.ShowJobIcons;
        if (ImGui.Checkbox("Job icons", ref v)) { config.ShowJobIcons = v; changed = true; }
        v = config.ShowNames;
        if (ImGui.Checkbox("Names", ref v)) { config.ShowNames = v; changed = true; }
        v = config.AnonymizeNames;
        if (ImGui.Checkbox("Anonymize names", ref v)) { config.AnonymizeNames = v; changed = true; }
        v = config.ShowTrails;
        if (ImGui.Checkbox("Movement trails", ref v)) { config.ShowTrails = v; changed = true; }
        v = config.ShowFakeAoes;
        if (ImGui.Checkbox("Fake AoEs", ref v)) { config.ShowFakeAoes = v; changed = true; }
        v = config.ShowInferredTelegraphs;
        if (ImGui.Checkbox("Inferred telegraphs", ref v)) { config.ShowInferredTelegraphs = v; changed = true; }
        v = config.ShowHelpers;
        if (ImGui.Checkbox("Helpers / hidden actors", ref v)) { config.ShowHelpers = v; changed = true; }
        v = config.ShowPets;
        if (ImGui.Checkbox("Pets", ref v)) { config.ShowPets = v; changed = true; }
        var op = config.AoeOpacity;
        if (ImGui.SliderFloat("AoE opacity", ref op, 0.05f, 0.8f)) { config.AoeOpacity = op; changed = true; }
        if (changed)
            config.Save();
        ImGui.Separator();
        if (renderer.HoveredActor is { } a)
            ImGui.TextDisabled($"Hover: {a} kind={a.Kind} base={a.BNpcBaseId} r={a.Radius:0.0}");
        if (renderer.HoveredAoe is { } aoe)
            ImGui.TextDisabled($"Hover AoE: {aoe.Label} 0x{aoe.ActionId:X} {aoe.Shape} [{aoe.ShapeSource}]");
        foreach (var d in r.Diagnostics)
            ImGui.TextDisabled(d);
    }
}
