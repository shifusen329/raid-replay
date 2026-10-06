using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.GameData;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>
/// The after-action "wipe card": verdict and chips, the root-cause card (culprits, one-line detail, optional
/// mini-map of the moment), compact expandable cards for contributing incidents, and the mitigation plan check.
/// All strings come pre-formatted from <see cref="WipeCard"/>; drawing is layout only.
/// </summary>
public sealed class ReportView
{
    private static readonly string[] SnapNames = ["Player", "HP", "Dir", "Miss", "Why", "Pos"];

    private static readonly string[] SnapTips =
    [
        "Party slot and name. ● = involved in this incident. Hover a row for everything at once.",
        "HP at the incident.",
        "Facing (compass).",
        "Distance and direction from where they stood to where they should have been.",
        "Where the expected spot comes from: a soak, the nearest safe spot, their usual spot in good pulls, or an assigned spot.",
        "Position (x, z) in yalms at the incident. Hidden by default: right-click a header to show it.",
    ];

    private static readonly string[] MitNames = ["Mechanic", "Player", "Planned", "Result"];

    private static readonly string[] MitTips =
    [
        "Mechanic from the mitigation sheet and its hit time.",
        "Who the sheet assigns.",
        "Planned cooldown ((carry) = pressed for an earlier mechanic and expected to still be up).",
        "up · MISSING (off cooldown, alive, not used) · LATE / NOT UP (pressed but not on anyone at the hit) · on cooldown · ...",
    ];

    private readonly Configuration config;
    private readonly ReplayRenderer renderer;
    private readonly ArenaView miniView = new() { WheelZoom = false };
    private WipeReport? fitFor;
    private bool showAllMitigation;
    private bool rootMore;
    private DateTime copiedAt;

    public ReportView(Configuration config, IGameData gameData)
    {
        this.config = config;
        renderer = new ReplayRenderer(config, gameData);
    }

    public Incident? Selected { get; set; }

    /// <summary>Time the summary was last copied (for "Copied" feedback).</summary>
    public bool RecentlyCopied => (DateTime.UtcNow - copiedAt).TotalSeconds < 1.5;

    private static float Scale => ImGuiHelpers.GlobalScale;

