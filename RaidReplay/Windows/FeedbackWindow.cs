using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Export;
using RaidReplay.Core.Feedback;
using RaidReplay.Core.Model;
using RaidReplay.Rendering;
using RaidReplay.Services;

namespace RaidReplay.Windows;

/// <summary>
/// "Report an inaccuracy" form for a wipe report: which incident, what's wrong, a note, and optionally the pull's log
/// (names replaced). Shows exactly what will be sent; nothing leaves the machine until Send.
/// </summary>
public sealed class FeedbackWindow : Window, IDisposable
{
    private static readonly (FeedbackCategory Category, string Label, string Hint)[] Categories =
    [
        (FeedbackCategory.WrongCulprit, "Wrong player blamed", "The mistake is right, but someone else was at fault."),
        (FeedbackCategory.WrongRootCause, "Wrong root cause", "Something else lost the pull."),
        (FeedbackCategory.MissedMistake, "A mistake it missed", "Someone made a mistake the report doesn't mention."),
        (FeedbackCategory.WrongSpot, "Wrong \"should have been\" spot", "Where someone should have stood is wrong."),
        (FeedbackCategory.Other, "Something else", "Anything else about this report."),
    ];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly FeedbackService service;
    private readonly Configuration config;
    private readonly string version;

    private WipeReport? report;
    private int incidentIndex = -1;
    private int category = -1;
    private string note = string.Empty;
    private bool attachLog = true;
    private CancellationTokenSource? excerptCts;
    private Task<(ExcerptInfo Info, string Base64)>? excerpt;
    private Task<(FeedbackService.Result Result, string Message)>? sending;
    private string? preview;
    private (int, int, string, bool, bool) previewKey;

