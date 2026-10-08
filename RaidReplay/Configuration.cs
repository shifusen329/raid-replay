using System;
using System.IO;
using Dalamud.Configuration;
using RaidReplay.GameData;

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

    /// <summary>Kinds of duty after whose pulls the report opens by itself.</summary>
    public DutyKind AutoOpenDuties { get; set; } = DutyKinds.Default;
    public bool ChatSummary { get; set; } = true;
    public bool AutoLoadLivePull { get; set; } = true;
    public int LivePollMs { get; set; } = 1000;
    public int FlushDelayMs { get; set; } = 1500;
    public bool LearnInBackground { get; set; } = true;

    /// <summary>Random id sent with feedback reports so one install's reports can be grouped; created on the first report.</summary>
    public Guid FeedbackInstallId { get; set; }

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

    /// <summary>Fill AoE shapes (off = outlines only, so the map stays readable).</summary>
    public bool FillAoes { get; set; } = true;

    /// <summary>Draw raidwides and AoEs that cover most of the arena as outlines only.</summary>
    public bool OutlineRoomWideAoes { get; set; } = true;

    /// <summary>Canvas labels: 0 = all, 1 = declutter (merge duplicates, nudge or drop overlaps), 2 = hover only.</summary>
    public int LabelMode { get; set; } = 1;

    public bool ShowLegend { get; set; }

    // Replay window layout (unscaled px)
    public float PullListWidth { get; set; } = 380;
    public float SidePanelWidth { get; set; } = 440;
    public bool PullListCollapsed { get; set; }

    /// <summary>Pull list grouped by entry into a duty (expand an entry to see its pulls); off = one flat list.</summary>
    public bool GroupPullsByEntry { get; set; } = true;

    public bool SidePanelCollapsed { get; set; }
    public bool SidePanelPoppedOut { get; set; }

    // Playback
    public bool LoopPlayback { get; set; }
    public bool FollowPlayhead { get; set; } = true;

    /// <summary>Default ACT log folder. Stored unexpanded so the setting is portable; environment variables are expanded on use.</summary>
    public const string DefaultLogsDirectory = @"%USERPROFILE%\AppData\Roaming\Advanced Combat Tracker\FFXIVLogs";

    /// <summary>The log folder with environment variables (e.g. %USERPROFILE%) expanded.</summary>
    public string ResolvedLogsDirectory() => Environment.ExpandEnvironmentVariables(LogsDirectory);

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
