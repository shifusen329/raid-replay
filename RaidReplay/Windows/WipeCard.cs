using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Model;
using RaidReplay.Rendering;

namespace RaidReplay.Windows;

/// <summary>
/// Pre-formatted view model of a <see cref="WipeReport"/>. Every string the report UI shows is built once per report
/// (on the analysis thread for live wipes), so drawing the card is just layout. Contains no ImGui calls.
/// </summary>
public sealed class WipeCard
{
    private static readonly ConditionalWeakTable<WipeReport, WipeCard> Cache = new();

    private WipeCard(WipeReport report, bool anonymize)
    {
        Report = report;
        Anonymized = anonymize;
        var s = report.Pull.Summary;
        var root = report.RootCause;
        var deaths = report.Incidents.Count(i => i.Kind is IncidentKind.Death or IncidentKind.FellOff);

        Verdict = report.Verdict.Length > 0 ? report.Verdict : s.Outcome.ToString();
        VerdictColor = root != null ? Theme.Severity(root) : s.Outcome == PullOutcome.Clear ? Theme.Good : Theme.SevMinor;
        TimeChip = root != null ? Timeline.Fmt(root.T)[..^2] : Timeline.Fmt(s.DurationMs)[..^2];
        MechanicChip = root?.Mechanic ?? report.Segment;
        PhaseChip = report.Phase.Length > 0 && report.Phase != MechanicChip ? report.Phase : null;
        Meta = $"Pull #{s.Ordinal} · {s.StartLocal:ddd MM-dd HH:mm} · {Timeline.Fmt(s.DurationMs)[..^2]} long" +
               (s.BossHpPct >= 0 ? $" · boss {s.BossHpPct:0.0}%" : "") +
               $" · {deaths} death{(deaths == 1 ? "" : "s")} · {s.Outcome}";

        foreach (var inc in report.Incidents)
        {
            var row = new IncidentRow(this, inc);
            All.Add(row);
            if (ReferenceEquals(inc, root))
                Root = row;
            else if (inc.InCollapse)
                Contributing.Add(row);
        }

        BuildOutline(report);
        OutlineTitle = $"Incidents ({All.Count})";

        foreach (var slot in PartySlots.Order)
        {
            var p = report.Slots.FirstOrDefault(kv => kv.Value == slot).Key;
            if (p != null)
                Party.Add(new Culprit(p.Job, slot, $"{slot} · {Jobs.Abbrev(p.Job)} · {Name(p)}"));
        }

        Notes.AddRange(report.Notes);
        if (report.LearnedPulls > 0)
            Footnote = $"Expected positions learned from {report.LearnedPulls} pulls of this fight.";

        BuildMitigation(report);
        Clipboard = BuildClipboard(report);
        ChatLine = BuildChatLine(report);
    }

    public WipeReport Report { get; }
    public bool Anonymized { get; }
    public string Verdict { get; }
    public Vector4 VerdictColor { get; }
    public string TimeChip { get; }
    public string? MechanicChip { get; }
    public string? PhaseChip { get; }
    public string Meta { get; }
    public IncidentRow? Root { get; }
    public List<IncidentRow> All { get; } = [];

    /// <summary>Incidents of the final collapse (and the chain traced back from the root), other than the root.</summary>
    public List<IncidentRow> Contributing { get; } = [];

    /// <summary>Every incident, grouped by prog point (or by phase where the pack has none), in pull order.</summary>
    public List<OutlineSection> Outline { get; } = [];

    public string OutlineTitle { get; } = string.Empty;
    public List<Culprit> Party { get; } = [];
    public List<string> Notes { get; } = [];
    public string? Footnote { get; }
    public string MitigationTitle { get; private set; } = string.Empty;
    public List<MitRow> Mitigation { get; } = [];
    public string Clipboard { get; }

    /// <summary>The summary as one line for the game's chat box (see <see cref="ForChat"/>).</summary>
    public string ChatLine { get; }

    /// <summary>The card for a report, built on first use and cached for the report's lifetime. Thread-safe.</summary>
    public static WipeCard For(WipeReport report, bool anonymize)
    {
        if (Cache.TryGetValue(report, out var card) && card.Anonymized == anonymize)
            return card;
        card = new WipeCard(report, anonymize);
        Cache.AddOrUpdate(report, card);
        return card;
    }

