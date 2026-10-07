using System.Text.Json.Nodes;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Export;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Feedback;

/// <summary>What a feedback report says is wrong.</summary>
public enum FeedbackCategory
{
    WrongCulprit,
    WrongRootCause,
    MissedMistake,
    WrongSpot,
    Other,
}

/// <summary>
/// Builds the JSON body of a feedback report: the columns of the server's <c>rr_feedback</c> table
/// (server/rr_feedback.sql). Player names never leave the machine: in every text they become party slots (MT, H1, …),
/// and the attached log excerpt uses Player1..8, which <c>report.players</c> maps back to slots.
/// </summary>
public static class FeedbackReport
{
    public const int MaxNote = 4000;

    /// <summary>The server's limit on the base64 log (7 MB); a larger log is left out.</summary>
    public const int MaxLogBase64 = 7 * 1024 * 1024;

    public static string Key(FeedbackCategory c) => c switch
    {
        FeedbackCategory.WrongCulprit => "wrong_culprit",
        FeedbackCategory.WrongRootCause => "wrong_root_cause",
        FeedbackCategory.MissedMistake => "missed_mistake",
        FeedbackCategory.WrongSpot => "wrong_spot",
        _ => "other",
    };

    /// <param name="report">The analyzed pull.</param>
    /// <param name="incident">The incident the user is reporting on, or null for the report as a whole.</param>
    /// <param name="excerpt">The attached log's name map, when a log is attached.</param>
    /// <param name="logGzBase64">Base64 of the gzipped excerpt, or null.</param>
    public static JsonObject Build(WipeReport report, Incident? incident, FeedbackCategory category, string note,
                                   string pluginVersion, Guid installId, string? pack, ExcerptInfo? excerpt, string? logGzBase64)
    {
        var r = report.Pull;
        var s = r.Summary;
        var names = new Names(report);
        var incidents = report.Incidents;

        var players = new JsonArray();
        foreach (var p in r.Party)
        {
            players.Add(new JsonObject
            {
                ["slot"] = names.Label(p),
                ["job"] = Jobs.Abbrev(p.Job),
                ["log"] = excerpt?.LogNames.GetValueOrDefault(p.Id),
            });
        }

        var body = new JsonObject
        {
            ["install_id"] = installId.ToString(),
            ["plugin_version"] = Cap(pluginVersion, 32),
            ["pack"] = pack is { Length: > 0 } ? Cap(pack, 64) : null,
            ["category"] = Key(category),
            ["note"] = Cap(note.Trim(), MaxNote),
            ["pull"] = new JsonObject
            {
                ["zone"] = s.ZoneName,
                ["territory"] = s.ZoneId,
                ["encounter"] = r.Encounter?.Def.Name,
                ["ordinal"] = s.Ordinal,
                ["durationMs"] = s.DurationMs,
                ["outcome"] = s.Outcome.ToString(),
                ["phase"] = report.Phase,
                ["segment"] = report.Segment,
                ["bossHpPct"] = s.BossHpPct >= 0 ? Math.Round(s.BossHpPct, 1) : null,
                ["deaths"] = incidents.Count(i => i.Kind is IncidentKind.Death or IncidentKind.FellOff),
                ["verdict"] = names.Scrub(report.Verdict),
            },
            ["incident"] = incident != null ? Incident(incident, incidents.IndexOf(incident), names) : null,
            ["report"] = new JsonObject
            {
                ["verdict"] = names.Scrub(report.Verdict),
                ["headline"] = names.Scrub(report.Headline),
                ["phase"] = report.Phase,
                ["segment"] = report.Segment,
                ["rootCause"] = report.RootCause != null ? incidents.IndexOf(report.RootCause) : null,
                ["learnedPulls"] = report.LearnedPulls,
                ["players"] = players,
                ["incidents"] = new JsonArray(incidents.Select((i, n) => (JsonNode?)Incident(i, n, names)).ToArray()),
                ["notes"] = new JsonArray(report.Notes.Select(n => (JsonNode?)names.Scrub(n)).ToArray()),
            },
            ["log_gz_base64"] = logGzBase64 is { Length: > 0 and <= MaxLogBase64 } ? logGzBase64 : null,
        };
        return body;
    }

    private static JsonObject Incident(Incident i, int index, Names names) => new()
    {
        ["index"] = index,
        ["kind"] = i.Kind.ToString(),
        ["t"] = i.T,
        ["time"] = Time(i.T),
        ["mechanic"] = i.Mechanic,
        ["title"] = names.Scrub(i.Title),
        ["detail"] = names.Scrub(i.Detail),
        ["atFault"] = new JsonArray(i.Players.Distinct().Select(p => (JsonNode?)names.Label(p)).ToArray()),
        ["victim"] = i.Victim != null ? names.Label(i.Victim) : null,
        ["severity"] = i.Severity,
        ["root"] = i.IsRootCause,
        ["verdictHint"] = i.VerdictHint,
        ["intentional"] = i.Intentional,
        ["deliberateWipe"] = i.DeliberateWipe,
        ["misses"] = new JsonArray(i.Snapshot.Where(p => p is { Involved: true, MissDistance: > 0.5f })
                                    .Select(p => (JsonNode?)new JsonObject
                                    {
                                        ["slot"] = names.Label(p.Player),
                                        ["missY"] = Math.Round(p.MissDistance!.Value, 1),
                                        ["source"] = p.ExpectedSource.ToString(),
                                        ["note"] = p.ExpectedNote != null ? names.Scrub(p.ExpectedNote) : null,
                                    }).ToArray()),
    };

    /// <summary>Pull time as m:ss.f.</summary>
    private static string Time(int ms) => $"{(ms < 0 ? "-" : "")}{Math.Abs(ms) / 60000}:{Math.Abs(ms) % 60000 / 1000.0:00.0}";

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Replaces player names with party slots (or job abbreviations for players outside the party).</summary>
    private sealed class Names
    {
        private readonly WipeReport report;
        private readonly List<(string Name, string Label)> replacements;

        public Names(WipeReport report)
        {
            this.report = report;
            replacements = report.Pull.Actors.Where(a => a.IsPlayer && a.Name.Length > 0)
                                 .Select(a => (a.Name, Label(a)))
                                 .DistinctBy(x => x.Name)
                                 .OrderByDescending(x => x.Name.Length)
                                 .ToList();
        }

        public string Label(Actor a) => report.SlotOf(a) is { Length: > 0 } slot ? slot : Jobs.Abbrev(a.Job);

        public string Scrub(string text)
        {
            foreach (var (name, label) in replacements)
                text = text.Replace(name, label, StringComparison.Ordinal);
            return text;
        }
    }
}