    public void CopySummary(WipeReport report)
    {
        ImGui.SetClipboardText(WipeCard.For(report, config.AnonymizeNames).Clipboard);
        copiedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Draws the report. <paramref name="miniMap"/> adds a snapshot map of the selected incident next to the root
    /// cause. Returns an incident the user clicked (to seek the replay), if any.
    /// </summary>
    public Incident? Draw(WipeReport report, bool miniMap)
    {
        var card = WipeCard.For(report, config.AnonymizeNames);
        Incident? clicked = null;
        if (Selected != null && card.RowFor(Selected) == null)
            Selected = null;
        Selected ??= report.RootCause;

        DrawHeader(card, report);
        ImGui.Spacing();
        DrawHero(card, report, miniMap, ref clicked);
        DrawParty(card);
        ImGui.Spacing();
        DrawContributing(card, ref clicked);
        if (card.Mitigation.Count > 0)
        {
            ImGui.Spacing();
            DrawMitigation(card);
        }

        if (card.Footnote != null)
        {
            ImGui.Spacing();
            Theme.Wrapped(card.Footnote, Theme.TextFaint);
        }

        return clicked;
    }

    // ---- header ----------------------------------------------------------------------------------------------

    private void DrawHeader(WipeCard card, WipeReport report)
    {
        using (Theme.HeadingFont())
            Theme.Wrapped(card.Verdict, card.VerdictColor);

        var row = Theme.ChipRow.Here();
        var right = row.MaxX;
        row.MaxX -= ImGui.GetFrameHeight() * 1.3f;
        if (Theme.Chip(ref row, card.TimeChip, card.VerdictColor, FontAwesomeIcon.Clock))
            ImGui.SetTooltip("When the root cause happened (pull time).");
        if (card.MechanicChip != null && Theme.Chip(ref row, card.MechanicChip, Theme.Accent, FontAwesomeIcon.Bullseye))
            ImGui.SetTooltip("Mechanic / segment of the root cause.");
        if (card.PhaseChip != null && Theme.Chip(ref row, card.PhaseChip, Theme.SevMinor, FontAwesomeIcon.LayerGroup))
            ImGui.SetTooltip("Phase.");

        // Copy button, right-aligned on the chip line.
        ImGui.SameLine();
        var w = ImGui.GetFrameHeight() * 1.15f;
        var x = right - w;
        if (ImGui.GetCursorScreenPos().X < x)
            ImGui.SetCursorScreenPos(new Vector2(x, ImGui.GetCursorScreenPos().Y));
        if (Theme.IconButton("##copy", RecentlyCopied ? FontAwesomeIcon.Check : FontAwesomeIcon.Clipboard,
                             RecentlyCopied ? "Copied!" : "Copy a text summary to the clipboard (for Discord / party chat)."))
            CopySummary(report);

        Theme.Wrapped(card.Meta, Theme.TextDim);
    }

    // ---- root cause ------------------------------------------------------------------------------------------

    private void DrawHero(WipeCard card, WipeReport report, bool miniMap, ref Incident? clicked)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var mapSize = MathF.Round(240 * Scale);
        var side = miniMap && avail >= mapSize + (280 * Scale);
        var cardW = side ? avail - mapSize - ImGui.GetStyle().ItemSpacing.X : avail;
        if (side)
            ImGui.BeginGroup();
        DrawRootCard(card, cardW, ref clicked);
        if (side)
        {
            ImGui.EndGroup();
            ImGui.SameLine();
            DrawMiniMap(card, report, mapSize);
        }
        else if (miniMap)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (avail - mapSize) / 2));
            DrawMiniMap(card, report, mapSize);
        }
    }

    private void DrawRootCard(WipeCard card, float width, ref Incident? clicked)
    {
        var root = card.Root;
        var c = Theme.BeginCard(width, root?.Color ?? Theme.SevMinor);
        if (root == null)
        {
            Theme.Caption("RESULT");
            Theme.Wrapped(card.Report.Headline, Theme.Text);
            Theme.EndCard(c);
            return;
        }

        Theme.Caption(root.Caption, Theme.With(root.Color, 0.9f));
        using (Theme.TitleFont())
            Theme.Wrapped(root.Title, Theme.Text);

        if (root.Culprits.Count > 0)
        {
            var row = new Theme.ChipRow(c.ContentMaxX);
            foreach (var p in root.Culprits)
            {
                if (Theme.Chip(ref row, p.Label, Palette.ForJob(p.Job), job: p.Job))
                    ImGui.SetTooltip(p.Tooltip);
            }
        }

        if (root.Detail.Length > 0)
        {
            Theme.Wrapped(rootMore || !root.HasMore ? root.Detail : root.DetailShort, new Vector4(0.82f, 0.85f, 0.9f, 1));
            if (root.HasMore && Link(rootMore ? "Show less" : "Show full detail"))
                rootMore = !rootMore;
        }

        foreach (var n in card.Notes)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            Theme.Wrapped(n, Theme.TextDim);
        }

        if (root.MitSummary != null)
            Theme.Wrapped(root.MitSummary, Theme.TextDim);

        if (root.Snapshot.Count > 0 && ImGui.TreeNode("Where everyone was###rootsnap"))
        {
            DrawSnapshotTable(root, c.ContentMaxX - ImGui.GetCursorScreenPos().X);
            ImGui.TreePop();
        }

        var selected = ReferenceEquals(Selected, root.Inc);
        var hovered = Theme.EndCard(c, selected);
        if (hovered && !ImGui.IsAnyItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Selected = root.Inc;
            clicked = root.Inc;
        }
    }

    private void DrawMiniMap(WipeCard card, WipeReport report, float size)
    {
        var inc = Selected ?? report.RootCause;
        var r = report.Pull;
        ImGui.BeginGroup();
        miniView.Begin("##minimap", new Vector2(size, size));
        var refit = miniView.Hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if ((!ReferenceEquals(fitFor, report) || refit) && miniView.CanvasSize.X > 10)
        {
            var arena = r.Encounter?.Def.Arena;
            miniView.Fit(arena != null ? new Vector2(arena.Center[0], arena.Center[1]) : new Vector2(100, 100),
                         arena != null ? (arena.ViewRadius > 0 ? arena.ViewRadius : arena.Radius * 1.15f) : 25);
            fitFor = report;
        }

        renderer.DrawMiniMap(r, miniView, inc);
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + size))
            Theme.Wrapped(card.RowFor(inc)?.MapCaption ?? "Snapshot at the end of the pull", Theme.TextFaint);
        ImGui.EndGroup();
    }

    // ---- party strip -----------------------------------------------------------------------------------------

    private static void DrawParty(WipeCard card)
    {
        if (card.Party.Count == 0)
            return;
        var size = ImGui.GetTextLineHeight() * 1.2f;
        var maxX = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;
        foreach (var p in card.Party)
        {
            var w = size + (3 * Scale) + ImGui.CalcTextSize(p.Label).X;
            if (!first)
            {
                ImGui.SameLine(0, 10 * Scale);
                if (ImGui.GetCursorScreenPos().X + w > maxX)
                    ImGui.NewLine();
            }

            first = false;
            ImGui.BeginGroup();
            JobIcon(p.Job, size);
            ImGui.SameLine(0, 3 * Scale);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, p.Label);
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(p.Tooltip);
        }
    }

    // ---- contributing incidents ------------------------------------------------------------------------------

    private void DrawContributing(WipeCard card, ref Incident? clicked)
    {
        Theme.SectionTitle(card.ContributingTitle);
        if (card.Contributing.Count == 0)
        {
            Theme.Dim("No other incidents.");
            return;
        }

        var width = ImGui.GetContentRegionAvail().X;
        foreach (var row in card.Contributing)
            DrawIncidentRow(row, width, ref clicked);
    }

    private void DrawIncidentRow(IncidentRow row, float width, ref Incident? clicked)
    {
        var selected = ReferenceEquals(Selected, row.Inc);
        var c = Theme.BeginCard(width, row.Color, 5);
        var x0 = ImGui.GetCursorPosX();
        bool open;
        using (ImRaii.PushId(row.Inc.GetHashCode()))
        {
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.NoTreePushOnOpen |
                        ImGuiTreeNodeFlags.AllowItemOverlap | ImGuiTreeNodeFlags.SpanAvailWidth;
            open = ImGui.TreeNodeEx("##row", flags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            {
                Selected = row.Inc;
                clicked = row.Inc;
            }

            if (ImGui.IsItemHovered() && !open)
                ImGui.SetTooltip(row.Tooltip);
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(x0 + ImGui.GetTreeNodeToLabelSpacing());
        ImGui.TextColored(Theme.TextDim, row.Time);
        var lineH = ImGui.GetTextLineHeight();
        var n = 0;
        foreach (var p in row.Culprits)
        {
            if (n++ == 4)
                break;
            ImGui.SameLine(0, n == 1 ? 8 * Scale : 2 * Scale);
            JobIcon(p.Job, lineH);
        }

        ImGui.SameLine(0, 8 * Scale);
        var avail = c.ContentMaxX - ImGui.GetCursorScreenPos().X;
        ImGui.TextUnformatted(Theme.Ellipsize(row.Title, avail, ref row.EllipsisWidth, ref row.Ellipsis));

        if (open)
        {
            ImGui.Indent(ImGui.GetTreeNodeToLabelSpacing());
            Theme.Caption(row.Caption, Theme.With(row.Color, 0.9f));
            if (row.Title.Length > 0 && !ReferenceEquals(row.Ellipsis, row.Title))
                Theme.Wrapped(row.Title, Theme.Text);
            if (row.Culprits.Count > 0)
            {
                var chips = new Theme.ChipRow(c.ContentMaxX);
                foreach (var p in row.Culprits)
                {
                    if (Theme.Chip(ref chips, p.Label, Palette.ForJob(p.Job), job: p.Job))
                        ImGui.SetTooltip(p.Tooltip);
                }
            }

            if (row.Detail.Length > 0)
                Theme.Wrapped(row.Detail, new Vector4(0.82f, 0.85f, 0.9f, 1));
            if (row.MitSummary != null)
                Theme.Wrapped(row.MitSummary, Theme.TextDim);
            if (row.Snapshot.Count > 0)
                DrawSnapshotTable(row, c.ContentMaxX - ImGui.GetCursorScreenPos().X);
            ImGui.Unindent(ImGui.GetTreeNodeToLabelSpacing());
        }

        Theme.EndCard(c, selected);
    }

    // ---- tables ----------------------------------------------------------------------------------------------

    private static void DrawSnapshotTable(IncidentRow row, float width)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                                      ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.SizingStretchProp;
        using var wrap = ImRaii.TextWrapPos(-1);

        // Same id for every incident: column widths/visibility are shared (and saved) across all snapshot tables.
        using var table = ImRaii.Table("##snapshot", 6, flags, new Vector2(Math.Max(100, width), 0));
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 2.2f);
        ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupColumn("Dir", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Miss", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Pos", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 1.0f);
        Theme.TableHeaders(SnapNames, SnapTips);
        var size = ImGui.GetTextLineHeight();
        foreach (var s in row.Snapshot)
        {
            ImGui.TableNextRow();
            if (ImGui.TableSetColumnIndex(0))
            {
                JobIcon(s.Job, size);
                ImGui.SameLine(0, 4 * Scale);
                ImGui.TextColored(s.Color, s.Label);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(s.Tooltip);
            }

            if (ImGui.TableSetColumnIndex(1))
                ImGui.TextUnformatted(s.Hp);
            if (ImGui.TableSetColumnIndex(2))
                ImGui.TextUnformatted(s.Dir);
            if (ImGui.TableSetColumnIndex(3) && s.Miss.Length > 0)
            {
                ImGui.TextColored(s.MissColor, s.Miss);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(s.Tooltip);
            }

            if (ImGui.TableSetColumnIndex(4) && s.Why.Length > 0)
            {
                ImGui.TextColored(Theme.TextDim, s.Why);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(s.Why);
            }

            if (ImGui.TableSetColumnIndex(5))
                ImGui.TextColored(Theme.TextDim, s.Pos);
        }
    }

    private void DrawMitigation(WipeCard card)
    {
        if (!ImGui.CollapsingHeader(card.MitigationTitle))
            return;
        ImGui.Checkbox("Show entries that were up", ref showAllMitigation);
        Theme.Help("Reference plan: only cooldowns that were off cooldown, with their prerequisite met and the player alive, count as missing.");
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV |
                                      ImGuiTableFlags.Resizable;
        using var wrap = ImRaii.TextWrapPos(-1);
        using var table = ImRaii.Table("##mit", 4, flags);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Planned", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        Theme.TableHeaders(MitNames, MitTips);
        var size = ImGui.GetTextLineHeight();
        foreach (var m in card.Mitigation)
        {
            if (m.Header)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(m.Color, m.Mechanic);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(Theme.TextDim, m.Status);
                continue;
            }

            if (m.Active && !showAllMitigation)
                continue;
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);
            JobIcon(m.Job, size);
            ImGui.SameLine(0, 3 * Scale);
            ImGui.TextUnformatted(m.Player);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(m.Planned);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextColored(m.Color, m.Status);
            if (m.Detail.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, m.Detail);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(m.Detail);
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Accent-coloured clickable text.</summary>
    private static bool Link(string text)
    {
        ImGui.TextColored(Theme.Accent, text);
        if (!ImGui.IsItemHovered())
            return false;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return ImGui.IsItemClicked(ImGuiMouseButton.Left);
    }

    /// <summary>Draws a job icon inline (falls back to the abbreviation).</summary>
    public static void JobIcon(byte job, float size)
    {
        var icon = Jobs.Get(job)?.Icon ?? 0;
        if (icon == 0)
        {
            ImGui.TextUnformatted(Jobs.Abbrev(job));
            return;
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        ImGui.Image(tex.Handle, new Vector2(size, size));
    }

    public static string Compass(float heading)
    {
        ReadOnlySpan<string> names = ["S", "SE", "E", "NE", "N", "NW", "W", "SW"];
        var idx = (int)Math.Round(heading / (Math.PI / 4));
        return names[((idx % 8) + 8) % 8];
    }
}