    public IncidentRow? RowFor(Incident? inc)
    {
        if (inc == null)
            return null;
        foreach (var r in All)
        {
            if (ReferenceEquals(r.Inc, inc))
                return r;
        }

        return null;
    }

    internal string Name(Actor a) => Anonymized ? Jobs.Abbrev(a.Job) : a.Name;

    internal string Slot(Actor a)
    {
        var slot = Report.SlotOf(a);
        return slot.Length > 0 ? slot : Jobs.Abbrev(a.Job);
    }

    /// <summary>
    /// One section per prog point reached; a phase without prog points is one section. Each incident goes where what
    /// caused it happened (a death under the hit that killed them).
    /// </summary>
    private void BuildOutline(WipeReport report)
    {
        var r = report.Pull;
        var phases = r.Phases.Where(p => !p.IsSegment).ToList();
        foreach (var pp in r.ProgPoints)
            Outline.Add(new OutlineSection(pp.Id, pp.Name, phases.FirstOrDefault(p => p.Id == pp.Phase)?.Name, pp.StartMs, pp.EndMs));
        foreach (var ph in phases.Where(ph => r.ProgPoints.All(pp => pp.Phase != ph.Id)))
            Outline.Add(new OutlineSection(ph.Id, ph.Name, null, ph.StartMs, ph.EndMs));
        Outline.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        if (Outline.Count == 0)
            Outline.Add(new OutlineSection("pull", "Pull", null, 0, r.EndMs));

        foreach (var row in All)
        {
            var t = row.Inc.CauseT;
            (Outline.LastOrDefault(s => s.StartMs <= t) ?? Outline[0]).Rows.Add(row);
        }

        foreach (var s in Outline)
            s.Finish();
    }

    private void BuildMitigation(WipeReport report)
    {
        if (report.Mitigation.Count == 0)
            return;
        var missing = report.Mitigation.Sum(c => c.Missing.Count());
        MitigationTitle = $"Mitigation vs plan · {(missing > 0 ? $"{missing} missing" : "nothing missing")}" +
                          $" ({report.Pull.Encounter?.Def.Mitigation?.Source})###mitplan";
        foreach (var check in report.Mitigation)
        {
            var active = check.Entries.Count(e => e.Status == MitStatus.Active);
            var miss = check.Missing.Count();
            Mitigation.Add(new MitRow
            {
                Header = true,
                Mechanic = $"{Timeline.Fmt(check.T)[..^2]} {check.Mechanic.Name}",
                Status = $"{active}/{check.Entries.Count} up{(check.HitFound ? "" : " (hit not found)")}",
                Color = miss > 0 ? new Vector4(1, 0.6f, 0.5f, 1) : new Vector4(0.6f, 0.9f, 0.6f, 1),
            });
            foreach (var e in check.Entries)
            {
                var (label, color) = e.Status switch
                {
                    MitStatus.Active => ("up", new Vector4(0.5f, 0.95f, 0.5f, 1)),
                    MitStatus.Missing => ("MISSING", Theme.SevCritical),
                    MitStatus.UsedNotActive => (e.Blamable ? "LATE / NOT UP" : "not up", Theme.SevMajor),
                    MitStatus.OnCooldown => ("on cooldown", Theme.TextDim),
                    MitStatus.SheetConflict => ("sheet conflict", Theme.TextDim),
                    MitStatus.Expired => ("expired", new Vector4(0.8f, 0.75f, 0.5f, 1)),
                    MitStatus.NeedsPrerequisite => ("needs prerequisite", new Vector4(0.8f, 0.75f, 0.5f, 1)),
                    MitStatus.Dead => ("dead", Theme.TextDim),
                    _ => (e.Status.ToString(), Theme.TextDim),
                };
                Mitigation.Add(new MitRow
                {
                    Active = e.Status == MitStatus.Active,
                    Job = e.Player.Job,
                    Player = $"{Slot(e.Player)} {Name(e.Player)}",
                    Planned = e.Ability.Name + (e.Carry ? " (carry)" : string.Empty),
                    Status = label,
                    Detail = e.Detail,
                    Color = color,
                });
            }
        }
    }

