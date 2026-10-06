using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RaidReplay.Core.Analysis;
using RaidReplay.Rendering;
using RaidReplay.Services;

namespace RaidReplay.Windows;

/// <summary>Compact between-pulls report: verdict, incidents and a snapshot map of the selected incident.</summary>
public sealed class WipeReportWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ReplayService service;
    private readonly ReportView reportView;
    private readonly ReplayRenderer renderer;
    private readonly ArenaView view = new();
    private WipeReport? report;
    private bool fit = true;

    public WipeReportWindow(Plugin plugin, ReplayService service, Configuration config)
        : base("Wipe Report###RaidReplayWipeReport", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.service = service;
        reportView = new ReportView(config);
        renderer = new ReplayRenderer(config, service.GameData);
        Size = new Vector2(980, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 420), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void Dispose() { }

    public void Show(WipeReport r)
    {
        report = r;
        reportView.Selected = r.RootCause;
        fit = true;
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        if (report == null)
        {
            ImGui.TextDisabled("No wipe analyzed yet. Reports appear here automatically when a pull ends.");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail();
        var mapSize = Math.Min(avail.Y, avail.X * 0.45f);
        using (var left = ImRaii.Child("##reportleft", new Vector2(avail.X - mapSize - ImGui.GetStyle().ItemSpacing.X, avail.Y), false))
        {
            if (left.Success)
            {
                if (ImGui.Button("Open in replay"))
                    plugin.OpenReplayAt(report, reportView.Selected);
                ImGui.SameLine();
                ImGui.TextDisabled("Click an incident to see where everyone was.");
                reportView.Draw(report);
            }
        }

        ImGui.SameLine();
        using var right = ImRaii.Child("##reportmap", new Vector2(mapSize, avail.Y), true,
                                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!right.Success)
            return;
        var r = report.Pull;
        var inc = reportView.Selected;
        view.Begin("##snapcanvas", ImGui.GetContentRegionAvail() - new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
        if (fit)
        {
            var arena = r.Encounter?.Def.Arena;
            view.Fit(arena != null ? new Vector2(arena.Center[0], arena.Center[1]) : new Vector2(100, 100),
                     arena != null ? (arena.ViewRadius > 0 ? arena.ViewRadius : arena.Radius * 1.25f) : 25);
            fit = false;
        }

        renderer.Highlight = inc;
        renderer.Draw(r, view, inc?.T ?? r.EndMs);
        ImGui.TextDisabled(inc != null ? $"Snapshot at {Timeline.Fmt(inc.T)} — ghosts show where players should have been"
                                       : "Snapshot at wipe");
        _ = scale;
    }
}
