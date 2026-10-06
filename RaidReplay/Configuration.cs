using System;
using System.IO;
using Dalamud.Configuration;

namespace RaidReplay;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Sources
    public string LogsDirectory { get; set; } = DefaultLogsDirectory;
    public bool InstancedOnly { get; set; } = true;
    public int MinPullSeconds { get; set; } = 5;

    // Live analysis
    public bool LiveEnabled { get; set; } = true;
    public bool AutoOpenReport { get; set; } = true;
    public bool ChatSummary { get; set; } = true;
    public bool AutoLoadLivePull { get; set; } = true;
    public int LivePollMs { get; set; } = 1000;
    public int FlushDelayMs { get; set; } = 1500;
    public bool LearnInBackground { get; set; } = true;

    // Display
    public bool ShowMapTexture { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool AnonymizeNames { get; set; }
    public bool ShowJobIcons { get; set; } = true;
    public bool ShowHelpers { get; set; }
    public bool ShowPets { get; set; }
    public bool ShowInferredTelegraphs { get; set; } = true;
    public bool ShowFakeAoes { get; set; } = true;
    public bool ShowHeadMarkers { get; set; } = true;
    public bool ShowTethers { get; set; } = true;
    public bool ShowCastBars { get; set; } = true;
    public bool ShowHitLines { get; set; } = true;
    public bool ShowTrails { get; set; } = true;
    public float TrailSeconds { get; set; } = 3f;
    public float AoeOpacity { get; set; } = 0.35f;
    public float PlaybackSpeed { get; set; } = 1f;

    public static string DefaultLogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