    private string BuildClipboard(WipeReport report)
    {
        var s = report.Pull.Summary;
        var sb = new StringBuilder();
        sb.Append("[Raid Replay] ").Append(Verdict).Append(" — ").Append(TimeChip);
        if (MechanicChip != null)
            sb.Append(" · ").Append(MechanicChip);
        sb.Append($" (pull #{s.Ordinal}, {Timeline.Fmt(s.DurationMs)[..^2]}");
        if (report.Phase.Length > 0)
            sb.Append(", ").Append(report.Phase);
        if (s.BossHpPct >= 0)
            sb.Append($", boss {s.BossHpPct:0.0}%");
        sb.AppendLine(")");
        if (Root != null)
        {
            sb.Append("Root cause ").Append(Root.Time).Append(": ").Append(Root.Title);
            if (Root.Culprits.Count > 0)
                sb.Append(" — ").Append(string.Join(", ", Root.Culprits.Select(c => c.Label)));
            sb.AppendLine();
            if (Root.Detail.Length > 0)
                sb.AppendLine(Root.Detail);
        }

        if (Contributing.Count > 0)
        {
            sb.AppendLine("Contributing:");
            foreach (var r in Contributing.Take(15))
                sb.Append("• ").Append(r.Time).Append(' ').Append(r.Kind).Append(" — ").Append(r.Title)
                  .Append(r.DetailShort.Length > 0 ? $": {r.DetailShort}" : string.Empty).AppendLine();
            if (Contributing.Count > 15)
                sb.AppendLine($"… and {Contributing.Count - 15} more");
        }

        // Everything else, by prog point.
        var earlier = Outline.Select(sec => (sec, rows: sec.Rows.Where(r => !r.Inc.InCollapse).ToList())).Where(x => x.rows.Count > 0).ToList();
        if (earlier.Count > 0)
        {
            sb.AppendLine("Also in this pull:");
            foreach (var (sec, rows) in earlier)
                sb.Append("• ").Append(sec.Name).Append(": ").Append(string.Join("; ", rows.Take(3).Select(r => $"{r.Time} {r.Title}")))
                  .Append(rows.Count > 3 ? $"; … and {rows.Count - 3} more" : string.Empty).AppendLine();
        }

        foreach (var n in Notes)
            sb.Append("· ").AppendLine(n);
        return sb.ToString().TrimEnd();
    }

    private string BuildChatLine(WipeReport report)
    {
        var s = report.Pull.Summary;
        var head = $"[Raid Replay] {Verdict} — pull #{s.Ordinal}, {Timeline.Fmt(s.DurationMs)[..^2]}" +
                   (report.Phase.Length > 0 ? $", {report.Phase}" : "") + (s.BossHpPct >= 0 ? $", boss {s.BossHpPct:0.0}%" : "");
        return ForChat(Root != null ? $"{head}. Root cause {Root.Time}{(Root.Mechanic != null ? $" · {Root.Mechanic}" : "")}: {Root.ChatBody()}"
                                    : $"{head}. {report.Headline}");
    }

    private const int ChatMaxBytes = 500;

    /// <summary>
    /// Text for the game's chat box, which keeps only the first line of a paste and at most 500 bytes: line breaks become
    /// spaces, and longer text is cut at a word and ends with "...".
    /// </summary>
    internal static string ForChat(string text)
    {
        var s = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                      .Replace("…", "...");
        if (Encoding.UTF8.GetByteCount(s) <= ChatMaxBytes)
            return s;
        var cut = Math.Min(s.Length, ChatMaxBytes - 3);
        while (cut > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, cut)) > ChatMaxBytes - 3)
            cut--;
        var space = s.LastIndexOf(' ', cut);
        if (space > cut / 2)
            cut = space;
        return s[..cut].TrimEnd(' ', ',', ';', ':', '—', '-') + "...";
    }

    /// <summary>The first sentence/clause of a detail string, for one-line display.</summary>
    internal static string FirstSentence(string detail, out bool more)
    {
        more = false;
        if (detail.Length <= 150)
            return detail;
        foreach (var sep in new[] { "; ", ". ", " — " })
        {
            var i = detail.IndexOf(sep, 25, StringComparison.Ordinal);
            if (i > 0 && i <= 170)
            {
                more = true;
                return detail[..i].TrimEnd('.') + (sep == ". " ? "." : string.Empty);
            }
        }

        more = true;
        return detail[..140].TrimEnd() + "…";
    }
}

