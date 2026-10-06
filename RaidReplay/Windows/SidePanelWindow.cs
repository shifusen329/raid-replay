using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>
/// The replay window's right-hand panel (report, deaths, events, party, info) popped out into its own window.
/// Closing it docks the panel back into the replay window.
/// </summary>
public sealed class SidePanelWindow : Window
{
    private readonly ReplayWindow owner;
    private readonly Configuration config;

    public SidePanelWindow(ReplayWindow owner, Configuration config)
        : base("Raid Replay · Analysis###RaidReplaySidePanel", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.owner = owner;
        this.config = config;
        Size = new Vector2(520, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(320, 300), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override bool DrawConditions() => config.SidePanelPoppedOut && owner.IsOpen;

    public override void PreDraw() => Theme.PushWindow();

    public override void PostDraw() => Theme.PopWindow();

    public override void Draw() => owner.DrawSidePanel(true);

    public override void OnClose()
    {
        if (!config.SidePanelPoppedOut)
            return;
        config.SidePanelPoppedOut = false;
        config.Save();
    }
}
