using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RaidReplay.Core.Analysis;
using RaidReplay.GameData;
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

    public readonly WindowSystem WindowSystem = new("RaidReplay");

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Service = new ReplayService(Configuration, new LuminaGameData(DataManager.Excel),
                                    PluginInterface.GetPluginConfigDirectory());

        ConfigWindow = new ConfigWindow(Configuration, Service);
        ReplayWindow = new ReplayWindow(this, Service, Configuration);
        WipeReportWindow = new WipeReportWindow(this, Service, Configuration);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(ReplayWindow);
        WindowSystem.AddWindow(WipeReportWindow);

        var help = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the raid replay window. '/raidreplay report' shows the last wipe report, '/raidreplay config' the settings.",
        };
        CommandManager.AddHandler(CommandName, help);
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand) { HelpMessage = "Alias of /raidreplay.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
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

    public void Dispose()
    {
        Service.LiveReportReady -= OnLiveReport;
        DutyState.DutyWiped -= OnDutyEvent;
        DutyState.DutyCompleted -= OnDutyEvent;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        ReplayWindow.Dispose();
        WipeReportWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        Service.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework) =>
        Service.InDuty = Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56] ||
                         Condition[ConditionFlag.BoundByDuty95] || Condition[ConditionFlag.InCombat];

    private void OnDutyEvent(Dalamud.Game.DutyState.IDutyStateEventArgs args) => Service.NudgeLive();

    private void OnLiveReport(WipeReport report)
    {
        // Raised on a background thread; touch UI/chat on the framework thread.
        Framework.RunOnFrameworkThread(() =>
        {
            if (Configuration.ChatSummary)
                ChatGui.Print(report.ChatLine.Replace("[Raid Replay] ", string.Empty), "RaidReplay");
            if (Configuration.AutoOpenReport)
                WipeReportWindow.Show(report);
        });
    }

    public void ShowLiveReport(WipeReport report) => WipeReportWindow.Show(report);

    public void OpenReplayAt(WipeReport report, Incident? incident)
    {
        Service.ShowReport(report);
        ReplayWindow.IsOpen = true;
        ReplayWindow.SelectIncident(incident ?? report.RootCause);
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
                WipeReportWindow.IsOpen = !WipeReportWindow.IsOpen;
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => ReplayWindow.Toggle();
}