/// <summary>A prog point (or phase) of the report's incident outline.</summary>
public sealed class OutlineSection(string id, string name, string? phase, int startMs, int endMs)
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>The phase a prog point belongs to (shown as a heading above its prog points); null for a phase section.</summary>
    public string? Phase { get; } = phase;

    public int StartMs { get; } = startMs;
    public int EndMs { get; } = endMs;
    public List<IncidentRow> Rows { get; } = [];
    public string Range { get; private set; } = string.Empty;
    public Vector4 Color { get; private set; }
    public bool HasRoot { get; private set; }

    /// <summary>Holds the root cause or part of the collapse, so it starts open.</summary>
    public bool DefaultOpen { get; private set; }

    public string Tooltip { get; private set; } = string.Empty;

    internal void Finish()
    {
        Range = $"{Timeline.Fmt(Math.Max(0, StartMs))[..^2]}–{Timeline.Fmt(EndMs)[..^2]}";
        var worst = Rows.MaxBy(r => (r.Inc.IsRootCause, Theme.Severity(r.Inc) == Theme.SevCritical, r.Inc.Severity));
        Color = worst != null ? Theme.Severity(worst.Inc) : Theme.Good;
        HasRoot = Rows.Any(r => r.Inc.IsRootCause);
        DefaultOpen = HasRoot || Rows.Any(r => r.Inc.InCollapse);
        var kinds = Rows.GroupBy(r => r.Kind).Select(g => g.Count() > 1 ? $"{g.Count()} × {g.Key}" : g.Key);
        Tooltip = $"{Name} · {Range}" + (Phase != null ? $" · {Phase}" : "") +
                  (Rows.Count == 0 ? "\nNothing went wrong here." : $"\n{string.Join(", ", kinds)}" + (HasRoot ? "\nHolds the root cause." : "")) +
                  (Rows.Count > 0 ? "\n\nClick to show or hide its incidents." : "");
    }
}

/// <summary>A player reference shown as a chip: job icon, label and tooltip.</summary>
public readonly record struct Culprit(byte Job, string Label, string Tooltip);

/// <summary>Pre-formatted incident.</summary>
public sealed class IncidentRow
{
    private readonly WipeCard card;
    private string? recap;
    private string? chatLine;

    internal IncidentRow(WipeCard card, Incident inc)
    {
        this.card = card;
        Inc = inc;
        Time = Timeline.Fmt(inc.T);
        Kind = Theme.KindLabel(inc.Kind);
        Color = Theme.Severity(inc);
        Title = inc.Title;
        Mechanic = inc.Mechanic;
        Detail = inc.Detail;
        DetailShort = WipeCard.FirstSentence(inc.Detail, out var more);
        HasMore = more;
        foreach (var p in inc.Players.Distinct())
            Culprits.Add(new Culprit(p.Job, $"{card.Slot(p)} {card.Name(p)}", $"{card.Slot(p)} · {Jobs.Abbrev(p.Job)} · {p.Name}"));
        Caption = (inc.IsRootCause ? "ROOT CAUSE · " : "") + Kind.ToUpperInvariant() + (inc.IsRootCause ? $" · {Time}" : "") +
                  (Mechanic != null ? $" · {Mechanic}" : "");
        MapCaption = $"{(inc.IsRootCause ? "Root cause" : Kind)} at {Time}. Rings: where players should have been.";
        Tooltip = $"{Kind} at {Time}" + (Mechanic != null ? $" · {Mechanic}" : "") + (inc.IsRootCause ? " · ROOT CAUSE" : "") +
                  (Detail.Length > 0 ? $"\n{Wrap(Detail, 90)}" : "") + "\n\nClick to select · arrow / double-click to expand";

        foreach (var s in inc.Snapshot.OrderByDescending(s => s.Involved).ThenBy(s => Array.IndexOf(PartySlots.Order, s.Slot)))
            Snapshot.Add(new SnapRow(card, s));

        if (inc.Mitigation is { } check)
        {
            var active = check.Entries.Count(e => e.Status == MitStatus.Active);
            var missing = check.Missing.Select(e => $"{card.Slot(e.Player)} {e.Ability.Name}").ToList();
            MitSummary = $"Mitigation at this hit: {active}/{check.Entries.Count} up" +
                         (missing.Count > 0 ? $" · missing: {string.Join(", ", missing)}" : "");
        }
    }