    public FeedbackWindow(FeedbackService service, Configuration config)
        : base("Report an inaccuracy###RaidReplayFeedback")
    {
        this.service = service;
        this.config = config;
        version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "?";
        Size = new Vector2(540, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 420), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void Dispose() => excerptCts?.Cancel();

    /// <summary>Opens the form for <paramref name="r"/>, starting at <paramref name="incident"/> (the selected one).</summary>
    public void Open(WipeReport r, Incident? incident)
    {
        if (!ReferenceEquals(report, r) || sending is { IsCompleted: true })
        {
            report = r;
            category = -1;
            note = string.Empty;
            attachLog = true;
            sending = null;
            preview = null;
            excerptCts?.Cancel();
            excerptCts = new CancellationTokenSource();
            var pull = r.Pull.Summary;
            var ct = excerptCts.Token;
            excerpt = Task.Run(() => BuildExcerpt(pull, ct), ct);
        }

        incidentIndex = incident != null ? r.Incidents.IndexOf(incident) : -1;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw() => Theme.PushWindow();

    public override void PostDraw() => Theme.PopWindow();

    public override void Draw()
    {
        if (report == null)
        {
            Theme.Dim("Open this from a wipe report.");
            return;
        }

        var r = report;
        var busy = sending is { IsCompleted: false };
        var sent = sending is { IsCompletedSuccessfully: true, Result.Result: FeedbackService.Result.Sent or FeedbackService.Result.Queued };
        Theme.Wrapped($"Pull #{r.Pull.Summary.Ordinal} · {r.Verdict}", Theme.TextDim);
        ImGui.Spacing();

        using (ImRaii.Disabled(busy || sent))
        {
            ImGui.TextUnformatted("Which part of the report?");
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##incident", IncidentLabel(r, incidentIndex)))
            {
                if (combo.Success)
                {
                    if (ImGui.Selectable("The report as a whole", incidentIndex < 0))
                        incidentIndex = -1;
                    for (var i = 0; i < r.Incidents.Count; i++)
                    {
                        if (ImGui.Selectable($"{IncidentLabel(r, i)}##{i}", incidentIndex == i))
                            incidentIndex = i;
                    }
                }
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("What's wrong?");
            for (var i = 0; i < Categories.Length; i++)
            {
                if (ImGui.RadioButton(Categories[i].Label, category == i))
                    category = i;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Categories[i].Hint);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("What should it have said?");
            ImGui.InputTextMultiline("##note", ref note, FeedbackReport.MaxNote, new Vector2(-1, 110 * ImGuiHelpers.GlobalScale));
            Theme.Wrapped("For example: \"M1 was in their tower; R2 should have soaked it.\"", Theme.TextFaint);

            ImGui.Spacing();
            ImGui.Checkbox(LogLabel(), ref attachLog);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The pull's lines from your ACT log, so the analysis can be replayed and fixed.\n" +
                                 "Player names become Player1–8, dates and checksums are removed, chat isn't included.");
        }

        if (ImGui.CollapsingHeader("What will be sent"))
        {
            var key = (incidentIndex, category, note, attachLog, excerpt?.IsCompleted ?? false);
            if (preview == null || key != previewKey)
            {
                previewKey = key;
                var body = Build(withLog: false);
                if (attachLog && LogReady() is { } log)
                    body["log_gz_base64"] = $"<{log.Base64.Length / 1024} KB: the pull's log, names replaced by Player1–8>";
                preview = body.ToJsonString(Pretty);
            }

            using var child = ImRaii.Child("##preview", new Vector2(-1, 180 * ImGuiHelpers.GlobalScale), true);
            if (child.Success)
                Theme.Wrapped(preview, Theme.TextDim);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(busy || sent || category < 0 || (attachLog && excerpt is { IsCompleted: false })))
        {
            if (ImGui.Button("Send"))
                Send();
        }

        if (category < 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Choose what's wrong first.");
        ImGui.SameLine();
        if (ImGui.Button(sent ? "Close" : "Cancel"))
            IsOpen = false;
        ImGui.SameLine();
        DrawStatus();

        ImGui.Spacing();
        Theme.Wrapped("Goes to the Raid Replay developer, only when you press Send. Player names are replaced by party slots " +
                      "(MT, H1, …) everywhere.", Theme.TextFaint);
    }

    private void DrawStatus()
    {
        ImGui.AlignTextToFramePadding();
        switch (sending)
        {
            case null:
                break;
            case { IsCompleted: false }:
                Theme.Dim("Sending…");
                break;
            case { IsCompletedSuccessfully: true, Result: var (result, message) }:
                if (result == FeedbackService.Result.Sent)
                    ImGui.TextColored(Theme.Good, "Sent. Thank you!");
                else if (result == FeedbackService.Result.Queued)
                    ImGui.TextColored(Theme.SevMajor, "Couldn't reach the server; saved and will retry.");
                else
                    ImGui.TextColored(Theme.SevCritical, "The server refused it.");
                if (message.Length > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(message);
                break;
            default:
                ImGui.TextColored(Theme.SevCritical, "Sending failed.");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(sending.Exception?.GetBaseException().Message ?? string.Empty);
                break;
        }
    }

    private void Send()
    {
        if (config.FeedbackInstallId == Guid.Empty)
        {
            config.FeedbackInstallId = Guid.NewGuid();
            config.Save();
        }

        var json = Build(withLog: attachLog).ToJsonString();
        sending = Task.Run(() => service.SendAsync(json));
    }

    private System.Text.Json.Nodes.JsonObject Build(bool withLog)
    {
        var r = report!;
        var log = withLog ? LogReady() : null;
        var enc = r.Pull.Encounter;
        return FeedbackReport.Build(r, incidentIndex >= 0 && incidentIndex < r.Incidents.Count ? r.Incidents[incidentIndex] : null,
                                    category >= 0 ? Categories[category].Category : FeedbackCategory.Other, note, version,
                                    config.FeedbackInstallId, enc != null ? $"{enc.Key} {enc.Hash[..Math.Min(8, enc.Hash.Length)]}" : null,
                                    log?.Info, log?.Base64);
    }

    private (ExcerptInfo Info, string Base64)? LogReady() =>
        excerpt is { IsCompletedSuccessfully: true } t && t.Result.Base64.Length <= FeedbackReport.MaxLogBase64 ? t.Result : null;

    private string LogLabel() => excerpt switch
    {
        { IsCompleted: false } => "Attach this pull's log (preparing…)",
        { IsCompletedSuccessfully: true } t when t.Result.Base64.Length > FeedbackReport.MaxLogBase64 =>
            "Attach this pull's log (too large to send)",
        { IsCompletedSuccessfully: true } t => $"Attach this pull's log ({t.Result.Base64.Length / 1024:N0} KB, names replaced)",
        _ => "Attach this pull's log (couldn't be read)",
    } + "###attachlog";

    private static string IncidentLabel(WipeReport r, int i) =>
        i < 0 || i >= r.Incidents.Count
            ? "The report as a whole"
            : $"{Timeline.Fmt(r.Incidents[i].T)[..^2]}  {(r.Incidents[i].IsRootCause ? "ROOT · " : "")}{r.Incidents[i].Title}";

    /// <summary>The pull's anonymized log, gzipped and base64-encoded.</summary>
    private static (ExcerptInfo Info, string Base64) BuildExcerpt(PullSummary pull, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ExcerptInfo info;
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gz, new UTF8Encoding(false), 1 << 16))
            info = PullExcerpt.Write(pull, writer, ct: ct);
        return (info, Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length));
    }
}
