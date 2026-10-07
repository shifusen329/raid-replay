using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

/// <summary>
/// The replay window: a resizable/collapsible pull list, the arena canvas with its View menu and legend, transport
/// controls and the zoomable timeline, and the analysis panel (wipe card, deaths, events, party, info), which can be
/// collapsed or popped out into its own window.
/// </summary>
public sealed class ReplayWindow : Window, IDisposable
{
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f, 4f, 8f];
    private static readonly string[] SpeedLabels = ["0.1x", "0.25x", "0.5x", "1x", "1.5x", "2x", "4x", "8x"];

    private static readonly string[] PullColumns = ["#", "When", "Dur", "Result", "Phase", "Boss", "Deaths", "Zone"];

    private static readonly string[] PullTips =
    [
        "Pull number within its log file.",
        "Start time (local). The full date is in the row tooltip.",
        "Pull duration (m:ss).",
        "Wipe, Clear, Zone out (left the instance), Ended (combat ended without a wipe/clear), Live (in progress).",
        "Furthest phase reached (from the encounter pack). Sorting orders by progress.",
        "Boss HP % when the pull ended (lower = further).",
        "Player deaths during the pull.",
        "Zone / duty.",
    ];

    private static readonly string[] RecapNames = ["Time", "Ability", "Amount", "HP"];

    private static readonly string[] RecapTips =
    [
        "Seconds before the death.", "Ability (and who used it, for player sources).", "Damage (red) or healing (green).",
        "HP before the hit.",
    ];

    private static readonly string[] DpsNames = ["Player", "DPS", "Share", "Damage"];

    private static readonly string[] DpsTips =
    [
        "Party member (dimmed while dead).", "Damage per second over the chosen window.", "Share of the party's damage in the window.",
        "Total damage in the window.",
    ];

    private static readonly ConditionalWeakTable<PullSummary, PullRowText> RowTexts = new();

    private readonly Plugin plugin;
    private readonly ReplayService service;
    private readonly Configuration config;
    private readonly ArenaView view = new();
    private readonly ReplayRenderer renderer;
    private readonly ReportView reportView;
    private readonly Timeline timeline = new();
    private PullReplay? shown;
    private float timeMs;
    private bool playing;
    private bool fitPending = true;
    private string zoneFilter = "All";
    private DeathEvent? selectedDeath;
    private int eventFilter = 0b1111;

    /// <summary>DPS tab window: 0 = since the pull started, 1 = since the phase started, 2 = the last 15 seconds.</summary>
    private int dpsWindow;
    private List<PullSummary> filtered = [];
    private List<string> zones = ["All"];
    private (int, bool, int, string) filterState;
    private string pullCountText = string.Empty;
    private bool sortDirty = true;
    private uint sortColumn = 1;
    private bool sortAscending;
    private float controlsHeight;
    private bool rowStarted;
    private bool overlayCleared;
    private (WipeReport Report, Incident? Incident)? pendingOpen;

    // Per-pull caches for the side tabs.
    private List<DeathEvent> deaths = [];
    private List<string> deathLabels = [];
    private DeathEvent? recapFor;
    private List<RecapRow> recap = [];
    private List<string> recapStatuses = [];
    private List<(int T, string Text, Vector4 Color)> events = [];
    private (PullReplay?, int, bool) eventsKey;

    public ReplayWindow(Plugin plugin, ReplayService service, Configuration config)
        : base("Raid Replay###RaidReplayMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.service = service;
        this.config = config;
        renderer = new ReplayRenderer(config, service.GameData);
        reportView = new ReportView(config, service.GameData);
        Size = new Vector2(1500, 900);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 520), MaximumSize = new Vector2(float.MaxValue) };
    }

    /// <summary>The popped-out analysis panel (set by the plugin).</summary>
    public SidePanelWindow? Popout { get; set; }

    private static float Scale => ImGuiHelpers.GlobalScale;

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindow();

    public override void PostDraw() => Theme.PopWindow();

    public void Seek(int ms)
    {
        timeMs = ms;
        playing = false;
    }

    public void SelectIncident(Incident? inc)
    {
        reportView.Selected = inc;
        renderer.Highlight = inc;
        overlayCleared = inc == null;
        if (inc != null)
            Seek(inc.T);
    }

    /// <summary>Shows a report's pull at an incident (applied after the pull switch, so the reset does not undo it).</summary>
    public void ShowIncident(WipeReport report, Incident? inc)
    {
        pendingOpen = (report, inc ?? report.RootCause);
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        var current = SyncCurrent();
        DrawToolbar();

        var avail = ImGui.GetContentRegionAvail();
        var s = Scale;
        var splitW = 8 * s;
        var railW = 16 * s;
        var showLeft = !config.PullListCollapsed;
        var docked = !config.SidePanelPoppedOut;
        var showRight = docked && !config.SidePanelCollapsed;
        var leftW = showLeft ? Math.Clamp(config.PullListWidth * s, 220 * s, Math.Max(220 * s, avail.X * 0.45f)) : 0;
        var rightW = showRight ? Math.Clamp(config.SidePanelWidth * s, 300 * s, Math.Max(300 * s, avail.X * 0.6f)) : 0;
        var leftTotal = showLeft ? leftW + splitW : railW;
        var rightTotal = showRight ? rightW + splitW : docked ? railW : 0;

        // Keep a usable canvas: shrink the analysis panel first, then the pull list.
        var minCanvas = 360 * s;
        var over = leftTotal + rightTotal + minCanvas - avail.X;
        if (over > 0 && showRight)
        {
            var d = Math.Clamp(over, 0, rightW - (300 * s));
            rightW -= d;
            rightTotal -= d;
            over -= d;
        }

        if (over > 0 && showLeft)
        {
            var d = Math.Clamp(over, 0, leftW - (220 * s));
            leftW -= d;
            leftTotal -= d;
        }

        var mainW = Math.Max(100 * s, avail.X - leftTotal - rightTotal);

        // Pull list | splitter.
        if (showLeft)
        {
            using (var child = Panel("##pulls", new Vector2(leftW, avail.Y)))
            {
                if (child.Success)
                    DrawPullList();
            }

            ImGui.SameLine(0, 0);
            var before = leftW;
            var dragged = Splitter("##splitleft", avail.Y, ref leftW, 1, "Drag to resize the pull list · double-click to collapse it", out var toggle);
            if (leftW != before)
                config.PullListWidth = leftW / s;
            if (dragged)
                config.Save();
            if (toggle)
                TogglePullList();
        }
        else if (Rail("##railleft", avail.Y, FontAwesomeIcon.ChevronRight, "Show the pull list"))
        {
            TogglePullList();
        }

        ImGui.SameLine(0, 0);
        using (var child = ImRaii.Child("##main", new Vector2(mainW, avail.Y), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
                DrawMain(current);
        }

        // Splitter | analysis panel.
        if (showRight)
        {
            ImGui.SameLine(0, 0);
            var before = rightW;
            var dragged = Splitter("##splitright", avail.Y, ref rightW, -1, "Drag to resize the analysis panel · double-click to collapse it", out var toggle);
            if (rightW != before)
                config.SidePanelWidth = rightW / s;
            if (dragged)
                config.Save();
            if (toggle)
                ToggleSidePanel();
            ImGui.SameLine(0, 0);
            using var child = Panel("##side", new Vector2(rightW, avail.Y));
            if (child.Success)
                DrawSidePanel(false);
        }
        else if (docked)
        {
            ImGui.SameLine(0, 0);
            if (Rail("##railright", avail.Y, FontAwesomeIcon.ChevronLeft, "Show the analysis panel"))
                ToggleSidePanel();
        }

        HandleKeys(current);
    }

    /// <summary>Picks up pull changes and pending seeks from the service.</summary>
    private PullReplay? SyncCurrent()
    {
        var current = service.Current;
        if (!ReferenceEquals(current, shown))
        {
            shown = current;
            fitPending = true;
            selectedDeath = null;
            renderer.Highlight = null;
            reportView.Selected = null;
            overlayCleared = false;
            timeMs = current?.FirstMs ?? 0;
            playing = false;
            deaths = current?.Deaths.Where(d => d.Victim.IsPlayer).ToList() ?? [];
            deathLabels = [];
            recapFor = null;
        }

        if (service.PendingSeekMs is { } seek && current != null)
        {
            service.PendingSeekMs = null;
            reportView.Selected = service.CurrentReport?.RootCause;
            renderer.Highlight = reportView.Selected;
            overlayCleared = false;
            Seek(Math.Max(current.FirstMs, seek - 1500));
        }

        if (pendingOpen is { } open && current != null && ReferenceEquals(open.Report.Pull, current))
        {
            pendingOpen = null;
            SelectIncident(open.Incident);
        }
        else if (pendingOpen is { } stale && current != null && !ReferenceEquals(stale.Report.Pull, current))
        {
            pendingOpen = null;
        }

        return current;
    }

    // ---- layout helpers --------------------------------------------------------------------------------------

    /// <summary>A bordered child with the panel background (the colour is popped right after Begin).</summary>
    private static ImRaii.ChildDisposable Panel(string id, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.PanelBg);
        var child = ImRaii.Child(id, size, true);
        ImGui.PopStyleColor();
        return child;
    }

    /// <summary>A vertical drag handle. Returns true when a drag ends (to save the width).</summary>
    private static bool Splitter(string id, float height, ref float width, int dir, string tip, out bool toggle)
    {
        var s = Scale;
        var w = 8 * s;
        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(w, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (active)
            width += ImGui.GetIO().MouseDelta.X * dir;
        toggle = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (hovered && !active)
            ImGui.SetTooltip(tip);
        var dl = ImGui.GetWindowDrawList();
        var cx = p.X + (w / 2);
        var col = Theme.U32(active ? Theme.Accent : hovered ? Theme.With(Theme.Accent, 0.6f) : new Vector4(1, 1, 1, 0.08f));
        dl.AddLine(new Vector2(cx, p.Y + (8 * s)), new Vector2(cx, p.Y + height - (8 * s)), col, active || hovered ? 2 * s : 1);
        for (var i = -1; i <= 1; i++)
            dl.AddCircleFilled(new Vector2(cx, p.Y + (height / 2) + (i * 6 * s)), 1.6f * s, col, 8);
        return ImGui.IsItemDeactivated();
    }

    /// <summary>A slim clickable strip standing in for a collapsed panel.</summary>
    private static bool Rail(string id, float height, FontAwesomeIcon icon, string tip)
    {
        var s = Scale;
        var w = 16 * s;
        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(w, height));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetTooltip(tip);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p + new Vector2(2 * s, 0), p + new Vector2(w - (2 * s), height), Theme.U32(hovered ? Theme.FrameBgHover : Theme.PanelBg), 6 * s);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(p.X + ((w - gs.X) / 2), p.Y + (height / 2) - (gs.Y / 2)), Theme.U32(hovered ? Theme.Accent : Theme.TextDim), glyph);
        }

        return clicked;
    }

    private void TogglePullList()
    {
        config.PullListCollapsed = !config.PullListCollapsed;
        config.Save();
    }

    private void ToggleSidePanel()
    {
        if (config.SidePanelPoppedOut)
        {
            Popout?.BringToFront();
            return;
        }

        config.SidePanelCollapsed = !config.SidePanelCollapsed;
        config.Save();
    }

    private void TogglePopout()
    {
        config.SidePanelPoppedOut = !config.SidePanelPoppedOut;
        if (config.SidePanelPoppedOut)
            config.SidePanelCollapsed = false;
        config.Save();
        if (Popout != null)
        {
            Popout.IsOpen = config.SidePanelPoppedOut;
            if (config.SidePanelPoppedOut)
                Popout.BringToFront();
        }
    }

    // ---- toolbar ---------------------------------------------------------------------------------------------

    private void DrawToolbar()
    {
        if (Theme.IconButton("##tglpulls", FontAwesomeIcon.Bars, config.PullListCollapsed ? "Show the pull list" : "Hide the pull list",
                             !config.PullListCollapsed))
            TogglePullList();
        ImGui.SameLine();
        if (Theme.IconButton("##settings", FontAwesomeIcon.Cog, "Settings (log folder, live analysis, display, encounter packs)"))
            plugin.ToggleConfigUi();
        ImGui.SameLine();
        if (Theme.IconButton("##rescan", FontAwesomeIcon.Sync, "Rescan the log folder for new pulls"))
            service.RequestRefresh();
        ImGui.SameLine();
        if (Theme.IconButton("##lastwipe", FontAwesomeIcon.FileAlt,
                             service.LastLiveReport != null ? "Open the last live wipe report" : "No live wipe report yet",
                             enabled: service.LastLiveReport != null))
            plugin.ShowLiveReport(service.LastLiveReport!);

        ImGui.SameLine(0, 12 * Scale);
        ImGui.AlignTextToFramePadding();
        var lib = service.Library;
        var indexing = lib.Files.FirstOrDefault(f => f.Status == FileIndexStatus.Indexing);
        if (indexing != null)
        {
            var pending = lib.Files.Count(f => f.Status is FileIndexStatus.Pending or FileIndexStatus.Indexing);
            Theme.Dim($"Indexing {indexing.FileName} {indexing.Progress:P0} ({pending} files left)");
        }
        else
        {
            Theme.Dim($"{lib.Files.Count} logs · {lib.AllPulls.Count} pulls");
        }

        ImGui.SameLine();
        var live = service.ActiveLivePull;
        if (!config.LiveEnabled)
        {
            Theme.Dim("· live off");
        }
        else if (live != null)
        {
            ImGui.TextColored(Theme.SevCritical, $"· LIVE pull #{live.Ordinal}");
        }
        else if (service.LiveFile != null)
        {
            Theme.Dim("· watching live log");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(service.LiveFile);
        }

        if (service.Loading is { } loading)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.SevMajor, $"· loading #{loading.Ordinal}…");
        }

        if (service.LoadError is { } err)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.SevCritical, "· load failed");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(err);
        }

        // Right-aligned: analysis panel toggles.
        var bw = ImGui.GetFrameHeight() * 1.15f;
        var right = ImGui.GetWindowContentRegionMax().X - (2 * bw) - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine();
        if (ImGui.GetCursorPosX() < right)
            ImGui.SetCursorPosX(right);
        if (Theme.IconButton("##tglside", FontAwesomeIcon.Columns,
                             config.SidePanelPoppedOut ? "The analysis panel is popped out (bring it to front)" :
                             config.SidePanelCollapsed ? "Show the analysis panel" : "Hide the analysis panel",
                             !config.SidePanelCollapsed && !config.SidePanelPoppedOut))
            ToggleSidePanel();

        ImGui.SameLine();
        if (Theme.IconButton("##popout", config.SidePanelPoppedOut ? FontAwesomeIcon.WindowRestore : FontAwesomeIcon.ExternalLinkAlt,
                             config.SidePanelPoppedOut ? "Dock the analysis panel back into this window" : "Pop the analysis panel out into its own window",
                             config.SidePanelPoppedOut))
            TogglePopout();
    }

    // ---- pull list -------------------------------------------------------------------------------------------

    private void DrawPullList()
    {
        var lib = service.Library;
        var filterKey = (lib.Version, config.InstancedOnly, config.MinPullSeconds, zoneFilter);
        if (filterKey != filterState)
        {
            filterState = filterKey;
            var visible = lib.AllPulls.Where(Visible).ToList();
            zones = ["All", .. visible.Select(p => p.ZoneName).Distinct().OrderBy(z => z)];
            filtered = visible.Where(p => zoneFilter == "All" || p.ZoneName == zoneFilter).ToList();
            pullCountText = $"{filtered.Count} pull{(filtered.Count == 1 ? "" : "s")}";
            sortDirty = true;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Zone");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##zone", zoneFilter == "All" ? "All zones" : zoneFilter))
        {
            if (combo.Success)
            {
                foreach (var z in zones)
                {
                    if (ImGui.Selectable(z == "All" ? "All zones" : z, z == zoneFilter))
                        zoneFilter = z;
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Show pulls from one zone only.\nPulls shorter than {config.MinPullSeconds}s" +
                             (config.InstancedOnly ? " and outside instanced content" : "") + " are hidden (Settings › Logs).");
        }

        Theme.Dim(pullCountText);
        Theme.Help("Click a pull to load it. Hover a row for its summary.\n" +
                   "Click a column header to sort, drag a header edge to resize, drag a header to reorder, " +
                   "right-click a header to show or hide columns (# and Zone are hidden by default).");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable |
                                      ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable;
        using var table = ImRaii.Table("##pulls2", 8, flags, ImGui.GetContentRegionAvail());
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide | ImGuiTableColumnFlags.PreferSortDescending, 0, 0);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.DefaultSort |
                                       ImGuiTableColumnFlags.PreferSortDescending, 0, 1);
        ImGui.TableSetupColumn("Dur", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 0, 2);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 0, 3);
        ImGui.TableSetupColumn("Phase", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 1, 4);
        ImGui.TableSetupColumn("Boss", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortAscending, 0, 5);
        ImGui.TableSetupColumn("Deaths", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 0, 6);
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 1, 7);
        Theme.TableHeaders(PullColumns, PullTips);

        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && (specs.SpecsDirty || sortDirty))
        {
            if (specs.SpecsCount > 0)
            {
                sortColumn = specs.Specs.ColumnUserID;
                sortAscending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
            }

            SortPulls();
            specs.SpecsDirty = false;
            sortDirty = false;
        }

        // Manual clipping: rows above/below the viewport are replaced by one tall row.
        var rowH = ImGui.GetTextLineHeightWithSpacing();
        var first = Math.Max(0, (int)(ImGui.GetScrollY() / rowH) - 1);
        var count = (int)(ImGui.GetContentRegionAvail().Y / rowH) + 3;
        var last = Math.Min(filtered.Count, first + count);
        if (first > 0)
            ImGui.TableNextRow(ImGuiTableRowFlags.None, first * rowH);
        var cur = service.Current?.Summary;
        for (var i = first; i < last; i++)
        {
            var p = filtered[i];
            var txt = RowText(p);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
            using var id = ImRaii.PushId(i);
            if (ImGui.TableSetColumnIndex(0))
                Theme.Dim(txt.Ordinal);

            ImGui.TableSetColumnIndex(1);
            var x = ImGui.GetCursorPosX();
            var selected = cur != null && cur.StartOffset == p.StartOffset && cur.FilePath == p.FilePath;
            if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                service.Load(p);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(txt.Tooltip);
            ImGui.SameLine();
            ImGui.SetCursorPosX(x);
            ImGui.TextColored(Theme.TextFaint, txt.Date);
            ImGui.SameLine(0, 4 * Scale);
            ImGui.TextUnformatted(txt.Time);

            if (ImGui.TableSetColumnIndex(2))
                ImGui.TextUnformatted(txt.Duration);
            if (ImGui.TableSetColumnIndex(3))
                ImGui.TextColored(txt.ResultColor, txt.Result);
            if (ImGui.TableSetColumnIndex(4))
                ImGui.TextUnformatted(txt.Phase);
            if (ImGui.TableSetColumnIndex(5))
                ImGui.TextColored(p.BossHpPct >= 0 ? Theme.Text : Theme.TextFaint, txt.Boss);
            if (ImGui.TableSetColumnIndex(6))
                ImGui.TextColored(p.Deaths > 0 ? Theme.Text : Theme.TextFaint, txt.Deaths);
            if (ImGui.TableSetColumnIndex(7))
                Theme.Dim(p.ZoneName);
        }

        if (last < filtered.Count)
            ImGui.TableNextRow(ImGuiTableRowFlags.None, (filtered.Count - last) * rowH);
    }

    private PullRowText RowText(PullSummary p)
    {
        if (RowTexts.TryGetValue(p, out var txt) && txt.IsCurrent(p, config.AnonymizeNames))
            return txt;
        txt = new PullRowText(p, config.AnonymizeNames, service.Registry);
        RowTexts.AddOrUpdate(p, txt);
        return txt;
    }

    private void SortPulls()
    {
        var col = sortColumn;
        var asc = sortAscending;
        filtered.Sort((a, b) =>
        {
            var c = col switch
            {
                0 => a.Ordinal.CompareTo(b.Ordinal),
                2 => a.DurationMs.CompareTo(b.DurationMs),
                3 => a.Outcome.CompareTo(b.Outcome),
                4 => a.Phases.Count != b.Phases.Count ? a.Phases.Count.CompareTo(b.Phases.Count) : b.BossHpPct.CompareTo(a.BossHpPct),
                5 => (a.BossHpPct < 0 ? 101 : a.BossHpPct).CompareTo(b.BossHpPct < 0 ? 101 : b.BossHpPct),
                6 => a.Deaths.CompareTo(b.Deaths),
                7 => string.Compare(a.ZoneName, b.ZoneName, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            if (c == 0)
                c = a.StartTicks.CompareTo(b.StartTicks);
            return asc ? c : -c;
        });
    }

    private bool Visible(PullSummary p) =>
        (!config.InstancedOnly || p.HasDirector) && p.DurationMs >= config.MinPullSeconds * 1000;

    // ---- main canvas -----------------------------------------------------------------------------------------

    private void DrawMain(PullReplay? r)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (r == null)
        {
            ImGui.Dummy(new Vector2(0, avail.Y * 0.3f));
            using (Theme.TitleFont())
                ImGuiHelpers.CenteredText("No pull loaded");
            Theme.Wrapped("Select a pull on the left. Pulls are indexed from your ACT network logs in the background; " +
                          "while you raid, each wipe is analyzed as soon as it ends and the wipe card opens by itself.", Theme.TextDim);
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var controlsH = controlsHeight > 0 ? controlsHeight : (ImGui.GetFrameHeightWithSpacing() * 2);
        var canvasH = Math.Max(120 * Scale, avail.Y - controlsH - Timeline.Height - spacing);
        view.Begin("##canvas", new Vector2(avail.X, canvasH));
        if (fitPending && view.CanvasSize.X > 10)
        {
            FitView(r);
            fitPending = false;
        }

        timeline.Follow = config.FollowPlayhead && !config.LoopPlayback;
        if (playing)
        {
            timeMs += ImGui.GetIO().DeltaTime * 1000 * config.PlaybackSpeed;
            var loopStart = timeline.Zoomed ? timeline.ViewStart : r.FirstMs;
            var loopEnd = timeline.Zoomed ? Math.Min(timeline.ViewEnd, r.LastMs) : r.LastMs;
            if (config.LoopPlayback && timeMs >= loopEnd)
            {
                timeMs = loopStart;
            }
            else if (timeMs >= r.LastMs)
            {
                timeMs = r.LastMs;
                playing = false;
            }
        }

        var t = (int)timeMs;
        renderer.Draw(r, view, t);
        if (view.Hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            fitPending = true;
        DrawCanvasOverlay();

        var y0 = ImGui.GetCursorPosY();
        DrawControls(r, t);
        controlsHeight = ImGui.GetCursorPosY() - y0;

        var report = service.CurrentReport is { } rep && ReferenceEquals(rep.Pull, r) ? rep : null;
        var input = timeline.Draw(r, report, t, avail.X, renderer.Highlight);
        if (input.Seek is { } seek)
        {
            timeMs = seek;
            playing = false;
        }

        if (input.Incident != null)
            SelectIncident(input.Incident);
        if (input.Death != null)
            JumpToDeath(r, input.Death);
    }

    private void FitView(PullReplay r)
    {
        var arena = r.Encounter?.Def.Arena;
        if (arena != null)
        {
            view.Fit(new Vector2(arena.Center[0], arena.Center[1]), arena.ViewRadius > 0 ? arena.ViewRadius : arena.Radius * 1.25f);
            return;
        }

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

    /// <summary>Floating button bar in the canvas's top-right corner: View menu, legend, fit, help.</summary>
    private void DrawCanvasOverlay()
    {
        var s = Scale;
        var saved = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        var bw = h * 1.15f;
        var gap = 4 * s;
        var pad = 4 * s;
        const int n = 4;
        var size = new Vector2((n * bw) + ((n - 1) * gap) + (2 * pad), h + (2 * pad));
        ImGui.SetCursorScreenPos(new Vector2(view.CanvasMax.X - size.X - (8 * s), view.CanvasMin.Y + (8 * s)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.04f, 0.05f, 0.07f, 0.78f));
        using (var bar = ImRaii.Child("##canvasbar", size, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.PopStyleColor();
            if (bar.Success)
            {
                if (Theme.IconButton("##view", FontAwesomeIcon.Eye, "View: labels, players, enemies, mechanics, AoE fills, map…"))
                    ImGui.OpenPopup("##viewmenu");
                ImGui.SameLine();
                if (Theme.IconButton("##legend", FontAwesomeIcon.ListUl, config.ShowLegend ? "Hide the legend" : "Show a legend of markers and AoE colours",
                                     config.ShowLegend))
                {
                    config.ShowLegend = !config.ShowLegend;
                    config.Save();
                }

                ImGui.SameLine();
                if (Theme.IconButton("##fit", FontAwesomeIcon.ExpandArrowsAlt, "Fit the arena (or double-click the canvas)"))
                    fitPending = true;
                ImGui.SameLine();
                Theme.IconButton("##canvashelp", FontAwesomeIcon.QuestionCircle,
                                 "Canvas\n" +
                                 "  Mouse wheel: zoom at the cursor\n" +
                                 "  Drag (any button): pan\n" +
                                 "  Double-click: fit the arena\n" +
                                 "  Hover a player, enemy or AoE for details\n\n" +
                                 "Rings with arrows show where players should have been for the selected incident.\n" +
                                 "Labels can be decluttered or shown on hover only (View menu).");

                // The popup is a separate window: give it the normal theme metrics, not the bar's tight ones.
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Theme.WindowPadding * s).Push(ImGuiStyleVar.ItemSpacing, Theme.ItemSpacing * s))
                {
                    using var popup = ImRaii.Popup("##viewmenu");
                    if (popup.Success)
                        ConfigWindow.DrawDisplayOptions(config);
                }
            }
        }

        ImGui.PopStyleVar(2);
        ImGui.SetCursorScreenPos(saved);
    }

    // ---- transport controls ----------------------------------------------------------------------------------

    /// <summary>Starts a control group, wrapping to a new line if it doesn't fit.</summary>
    private void Group(float width)
    {
        if (!rowStarted)
        {
            rowStarted = true;
            return;
        }

        ImGui.SameLine(0, 10 * Scale);
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine();
    }

    private void DrawControls(PullReplay r, int t)
    {
        var s = Scale;
        var h = ImGui.GetFrameHeight();
        var bw = h * 1.15f;
        var tight = 2 * s;
        rowStarted = false;

        // Transport.
        Group((9 * bw) + (8 * tight));
        if (Theme.IconButton("##home", FontAwesomeIcon.FastBackward, "Jump to the pull start (Home)"))
            Seek(r.FirstMs);
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##b5", FontAwesomeIcon.Backward, "Back 5 s (Shift+←)"))
            Seek(Math.Max(r.FirstMs, t - 5000));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##b1", FontAwesomeIcon.AngleDoubleLeft, "Back 1 s (←)"))
            Seek(Math.Max(r.FirstMs, t - 1000));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##bstep", FontAwesomeIcon.AngleLeft, "Step back 0.1 s (Ctrl+←)"))
            Seek(Math.Max(r.FirstMs, t - 100));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##play", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, playing ? "Pause (Space)" : "Play (Space)", playing))
            TogglePlay(r);
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##fstep", FontAwesomeIcon.AngleRight, "Step forward 0.1 s (Ctrl+→)"))
            Seek(Math.Min(r.LastMs, t + 100));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##f1", FontAwesomeIcon.AngleDoubleRight, "Forward 1 s (→)"))
            Seek(Math.Min(r.LastMs, t + 1000));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##f5", FontAwesomeIcon.Forward, "Forward 5 s (Shift+→)"))
            Seek(Math.Min(r.LastMs, t + 5000));
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##end", FontAwesomeIcon.FastForward, "Jump to the pull end (End)"))
            Seek(r.LastMs);

        // Incident / death navigation.
        var navW = (2 * bw) + h + (2 * tight);
        Group(navW);
        if (Theme.IconButton("##pinc", FontAwesomeIcon.ChevronLeft, "Previous incident (,)"))
            StepIncident(r, -1);
        ImGui.SameLine(0, tight);
        ImGui.AlignTextToFramePadding();
        Theme.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.SevMajor);
        Theme.Tip("Incidents from the wipe report (the triangles on the timeline)");
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##ninc", FontAwesomeIcon.ChevronRight, "Next incident (.)"))
            StepIncident(r, 1);

        Group(navW);
        if (Theme.IconButton("##pdeath", FontAwesomeIcon.ChevronLeft, "Previous death (Shift+,)"))
            StepDeath(r, -1);
        ImGui.SameLine(0, tight);
        ImGui.AlignTextToFramePadding();
        Theme.Icon(FontAwesomeIcon.Skull, Theme.SevCritical);
        Theme.Tip("Player deaths (the red × on the timeline)");
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##ndeath", FontAwesomeIcon.ChevronRight, "Next death (Shift+.)"))
            StepDeath(r, 1);

        // Speed, loop, follow.
        var speedW = 70 * s;
        Group(speedW + (2 * bw) + (2 * tight));
        ImGui.SetNextItemWidth(speedW);
        var idx = SpeedIndex();
        using (var combo = ImRaii.Combo("##speed", SpeedLabels[idx]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < Speeds.Length; i++)
                {
                    if (ImGui.Selectable(SpeedLabels[i], i == idx))
                        SetSpeed(i);
                }
            }
        }

        Theme.Tip("Playback speed ([ slower, ] faster)");
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##loop", FontAwesomeIcon.Redo,
                             config.LoopPlayback ? "Loop is on (L): playback repeats the visible timeline range; zoom the timeline in to loop one mechanic"
                                                 : "Loop is off (L): playback stops at the end", config.LoopPlayback))
        {
            config.LoopPlayback = !config.LoopPlayback;
            config.Save();
        }

        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##follow", FontAwesomeIcon.Crosshairs,
                             config.FollowPlayhead ? "Follow is on (F): a zoomed-in timeline scrolls with the playhead" : "Follow is off (F)",
                             config.FollowPlayhead))
        {
            config.FollowPlayhead = !config.FollowPlayhead;
            config.Save();
        }

        // Timeline zoom + help.
        Group((4 * bw) + (3 * tight));
        if (Theme.IconButton("##zout", FontAwesomeIcon.SearchMinus, "Zoom the timeline out (−, or mouse wheel on the timeline)"))
            timeline.ZoomAroundPlayhead(1 / 1.5f, t);
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##zin", FontAwesomeIcon.SearchPlus, "Zoom the timeline in around the playhead (=)"))
            timeline.ZoomAroundPlayhead(1.5f, t);
        ImGui.SameLine(0, tight);
        if (Theme.IconButton("##zfit", FontAwesomeIcon.ArrowsAltH, "Show the whole pull on the timeline (0, or double-click the ruler)", enabled: timeline.Zoomed))
            timeline.Fit();
        ImGui.SameLine(0, tight);
        ImGuiHelpIcon();

        // Status line: always its own row, so it never pushes buttons off.
        if (renderer.Highlight != null)
        {
            if (Theme.IconButton("##clearov", FontAwesomeIcon.Eraser, "Clear the \"should have been\" overlay of the selected incident"))
                SelectIncident(null);
            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Timeline.Fmt(t)} / {Timeline.Fmt(r.EndMs)}");
        var phase = r.PhaseAt(t);
        PhaseSpan? segment = null;
        foreach (var p in r.Phases)
        {
            if (p.IsSegment && p.StartMs <= t && t < p.EndMs)
                segment = p;
        }

        if (phase.Length > 0)
        {
            ImGui.SameLine();
            Theme.Dim(phase);
        }

        if (segment != null)
        {
            ImGui.SameLine(0, 4 * s);
            Theme.Dim("›");
            ImGui.SameLine(0, 4 * s);
            Theme.Dim(segment.Name);
        }

        MechanicMarker? mech = null;
        foreach (var m in r.Mechanics)
        {
            if (m.T <= t && t - m.T < Math.Max(8000, m.DurationMs))
                mech = m;
        }

        if (mech != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Timeline.MechanicColor, mech.Label);
        }
    }

    /// <summary>The "(?)" button: hovering shows the keyboard shortcuts and the timeline legend.</summary>
    private static void ImGuiHelpIcon()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Accent))
            Dalamud.Interface.Components.ImGuiComponents.IconButton("##help", FontAwesomeIcon.QuestionCircle,
                                                                   new Vector2(ImGui.GetFrameHeight() * 1.15f, ImGui.GetFrameHeight()));
        if (!ImGui.IsItemHovered())
            return;
        using var tip = ImRaii.Tooltip();
        Theme.SectionTitle("Keyboard (replay window focused)");
        using (var table = ImRaii.Table("##keys", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                Key("Space", "Play / pause");
                Key("← / →", "Back / forward 1 s");
                Key("Shift + ← / →", "Back / forward 5 s");
                Key("Ctrl + ← / →", "Step 0.1 s");
                Key("Home / End", "Pull start / end");
                Key(", / .", "Previous / next incident");
                Key("Shift + , / .", "Previous / next death");
                Key("[ / ]", "Slower / faster");
                Key("L", "Loop the visible timeline range");
                Key("F", "Follow the playhead when zoomed");
                Key("= / −", "Zoom the timeline in / out");
                Key("0", "Fit the whole pull on the timeline");
            }
        }

        ImGui.Spacing();
        Theme.SectionTitle("Timeline mouse");
        ImGui.TextUnformatted("Click / drag: scrub · click a marker: jump to it\nWheel: zoom at the cursor · Shift+wheel or right-drag: pan\n" +
                             "Double-click the ruler: fit · drag the bar under the lanes: pan");
        ImGui.Spacing();
        Theme.SectionTitle("Timeline legend");
        Timeline.DrawLegend();
    }

    private static void Key(string key, string what)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(Theme.Accent, key);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(what);
    }

    private int SpeedIndex()
    {
        var best = 0;
        for (var i = 1; i < Speeds.Length; i++)
        {
            if (Math.Abs(Speeds[i] - config.PlaybackSpeed) < Math.Abs(Speeds[best] - config.PlaybackSpeed))
                best = i;
        }

        return best;
    }

    private void SetSpeed(int i)
    {
        config.PlaybackSpeed = Speeds[Math.Clamp(i, 0, Speeds.Length - 1)];
        config.Save();
    }

    private void TogglePlay(PullReplay r)
    {
        if (!playing && timeMs >= r.LastMs)
            timeMs = r.FirstMs;
        playing = !playing;
    }

    private void StepIncident(PullReplay r, int dir)
    {
        if (service.CurrentReport is not { } rep || !ReferenceEquals(rep.Pull, r) || rep.Incidents.Count == 0)
            return;
        var list = rep.Incidents;
        var t = (int)timeMs;
        var cur = reportView.Selected;
        var ci = cur != null ? list.IndexOf(cur) : -1;
        int idx;
        if (ci >= 0 && Math.Abs(cur!.T - t) <= 50)
            idx = ci + dir;
        else if (dir > 0)
            idx = list.FindIndex(i => i.T > t + 50);
        else
            idx = list.FindLastIndex(i => i.T < t - 50);
        if (idx >= 0 && idx < list.Count)
            SelectIncident(list[idx]);
    }

    private void StepDeath(PullReplay r, int dir)
    {
        if (deaths.Count == 0)
            return;
        var t = (int)timeMs;
        var ci = selectedDeath != null ? deaths.IndexOf(selectedDeath) : -1;
        int idx;
        if (ci >= 0 && Math.Abs(selectedDeath!.T - t) <= 50)
            idx = ci + dir;
        else if (dir > 0)
            idx = deaths.FindIndex(d => d.T > t + 50);
        else
            idx = deaths.FindLastIndex(d => d.T < t - 50);
        if (idx >= 0 && idx < deaths.Count)
            JumpToDeath(r, deaths[idx]);
    }

    private void JumpToDeath(PullReplay r, DeathEvent d)
    {
        selectedDeath = d;
        var inc = service.CurrentReport is { } rep && ReferenceEquals(rep.Pull, r) ? rep.Incidents.FirstOrDefault(i => i.Death == d) : null;
        if (inc != null)
            SelectIncident(inc);
        Seek(d.T);
    }

    private void HandleKeys(PullReplay? r)
    {
        if (r == null || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput)
            return;
        HandleKeysCore(r);
    }

    private void HandleKeysCore(PullReplay r)
    {
        var io = ImGui.GetIO();
        var step = io.KeyCtrl ? 100 : io.KeyShift ? 5000 : 1000;
        var t = (int)timeMs;
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
            TogglePlay(r);
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            Seek(Math.Max(r.FirstMs, t - step));
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            Seek(Math.Min(r.LastMs, t + step));
        if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
            Seek(r.FirstMs);
        if (ImGui.IsKeyPressed(ImGuiKey.End, false))
            Seek(r.LastMs);
        if (ImGui.IsKeyPressed(ImGuiKey.Comma))
        {
            if (io.KeyShift)
                StepDeath(r, -1);
            else
                StepIncident(r, -1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Period))
        {
            if (io.KeyShift)
                StepDeath(r, 1);
            else
                StepIncident(r, 1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket, false))
            SetSpeed(SpeedIndex() - 1);
        if (ImGui.IsKeyPressed(ImGuiKey.RightBracket, false))
            SetSpeed(SpeedIndex() + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.L, false))
        {
            config.LoopPlayback = !config.LoopPlayback;
            config.Save();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false))
        {
            config.FollowPlayhead = !config.FollowPlayhead;
            config.Save();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Equal) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd))
            timeline.ZoomAroundPlayhead(1.5f, t);
        if (ImGui.IsKeyPressed(ImGuiKey.Minus) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract))
            timeline.ZoomAroundPlayhead(1 / 1.5f, t);
        if (ImGui.IsKeyPressed(ImGuiKey.Key0, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0, false))
            timeline.Fit();
    }

    // ---- analysis panel --------------------------------------------------------------------------------------

    /// <summary>Draws the analysis tabs (docked, or from the popped-out window).</summary>
    public void DrawSidePanel(bool poppedOut)
    {
        var r = shown;
        if (poppedOut)
        {
            r = SyncCurrent();
            if (r != null && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput)
                HandleKeysCore(r);
        }

        if (r == null)
        {
            Theme.Wrapped("No pull loaded.", Theme.TextDim);
            return;
        }

        using var bar = ImRaii.TabBar("##sidetabs");
        if (!bar.Success)
            return;
        using (var tab = ImRaii.TabItem("Report"))
        {
            if (tab.Success)
                DrawReportTab(r);
        }

        using (var tab = ImRaii.TabItem("DPS"))
        {
            if (tab.Success)
                DrawDps(r);
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

    private void DrawReportTab(PullReplay r)
    {
        if (service.CurrentReport is not { } report || !ReferenceEquals(report.Pull, r))
        {
            Theme.Dim("Analyzing…");
            return;
        }

        using var child = ImRaii.Child("##reportscroll", Vector2.Zero, false);
        if (!child.Success)
            return;
        var clicked = reportView.Draw(report, false);
        if (clicked != null)
            SelectIncident(clicked);
        else if (!overlayCleared)
            renderer.Highlight = reportView.Selected;
    }

    private void DrawDps(PullReplay r)
    {
        const int RollingMs = 15000;
        var t = (int)timeMs;
        ImGui.RadioButton("Pull", ref dpsWindow, 0);
        Theme.Tip("Since the pull started (encounter DPS, as ACT shows it).");
        ImGui.SameLine();
        ImGui.RadioButton("Phase", ref dpsWindow, 1);
        Theme.Tip("Since the current phase started: what a DPS check like P3's Meteor measures.");
        ImGui.SameLine();
        ImGui.RadioButton("Last 15 s", ref dpsWindow, 2);
        Theme.Tip("The 15 seconds before the playhead: burst windows and dips.");

        var phase = r.Phases.LastOrDefault(p => !p.IsSegment && p.StartMs <= t);
        var (from, start, what) = dpsWindow switch
        {
            1 when phase != null => (phase.StartMs, phase.StartMs, $"since {phase.Name} started"),
            2 => (t - RollingMs, Math.Max(0, t - RollingMs), "in the last 15 s"),
            _ => (int.MinValue, 0, "since the pull started"),
        };
        var seconds = (t - start) / 1000f;
        if (seconds < 1)
        {
            Theme.Dim("Move the playhead past the start of the window.");
            return;
        }

        var meter = DamageMeter.For(r);
        var rows = r.Party.Select(p => (Player: p, Damage: meter.Damage(p, from, t))).OrderByDescending(x => x.Damage).ToList();
        var party = rows.Sum(x => x.Damage);
        var top = Math.Max(1, rows.Count > 0 ? rows[0].Damage : 1);
        using (Theme.TitleFont())
            Theme.Wrapped($"Party {party / seconds:N0} DPS", Theme.Text);
        Theme.Dim($"{ShortNumber(party)} damage {what} ({Timeline.Fmt((int)(seconds * 1000))[..^2]}).");

        using (var table = ImRaii.Table("##dps", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthStretch, 1.6f);
                ImGui.TableSetupColumn("Share", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn("Damage", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                Theme.TableHeaders(DpsNames, DpsTips);
                var icon = ImGui.GetTextLineHeight();
                foreach (var (p, damage) in rows)
                {
                    var dead = r.Deaths.Any(d => d.Victim == p && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t));
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ReportView.JobIcon(p.Job, icon);
                    ImGui.SameLine(0, 4 * Scale);
                    ImGui.TextColored(dead ? Theme.TextDim : Theme.Text, renderer.DisplayName(p));
                    ImGui.TableSetColumnIndex(1);
                    using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Theme.With(Palette.ForJob(p.Job), dead ? 0.35f : 0.85f)))
                        ImGui.ProgressBar((float)damage / top, new Vector2(-1, icon), $"{damage / seconds:N0}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(party > 0 ? $"{100.0 * damage / party:0.0}%" : "–");
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(ShortNumber(damage));
                }
            }
        }

        Theme.Wrapped("Hits and DoT ticks on enemies; pets and summons count for their owner. Downtime counts, as in ACT. " +
                      "Raid buffs are credited to whoever dealt the damage, not to whoever gave the buff.", Theme.TextFaint);
    }

    private static string ShortNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.00}M",
        >= 10_000 => $"{n / 1_000.0:0}k",
        _ => $"{n:N0}",
    };

    private void DrawDeaths(PullReplay r)
    {
        if (deaths.Count == 0)
        {
            Theme.Dim("No player deaths.");
            return;
        }

        if (deathLabels.Count != deaths.Count)
            deathLabels = deaths.Select(d => $"{Timeline.Fmt(d.T),7}  {renderer.DisplayName(d.Victim)}{(d.RaisedMs >= 0 ? "  (raised)" : "")}").ToList();

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var listH = Math.Min((deaths.Count * lineH) + (2 * ImGui.GetStyle().WindowPadding.Y), ImGui.GetContentRegionAvail().Y * 0.35f);
        using (var child = Panel("##deathlist", new Vector2(0, Math.Max(listH, lineH * 3))))
        {
            if (child.Success)
            {
                for (var i = 0; i < deaths.Count; i++)
                {
                    var d = deaths[i];
                    using var id = ImRaii.PushId(i);
                    ReportView.JobIcon(d.Victim.Job, ImGui.GetTextLineHeight());
                    ImGui.SameLine(0, 4 * Scale);
                    if (ImGui.Selectable(deathLabels[i], d == selectedDeath))
                    {
                        selectedDeath = d;
                        Seek(Math.Max(r.FirstMs, d.T - 3000));
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{d.Cause}\n\nClick for the last 15 s of damage (seeks 3 s before the death).");
                }
            }
        }

        if (selectedDeath is not { } sel)
        {
            Theme.Dim("Select a death for its damage recap.");
            return;
        }

        if (!ReferenceEquals(recapFor, sel))
            BuildRecap(r, sel);

        Theme.Wrapped($"{renderer.DisplayName(sel.Victim)} — {sel.Cause}", Theme.SevCritical);
        Theme.Dim("Damage taken in the last 15 s (HP before each hit):");
        using (var table = ImRaii.Table("##recap", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp |
                                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV,
                                        new Vector2(0, ImGui.GetContentRegionAvail().Y * 0.6f)))
        {
            if (table.Success)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                ImGui.TableSetupColumn("Ability", ImGuiTableColumnFlags.WidthStretch, 1.8f);
                ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthStretch, 0.8f);
                ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                Theme.TableHeaders(RecapNames, RecapTips);
                foreach (var row in recap)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(row.Time);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(row.Ability);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(row.Ability);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextColored(row.Damage ? Theme.SevCritical : Theme.Good, row.Amount);
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(row.Hp);
                }
            }
        }

        Theme.Dim("Statuses at death:");
        foreach (var st in recapStatuses)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(st);
        }
    }

    private void BuildRecap(PullReplay r, DeathEvent sel)
    {
        recapFor = sel;
        recap = r.Actions.Where(a => a.T <= sel.T + 100 && a.T >= sel.T - 15000)
                 .SelectMany(a => a.Hits.Where(h => h.Target == sel.Victim && (h.Damage > 0 || h.Heal > 0))
                                       .Select(h => (h.T, Name: a.Name, Src: a.Source, Dmg: h.Damage, Heal: h.Heal, Hp: h.HpBefore, Max: h.MaxHp)))
                 .Concat(r.Ticks.Where(k => k.Target == sel.Victim && k.T <= sel.T + 100 && k.T >= sel.T - 15000)
                          .Select(k => (k.T, Name: k.IsHeal ? "HoT" : "DoT", Src: k.Source!, Dmg: k.IsHeal ? 0 : k.Amount,
                                        Heal: k.IsHeal ? k.Amount : 0, Hp: sel.Victim.Hp.At(k.T - 1), Max: sel.Victim.MaxHp)))
                 .OrderBy(x => x.T)
                 .Select(x => new RecapRow(
                             $"{(x.T - sel.T) / 1000f:0.0}s",
                             x.Src != null && !x.Src.IsPlayer ? x.Name : $"{x.Name} ({(x.Src != null ? renderer.DisplayName(x.Src) : "?")})",
                             x.Dmg > 0 ? $"-{x.Dmg:N0}" : $"+{x.Heal:N0}",
                             x.Dmg > 0,
                             x.Max > 0 ? $"{100f * x.Hp / x.Max:0}%" : ""))
                 .ToList();
        recapStatuses = r.Statuses.Where(s => s.Target == sel.Victim && s.Active(sel.T - 50)).Take(16)
                         .Select(s => $"{s.Name}{(s.Source is { IsPlayer: true } src ? $" ({renderer.DisplayName(src)})" : "")}")
                         .ToList();
    }

    private void DrawEvents(PullReplay r)
    {
        var casts = (eventFilter & 1) != 0;
        var deathsOn = (eventFilter & 2) != 0;
        var markers = (eventFilter & 4) != 0;
        var mechs = (eventFilter & 8) != 0;
        if (ImGui.Checkbox("Casts", ref casts))
            eventFilter ^= 1;
        Theme.Tip("Boss and enemy casts");
        ImGui.SameLine();
        if (ImGui.Checkbox("Deaths", ref deathsOn))
            eventFilter ^= 2;
        ImGui.SameLine();
        if (ImGui.Checkbox("Markers", ref markers))
            eventFilter ^= 4;
        Theme.Tip("Head markers and tethers");
        ImGui.SameLine();
        if (ImGui.Checkbox("Mechanics", ref mechs))
            eventFilter ^= 8;
        Theme.Tip("Mechanic resolves from the encounter pack");

        var key = (r, eventFilter, config.AnonymizeNames);
        if (key != eventsKey)
        {
            eventsKey = key;
            BuildEvents(r, (eventFilter & 1) != 0, (eventFilter & 2) != 0, (eventFilter & 4) != 0, (eventFilter & 8) != 0);
        }

        using var child = Panel("##events", Vector2.Zero);
        if (!child.Success)
            return;
        var t = (int)timeMs;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            using var id = ImRaii.PushId(i);
            using (ImRaii.PushColor(ImGuiCol.Text, e.T <= t ? e.Color : e.Color * new Vector4(1, 1, 1, 0.55f)))
            {
                if (ImGui.Selectable(e.Text))
                    Seek(e.T);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(e.Text + "\n(click to seek)");
        }
    }

    private void BuildEvents(PullReplay r, bool casts, bool deathsOn, bool markers, bool mechs)
    {
        var list = new List<(int T, string Text, Vector4 Color)>();
        if (casts)
        {
            foreach (var c in r.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Enemy))
                list.Add((c.StartMs, $"{c.Source.DisplayName} casts {c.Name} ({c.DurationMs / 1000f:0.0}s)", new Vector4(1, 0.7f, 0.4f, 1)));
        }

        if (deathsOn)
        {
            foreach (var d in r.Deaths.Where(d => d.Victim.IsPlayer))
                list.Add((d.T, $"{renderer.DisplayName(d.Victim)} died — {d.Cause}", new Vector4(1, 0.45f, 0.45f, 1)));
        }

        if (markers)
        {
            foreach (var m in r.HeadMarkers)
                list.Add((m.T, $"Marker {m.Label ?? $"{m.MarkerId:X4}"} on {renderer.DisplayName(m.Target)}", new Vector4(1, 0.9f, 0.4f, 1)));
            foreach (var te in r.Tethers)
            {
                list.Add((te.StartMs, $"Tether {te.Label ?? $"{te.TetherId:X4}"}: {renderer.DisplayName(te.Source)} → {renderer.DisplayName(te.Target)}",
                          new Vector4(0.8f, 0.6f, 1, 1)));
            }
        }

        if (mechs)
        {
            foreach (var m in r.Mechanics)
                list.Add((m.T, $"== {m.Label}", new Vector4(0.4f, 0.9f, 1, 1)));
        }

        list.Sort((a, b) => a.T.CompareTo(b.T));
        events = list.Select(e => (e.T, $"{Timeline.Fmt(e.T),7}  {e.Text}", e.Color)).ToList();
    }

    private void DrawParty(PullReplay r)
    {
        var t = (int)timeMs;
        var size = ImGui.GetTextLineHeight() * 1.2f;
        foreach (var p in r.Party)
        {
            var hp = p.Hp.At(t);
            var dead = r.Deaths.Any(d => d.Victim == p && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t));
            ReportView.JobIcon(p.Job, size);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(renderer.DisplayName(p));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, dead ? Theme.SevCritical : Theme.Good))
            {
                ImGui.ProgressBar(dead ? 0 : p.MaxHp > 0 && hp >= 0 ? (float)hp / p.MaxHp : 0, new Vector2(-1, 0),
                                  dead ? "dead" : p.MaxHp > 0 ? $"{100f * hp / p.MaxHp:0}%" : "");
            }

            var debuffs = r.Statuses.Where(s => s.Target == p && s.Active(t) && s.Source is not { IsPlayer: true }).Select(s => s.Name).Distinct();
            var text = string.Join(", ", debuffs);
            if (text.Length > 0)
            {
                using (ImRaii.PushIndent(size + ImGui.GetStyle().ItemSpacing.X, false))
                    Theme.Wrapped(text, Theme.TextDim);
            }
        }
    }

    private void DrawInfo(PullReplay r)
    {
        var s = r.Summary;
        using (Theme.TitleFont())
            Theme.Wrapped($"{s.ZoneName} · pull #{s.Ordinal} · {s.Outcome}", Theme.Text);
        Theme.Wrapped($"{s.StartLocal:dddd yyyy-MM-dd HH:mm:ss} · {Timeline.Fmt(s.DurationMs)}", Theme.TextDim);
        Theme.Wrapped($"{System.IO.Path.GetFileName(s.FilePath)} @ {s.StartOffset:N0}", Theme.TextDim);
        Theme.Wrapped($"Encounter pack: {r.Encounter?.Def.Name ?? "none (generic)"}", Theme.TextDim);
        Theme.Wrapped($"Actors {r.Actors.Count} · casts {r.Casts.Count} · actions {r.Actions.Count} · AoEs {r.Aoes.Count}", Theme.TextDim);
        ImGui.Spacing();
        Theme.Wrapped("Display options are in the canvas View menu (eye icon, top right of the arena) and in Settings › Display.", Theme.TextFaint);
        ImGui.Spacing();
        Theme.Caption("INSPECTOR");
        if (renderer.HoveredActor is { } a)
            Theme.Wrapped($"Hover: {a} kind={a.Kind} base={a.BNpcBaseId} r={a.Radius:0.0}", Theme.TextDim);
        else if (renderer.HoveredAoe is null)
            Theme.Wrapped("Hover an actor or AoE on the canvas to inspect it here.", Theme.TextFaint);
        if (renderer.HoveredAoe is { } aoe)
            Theme.Wrapped($"Hover AoE: {aoe.Label} 0x{aoe.ActionId:X} {aoe.Shape} [{aoe.ShapeSource}]", Theme.TextDim);
        if (r.Diagnostics.Count > 0)
        {
            ImGui.Spacing();
            Theme.Caption("DIAGNOSTICS");
            foreach (var d in r.Diagnostics)
                Theme.Wrapped(d, Theme.TextDim);
        }
    }

    private readonly record struct RecapRow(string Time, string Ability, string Amount, bool Damage, string Hp);
}

