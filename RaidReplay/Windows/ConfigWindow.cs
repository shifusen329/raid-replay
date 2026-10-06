using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Services;

namespace RaidReplay.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly ReplayService service;
    private readonly byte[] logsBuffer = new byte[512];

    public ConfigWindow(Configuration config, ReplayService service) : base("Raid Replay Settings###RaidReplayConfig")
    {
        this.config = config;
        this.service = service;
        Size = new Vector2(620, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SetBuffer(config.LogsDirectory);
    }

    public void Dispose() { }

    private void SetBuffer(string s)
    {
        Array.Clear(logsBuffer);
        Encoding.UTF8.GetBytes(s, logsBuffer);
    }

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("##configtabs");
        if (!bar.Success)
            return;
        using (var tab = ImRaii.TabItem("Logs"))
        {
            if (tab.Success)
                DrawLogs();
        }

        using (var tab = ImRaii.TabItem("Live analysis"))
        {
            if (tab.Success)
                DrawLive();
        }

        using (var tab = ImRaii.TabItem("Encounters"))
        {
            if (tab.Success)
                DrawEncounters();
        }
    }

    private void DrawLogs()
    {
        ImGui.TextUnformatted("ACT network log folder");
        ImGui.SetNextItemWidth(-90);
        ImGui.InputText("##logs", logsBuffer);
        ImGui.SameLine();
        if (ImGui.Button("Apply"))
        {
            var text = Encoding.UTF8.GetString(logsBuffer).TrimEnd('\0').Trim();
            config.LogsDirectory = text.Length > 0 ? text : Configuration.DefaultLogsDirectory;
            SetBuffer(config.LogsDirectory);
            config.Save();
            service.ChangeLogsDirectory();
        }

        if (!Directory.Exists(config.LogsDirectory))
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Folder not found.");
        if (ImGui.SmallButton("Reset to default"))
            SetBuffer(Configuration.DefaultLogsDirectory);

        var instanced = config.InstancedOnly;
        if (ImGui.Checkbox("Only list instanced content (raids, trials, dungeons)", ref instanced))
        {
            config.InstancedOnly = instanced;
            config.Save();
            service.RequestRefresh();
        }

        var min = config.MinPullSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Minimum pull length (s)", ref min))
        {
            config.MinPullSeconds = Math.Clamp(min, 0, 600);
            config.Save();
        }

        ImGui.Separator();
        if (ImGui.Button("Rescan now"))
            service.RequestRefresh();
        ImGui.SameLine();
        if (ImGui.Button("Clear index cache"))
            service.ClearCache();

        using var table = ImRaii.Table("##files", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                                       ImGui.GetContentRegionAvail());
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Pulls", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableHeadersRow();
        foreach (var f in service.Library.Files)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(f.FileName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{f.Size / 1048576.0:N1} MB");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(f.Status == FileIndexStatus.Indexing ? $"{f.Progress:P0}" : f.Status.ToString());
            if (f.Error != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(f.Error);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(f.Index?.Pulls.Count.ToString() ?? "");
        }
    }

    private void DrawLive()
    {
        var changed = false;
        var b = config.LiveEnabled;
        if (ImGui.Checkbox("Watch the live log and analyze each pull when it ends", ref b)) { config.LiveEnabled = b; changed = true; }
        b = config.AutoOpenReport;
        if (ImGui.Checkbox("Open the wipe report automatically", ref b)) { config.AutoOpenReport = b; changed = true; }
        b = config.ChatSummary;
        if (ImGui.Checkbox("Print a one-line summary to chat", ref b)) { config.ChatSummary = b; changed = true; }
        b = config.AutoLoadLivePull;
        if (ImGui.Checkbox("Load the just-ended pull into the replay window", ref b)) { config.AutoLoadLivePull = b; changed = true; }
        b = config.LearnInBackground;
        if (ImGui.Checkbox("Learn typical positions from past pulls (for \"should have been\")", ref b)) { config.LearnInBackground = b; changed = true; }
        var poll = config.LivePollMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Poll interval in duty (ms)", ref poll, 250, 3000)) { config.LivePollMs = poll; changed = true; }
        var flush = config.FlushDelayMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Wait for ACT to flush (ms)", ref flush, 0, 5000)) { config.FlushDelayMs = flush; changed = true; }
        if (changed)
            config.Save();

        ImGui.Separator();
        ImGui.TextDisabled($"Watching: {service.LiveFile ?? "-"}");
        var growth = service.LastLogGrowthUtc;
        ImGui.TextDisabled(growth == default ? "No log growth seen yet." : $"Last log write {(DateTime.UtcNow - growth).TotalSeconds:0}s ago");
        ImGui.TextDisabled($"Position profiles: {service.LearnStatus}");
        if (service.LastError is { } err)
            ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), $"Last error: {err}");
    }

    private void DrawEncounters()
    {
        ImGui.TextWrapped("Encounter packs (JSON) describe phases, mechanics, AoE shapes and actor roles for specific fights. " +
                          "Built-in packs can be overridden by placing a pack with the same key in the folder below.");
        ImGui.TextDisabled(service.PacksDirectory);
        if (ImGui.Button("Open folder"))
        {
            Directory.CreateDirectory(service.PacksDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(service.PacksDirectory) { UseShellExecute = true });
        }

        ImGui.SameLine();
        if (ImGui.Button("Export built-in packs"))
            EncounterRegistry.ExportBuiltins(service.PacksDirectory);
        ImGui.SameLine();
        if (ImGui.Button("Reload packs"))
            service.ReloadPacks();

        ImGui.Separator();
        foreach (var e in service.Registry.Encounters)
        {
            ImGui.BulletText($"{e.Def.Name} [{e.Key}] — {e.Abilities.Count} abilities, {e.Def.Phases.Count} phases, {e.Def.Mechanics.Count} mechanics");
            ImGui.TextDisabled($"   {e.Source}");
        }

        foreach (var err in service.Registry.Errors)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"{Path.GetFileName(err.Source)}: {err.Message}");
        if (!service.Registry.Encounters.Any())
            ImGui.TextDisabled("No packs loaded.");
    }
}
