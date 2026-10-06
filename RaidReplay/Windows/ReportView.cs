using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.GameData;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>Renders a <see cref="WipeReport"/>: verdict, incidents and the per-player snapshot of the selected incident.</summary>
public sealed class ReportView
{
    private readonly Configuration config;

    public ReportView(Configuration config) => this.config = config;

    public Incident? Selected { get; set; }

    /// <summary>Draws the report. Returns an incident the user clicked (to seek the replay), if any.</summary>
    public Incident? Draw(WipeReport report, bool compact = false)
    {
        Incident? clicked = null;
        var s = report.Pull.Summary;
        if (Selected != null && !report.Incidents.Contains(Selected))
            Selected = null;
        Selected ??= report.RootCause;

        ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1), report.Verdict.ToUpperInvariant());
        ImGui.SameLine();
        ImGui.TextDisabled($"#{s.Ordinal} {s.StartLocal:MM-dd HH:mm} · {Timeline.Fmt(s.DurationMs)} · {report.Phase}" +
                           (report.Segment != null ? $" · {report.Segment}" : "") +
                           (s.BossHpPct >= 0 ? $" · boss {s.BossHpPct:0.0}%" : ""));
        ImGui.TextWrapped(report.Headline);
        if (report.RootCause is { Detail.Length: > 0 } root)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.85f, 0.85f, 0.85f, 1)))
                ImGui.TextWrapped(root.Detail);
        }

        foreach (var n in report.Notes)
            ImGui.BulletText(n);
        if (report.LearnedPulls > 0)
            ImGui.TextDisabled($"Expected positions learned from {report.LearnedPulls} pulls of this fight.");
        ImGui.Separator();

        var listHeight = compact ? 150 : Math.Max(140, ImGui.GetContentRegionAvail().Y * 0.42f);
        using (var child = ImRaii.Child("##incidents", new Vector2(0, listHeight), true))
        {
            if (child.Success)
            {
                foreach (var inc in report.Incidents)
                {
                    var col = Palette.ForIncident(inc.Kind);
                    using var id = ImRaii.PushId(inc.GetHashCode());
                    using (ImRaii.PushColor(ImGuiCol.Text, inc.IsRootCause ? new Vector4(1, 0.45f, 0.35f, 1) : col))
                    {
                        var label = $"{(inc.IsRootCause ? "► " : "  ")}{Timeline.Fmt(inc.T),7}  {inc.Title}";
                        if (ImGui.Selectable(label, ReferenceEquals(Selected, inc)))
                        {
                            Selected = inc;
                            clicked = inc;
                        }
                    }

                    if (ImGui.IsItemHovered() && (inc.Detail.Length > 0 || inc.Mechanic != null))
                        ImGui.SetTooltip((inc.Mechanic != null ? $"[{inc.Mechanic}] " : "") + inc.Detail);
                }
            }
        }

        if (Selected != null)
            DrawSnapshot(Selected);
        return clicked;
    }

    private void DrawSnapshot(Incident inc)
    {
        ImGui.TextColored(Palette.ForIncident(inc.Kind), $"{Timeline.Fmt(inc.T)} {inc.Title}");
        if (inc.Mechanic != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"[{inc.Mechanic}]");
        }

        if (inc.Detail.Length > 0)
            ImGui.TextWrapped(inc.Detail);

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp |
                                      ImGuiTableFlags.ScrollY;
        using var table = ImRaii.Table("##snapshot", 6, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Facing", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Should have been", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableHeadersRow();
        foreach (var s in inc.Snapshot.OrderByDescending(s => s.Involved).ThenByDescending(s => s.MissDistance ?? 0))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var name = config.AnonymizeNames ? Jobs.Abbrev(s.Player.Job) : $"{Jobs.Abbrev(s.Player.Job)} {s.Player.Name}";
            var color = !s.Alive ? new Vector4(0.6f, 0.6f, 0.6f, 1) : s.Involved ? new Vector4(1, 0.55f, 0.45f, 1) : new Vector4(1, 1, 1, 1);
            ImGui.TextColored(color, (s.Involved ? "* " : "  ") + name + (s.Alive ? "" : " (dead)"));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{s.Pos.X:0.0}, {s.Pos.Y:0.0}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Compass(s.Heading));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(s.HpPct >= 0 ? $"{s.HpPct:0}%" : "-");
            ImGui.TableSetColumnIndex(4);
            if (s.Expected is { } e)
            {
                var d = e - s.Pos;
                ImGui.TextColored(Palette.ForExpected(s.ExpectedSource),
                                  $"{e.X:0.0}, {e.Y:0.0}  ({s.MissDistance:0.0}y {(d.LengthSquared() > 0.01f ? Compass(MathF.Atan2(d.X, d.Y)) : "")})");
            }

            ImGui.TableSetColumnIndex(5);
            if (s.Expected != null)
                ImGui.TextDisabled(s.ExpectedSource switch
                {
                    ExpectedSource.Learned => $"usual spot ({s.ExpectedNote})",
                    ExpectedSource.Soak => "soak position",
                    ExpectedSource.SafeSpot => "nearest safe spot",
                    _ => "",
                });
        }
    }

    public static string Compass(float heading)
    {
        string[] names = ["S", "SE", "E", "NE", "N", "NW", "W", "SW"];
        var idx = (int)Math.Round(heading / (Math.PI / 4));
        return names[((idx % 8) + 8) % 8];
    }
}
