using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RaidReplay.Core.Analysis;
using RaidReplay.Rendering;
using RaidReplay.Services;

namespace RaidReplay.Windows;

/// <summary>
/// The between-pulls wipe card: opens by itself when a pull ends and is meant to be read in a few seconds —
/// verdict, root cause with culprits and a snapshot mini-map, contributing incidents, mitigation, actions.
/// </summary>
public sealed class WipeReportWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ReportView reportView;
    private WipeReport? report;

    public WipeReportWindow(Plugin plugin, ReplayService service, Configuration config)
        : base("Wipe Report###RaidReplayWipeReport", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        reportView = new ReportView(config, service.GameData);
        Size = new Vector2(620, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void Dispose() { }

    public void Show(WipeReport r)
    {
        report = r;
        reportView.Selected = r.RootCause;
        WindowName = $"Wipe Report · pull #{r.Pull.Summary.Ordinal}###RaidReplayWipeReport";
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw() => Theme.PushWindow();

    public override void PostDraw() => Theme.PopWindow();

    public override void Draw()
    {
        // Escape closes the report when it has the focus, whatever Dalamud's own close-hotkey setting is. (While the game
        // has the focus, Plugin closes it from the game's key state.)
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            IsOpen = false;
            return;
        }

        if (report == null)
        {
            Theme.Wrapped("No wipe analyzed yet. The report opens here by itself when a pull ends.", Theme.TextDim);
            return;
        }

        var footer = ImGui.GetFrameHeight() + (ImGui.GetStyle().ItemSpacing.Y * 2) + 1;
        using (var child = ImRaii.Child("##wipecard", new Vector2(0, -footer), false, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (child.Success)
                reportView.Draw(report, true);
        }

        Theme.Rule();
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.With(Theme.Accent, 0.32f))
                     .Push(ImGuiCol.ButtonHovered, Theme.With(Theme.Accent, 0.48f))
                     .Push(ImGuiCol.ButtonActive, Theme.With(Theme.Accent, 0.62f)))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Open replay at this moment"))
                plugin.OpenReplayAt(report, reportView.Selected);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Load this pull in the replay window, seek to the selected incident and overlay where everyone should have been.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Theme.Dim("Click an incident to show it on the map.");
    }
}