/// <summary>Pre-formatted pull-list row (rebuilt only when the pull summary changes).</summary>
internal sealed class PullRowText
{
    private readonly (long End, int Deaths, PullOutcome Outcome, int Phases, float Hp, bool Anon) key;

    public PullRowText(PullSummary p, bool anonymize, Core.Encounters.EncounterRegistry registry)
    {
        key = KeyOf(p, anonymize);
        Ordinal = $"#{p.Ordinal}";
        Date = p.StartLocal.ToString("MM-dd");
        Time = p.StartLocal.ToString("HH:mm");
        Duration = Timeline.Fmt(p.DurationMs)[..^2];
        (Result, ResultColor) = p.Outcome switch
        {
            PullOutcome.Clear => ("Clear", Theme.Good),
            PullOutcome.Wipe => ("Wipe", new Vector4(1, 0.5f, 0.45f, 1)),
            PullOutcome.ZoneOut => ("Zone out", Theme.TextDim),
            PullOutcome.CombatEnd => ("Ended", Theme.TextDim),
            PullOutcome.InProgress => ("Live", Theme.SevMajor),
            _ => ("?", Theme.TextDim),
        };
        Phase = p.FurthestPhase ?? "";
        Boss = p.BossHpPct >= 0 ? $"{p.BossHpPct:0.0}%" : "–";
        Deaths = p.Deaths.ToString();

        var sb = new System.Text.StringBuilder();
        sb.Append($"Pull #{p.Ordinal} · {p.ZoneName}\n");
        sb.Append($"{p.StartLocal:dddd yyyy-MM-dd HH:mm:ss}\n");
        sb.Append($"{Result} after {Timeline.Fmt(p.DurationMs)[..^2]}");
        if (p.FurthestPhase != null)
            sb.Append($" · reached {p.FurthestPhase}");
        sb.Append('\n');
        if (p.BossHpPct >= 0)
            sb.Append($"Boss: {p.BossName} at {p.BossHpPct:0.0}%\n");
        sb.Append($"Deaths: {p.Deaths}");
        if (p.Deaths > 0 && p.FirstDeathTicks > p.StartTicks)
            sb.Append($" (first at {Timeline.Fmt((int)((p.FirstDeathTicks - p.StartTicks) / TimeSpan.TicksPerMillisecond))[..^2]})");
        sb.Append('\n');
        if (p.Party.Count > 0)
        {
            sb.Append("Party: ");
            sb.Append(string.Join(", ", p.Party.Select(m => anonymize ? Jobs.Abbrev(m.Job) : $"{Jobs.Abbrev(m.Job)} {m.Name}")));
            sb.Append('\n');
        }

        var enc = p.EncounterKey != null ? registry.Encounters.FirstOrDefault(e => e.Key == p.EncounterKey)?.Def.Name ?? p.EncounterKey : "none";
        sb.Append($"Encounter pack: {enc}\n");
        sb.Append($"{System.IO.Path.GetFileName(p.FilePath)} @ {p.StartOffset:N0}");
        if (p.StartTruncated)
            sb.Append("\n(log starts mid-pull)");
        if (p.EndTruncated)
            sb.Append("\n(in progress / truncated)");
        sb.Append("\n\nClick to load.");
        Tooltip = sb.ToString();
    }

    public string Ordinal { get; }
    public string Date { get; }
    public string Time { get; }
    public string Duration { get; }
    public string Result { get; }
    public Vector4 ResultColor { get; }
    public string Phase { get; }
    public string Boss { get; }
    public string Deaths { get; }
    public string Tooltip { get; }

    public bool IsCurrent(PullSummary p, bool anonymize) => key == KeyOf(p, anonymize);

    private static (long, int, PullOutcome, int, float, bool) KeyOf(PullSummary p, bool anonymize) =>
        (p.EndTicks, p.Deaths, p.Outcome, p.Phases.Count, p.BossHpPct, anonymize);
}
