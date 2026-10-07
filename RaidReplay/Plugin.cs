using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RaidReplay.Core.Analysis;
using RaidReplay.GameData;
using RaidReplay.Rendering;
using RaidReplay.Services;
using RaidReplay.Windows;

namespace RaidReplay;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/raidreplay";
    private const string CommandAlias = "/rreplay";
    private const string ShortCommand = "/rr";
    private const string AarCommand = "/aar";

    public readonly WindowSystem WindowSystem = new("RaidReplay");

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Service = new ReplayService(Configuration, new LuminaGameData(DataManager.Excel),
                                    PluginInterface.GetPluginConfigDirectory());
        Theme.Init(PluginInterface.UiBuilder);

        ConfigWindow = new ConfigWindow(Configuration, Service);
        ReplayWindow = new ReplayWindow(this, Service, Configuration);
        WipeReportWindow = new WipeReportWindow(this, Service, Configuration);
        SidePanelWindow = new SidePanelWindow(ReplayWindow, Configuration) { IsOpen = Configuration.SidePanelPoppedOut };
        ReplayWindow.Popout = SidePanelWindow;
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(ReplayWindow);
        WindowSystem.AddWindow(WipeReportWindow);
        WindowSystem.AddWindow(SidePanelWindow);

        var help = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the raid replay window. '/raidreplay report' (or /aar) shows the last after-action report, '/raidreplay config' the settings.",
        };
        CommandManager.AddHandler(CommandName, help);
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand) { HelpMessage = "Alias of /raidreplay.", ShowInHelp = false });
        if (!CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Short for /raidreplay: opens the replay window ('/rr report', '/rr config' work too)." }))
            Log.Warning($"{ShortCommand} is already taken by another plugin; use {CommandName} instead");
        CommandManager.AddHandler(AarCommand, new CommandInfo((_, _) => ToggleReport()) { HelpMessage = "Open the after-action report of the last wipe (again to close)." });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
        DutyState.DutyWiped += OnDutyEvent;
        DutyState.DutyCompleted += OnDutyEvent;

        Service.LiveReportReady += OnLiveReport;
        Service.Start();
    }

    public Configuration Configuration { get; }
    public ReplayService Service { get; }
    private ConfigWindow ConfigWindow { get; }
    private ReplayWindow ReplayWindow { get; }
    private WipeReportWindow WipeReportWindow { get; }
    private SidePanelWindow SidePanelWindow { get; }

    public void Dispose()
    {
        Service.LiveReportReady -= OnLiveReport;
        DutyState.DutyWiped -= OnDutyEvent;
        DutyState.DutyCompleted -= OnDutyEvent;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        ReplayWindow.Dispose();
        WipeReportWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        CommandManager.RemoveHandler(ShortCommand);
        CommandManager.RemoveHandler(AarCommand);
        Service.Dispose();
        Theme.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework) =>
        Service.InDuty = Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56] ||
                         Condition[ConditionFlag.BoundByDuty95] || Condition[ConditionFlag.InCombat];

    private void OnDutyEvent(Dalamud.Game.DutyState.IDutyStateEventArgs args) => Service.NudgeLive();

    /// <summary>A live report waiting to be shown; picked up by the next UI frame.</summary>
    private WipeReport? pendingReport;

    private void OnLiveReport(WipeReport report)
    {
        // Raised on the analysis thread. Format every string of the wipe card here, so the UI only lays it out, then
        // hand the report to the very next UI frame (no framework-thread hop).
        try
        {
            WipeCard.For(report, Configuration.AnonymizeNames);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Pre-formatting the wipe card failed; it will be built on first draw");
        }

        if (Configuration.AutoOpenReport)
            System.Threading.Volatile.Write(ref pendingReport, report);
        if (Configuration.ChatSummary)
            Framework.RunOnFrameworkThread(() => ChatGui.Print(report.ChatLine.Replace("[Raid Replay] ", string.Empty), "RaidReplay"));
    }

    private void DrawUi()
    {
        if (System.Threading.Interlocked.Exchange(ref pendingReport, null) is { } report)
            WipeReportWindow.Show(report);
        WindowSystem.Draw();
    }

    public void ShowLiveReport(WipeReport report) => WipeReportWindow.Show(report);

    public void OpenReplayAt(WipeReport report, Incident? incident)
    {
        Service.ShowReport(report);
        ReplayWindow.ShowIncident(report, incident);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "report":
                ToggleReport();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => ReplayWindow.Toggle();

    /// <summary>Opens the after-action report on top of everything, or closes it if it is already open.</summary>
    public void ToggleReport()
    {
        WipeReportWindow.IsOpen = !WipeReportWindow.IsOpen;
        if (WipeReportWindow.IsOpen)
            WipeReportWindow.BringToFront();
    }
}