    public Incident Inc { get; }
    public string Time { get; }
    public string Kind { get; }
    public Vector4 Color { get; }
    public string Title { get; }
    public string? Mechanic { get; }
    public string Detail { get; }
    public string DetailShort { get; }
    public bool HasMore { get; }
    public List<Culprit> Culprits { get; } = [];
    public List<SnapRow> Snapshot { get; } = [];
    public string? MitSummary { get; }
    public string Caption { get; }
    public string MapCaption { get; }
    public string Tooltip { get; }

    /// <summary>
    /// This incident as plain text for the clipboard: what happened, who is at fault, where they should have been and,
    /// for a death, the damage and healing taken in the seconds before it. Built on first use (only when copied).
    /// </summary>
    public string Recap => recap ??= BuildRecap();

    private const int RecapWindowMs = 10000;

    private string BuildRecap()
    {
        var r = card.Report.Pull;
        var sb = new StringBuilder();
        sb.Append("[Raid Replay] ").Append(Kind).Append(" at ").Append(Time);
        if (Mechanic != null)
            sb.Append(" · ").Append(Mechanic);
        sb.Append($" (pull #{r.Summary.Ordinal}").Append(Inc.IsRootCause ? ", root cause)" : ")").AppendLine();
        sb.AppendLine(Title);
        if (Culprits.Count > 0)
            sb.Append("At fault: ").AppendLine(string.Join(", ", Culprits.Select(c => c.Label)));
        if (Detail.Length > 0)
            sb.AppendLine(Detail);
        if (MitSummary != null)
            sb.AppendLine(MitSummary);
        foreach (var s in Snapshot.Where(s => s.Involved && s.Miss.Length > 0))
            sb.Append("· ").Append(s.Label.TrimStart('●', ' ')).Append(": ").Append(s.Miss).Append(" from where they should have been")
              .Append(s.Why.Length > 0 ? $" ({s.Why})" : string.Empty).AppendLine();

        if (Inc.Death is { } d)
        {
            string Who(Actor? a) => a is { IsPlayer: true } ? $" ({card.Name(a)})" : string.Empty;
            var lines = r.Actions.Where(a => a.T <= d.T + 100 && a.T >= d.T - RecapWindowMs)
                         .SelectMany(a => a.Hits.Where(h => h.Target == d.Victim && (h.Damage > 0 || h.Heal > 0))
                                                .Select(h => (h.T, Name: a.Name + Who(a.Source), h.Damage, h.Heal, h.HpBefore, h.MaxHp)))
                         .Concat(r.Ticks.Where(k => k.Target == d.Victim && k.T <= d.T + 100 && k.T >= d.T - RecapWindowMs)
                                  .Select(k => (k.T, Name: (k.IsHeal ? "HoT" : "DoT") + Who(k.Source), Damage: k.IsHeal ? 0 : k.Amount,
                                                Heal: k.IsHeal ? k.Amount : 0, HpBefore: d.Victim.Hp.At(k.T - 1), MaxHp: d.Victim.MaxHp)))
                         .OrderBy(x => x.T).ToList();
            if (lines.Count > 0)
            {
                sb.AppendLine($"Last {RecapWindowMs / 1000}s before {card.Name(d.Victim)} died:");
                foreach (var x in lines.TakeLast(25))
                {
                    sb.Append($"  {(x.T - d.T) / 1000f:0.0}s  ").Append(x.Damage > 0 ? $"-{x.Damage:N0}" : $"+{x.Heal:N0}").Append("  ").Append(x.Name);
                    if (x.MaxHp > 0)
                        sb.Append($"  ({100f * x.HpBefore / x.MaxHp:0}% HP before)");
                    sb.AppendLine();
                }
            }

            var statuses = r.Statuses.Where(s => s.Target == d.Victim && s.Active(d.T - 50)).Select(s => s.Name).Distinct().Take(16).ToList();
            if (statuses.Count > 0)
                sb.Append("Statuses at death: ").AppendLine(string.Join(", ", statuses));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// This incident as one line for the game's chat box: what happened, who is at fault, how far off their spot the
    /// involved players were and the first sentence of the detail. Built on first use.
    /// </summary>
    public string ChatLine => chatLine ??= WipeCard.ForChat(
        $"[Raid Replay] #{card.Report.Pull.Summary.Ordinal} {Time} {Kind}{(Mechanic != null ? $" · {Mechanic}" : "")}" +
        $"{(Inc.IsRootCause ? " (root cause)" : "")}: {ChatBody()}");

    /// <summary>The part of <see cref="ChatLine"/> after its header, which the summary's chat line reuses for the root cause.</summary>
    internal string ChatBody()
    {
        var sb = new StringBuilder(Title);
        if (Culprits.Count > 0)
            sb.Append(" — at fault: ").Append(string.Join(", ", Culprits.Select(c => c.Label)));
        var off = Inc.Snapshot.Where(s => s is { Involved: true, MissDistance: > 1.5f }).OrderByDescending(s => s.MissDistance).Take(3)
                     .Select(s => $"{card.Slot(s.Player)} {s.MissDistance:0.0}y off their spot").ToList();
        if (off.Count > 0)
            sb.Append(" — ").Append(string.Join(", ", off));
        if (DetailShort.Length > 0)
            sb.Append(" — ").Append(DetailShort);
        return sb.ToString();
    }

    // UI-side caches (width-dependent ellipsis of the title).
    internal float EllipsisWidth;
    internal string? Ellipsis;

    internal static string Wrap(string text, int width)
    {
        if (text.Length <= width)
            return text;
        var sb = new StringBuilder(text.Length + 8);
        var line = 0;
        foreach (var word in text.Split(' '))
        {
            if (line > 0 && line + word.Length + 1 > width)
            {
                sb.Append('\n');
                line = 0;
            }
            else if (line > 0)
            {
                sb.Append(' ');
                line++;
            }

            sb.Append(word);
            line += word.Length;
        }

        return sb.ToString();
    }
}

/// <summary>Pre-formatted snapshot-table row.</summary>
public sealed class SnapRow
{
    internal SnapRow(WipeCard card, PlayerSnapshot s)
    {
        Job = s.Player.Job;
        Involved = s.Involved;
        Alive = s.Alive;
        Label = $"{(s.Involved ? "● " : "")}{(s.Slot.Length > 0 ? s.Slot : Jobs.Abbrev(s.Player.Job))} {card.Name(s.Player)}{(s.Alive ? "" : " (dead)")}";
        Color = !s.Alive ? Theme.TextDim : s.Involved ? new Vector4(1, 0.6f, 0.5f, 1) : Theme.Text;
        Hp = s.HpPct >= 0 ? $"{s.HpPct:0}%" : "–";
        Dir = ReportView.Compass(s.Heading);
        Pos = $"{s.Pos.X:0.0}, {s.Pos.Y:0.0}";
        var tip = new StringBuilder();
        tip.Append($"{s.Slot} · {Jobs.Abbrev(s.Player.Job)} · {s.Player.Name}");
        tip.Append($"\nHP {Hp} · facing {Dir} · at ({Pos})");
        if (s.Expected is { } e)
        {
            var d = e - s.Pos;
            var dir = d.LengthSquared() > 0.01f ? ReportView.Compass(MathF.Atan2(d.X, d.Y)) : "";
            Miss = $"{s.MissDistance:0.0}y {dir}".TrimEnd();
            MissColor = Palette.ForExpected(s.ExpectedSource);
            Why = s.ExpectedNote ?? s.ExpectedSource switch
            {
                ExpectedSource.Learned => "usual spot",
                ExpectedSource.Soak => "soak position",
                ExpectedSource.SafeSpot => "nearest safe spot",
                ExpectedSource.Assigned => "assigned spot",
                _ => "",
            };
            tip.Append($"\nShould have been at ({e.X:0.0}, {e.Y:0.0}): {s.MissDistance:0.0}y {dir} of where they stood ({Why})");
        }

        if (s.Involved)
            tip.Append("\nInvolved in this incident");
        if (!s.Alive)
            tip.Append("\nDead at this moment");
        Tooltip = tip.ToString();
    }

    public byte Job { get; }
    public bool Involved { get; }
    public bool Alive { get; }
    public string Label { get; }
    public Vector4 Color { get; }
    public string Hp { get; }
    public string Dir { get; }
    public string Pos { get; }
    public string Miss { get; } = string.Empty;
    public Vector4 MissColor { get; }
    public string Why { get; } = string.Empty;
    public string Tooltip { get; }
}

/// <summary>Pre-formatted mitigation-plan row (a mechanic header or one planned cooldown).</summary>
public sealed class MitRow
{
    public bool Header { get; init; }
    public bool Active { get; init; }
    public byte Job { get; init; }
    public string Mechanic { get; init; } = string.Empty;
    public string Player { get; init; } = string.Empty;
    public string Planned { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public Vector4 Color { get; init; }
}
