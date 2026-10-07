namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>
    /// rr statuses &lt;file&gt; &lt;n&gt; [--from s] [--to s] [--all]: status gains on players (or everyone with --all) in a window,
    /// grouped by status: id, name, source, how many players, duration, first/last time. Plus map effects and directors.
    /// </summary>
    private static int Statuses(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var from = (int)(o.Double("from", -30) * 1000);
        var to = (int)(o.Double("to", 100000) * 1000);
        var groups = r.Statuses.Where(s => (o.Has("all") || s.Target.IsPlayer) && s.StartMs >= from && s.StartMs <= to)
                      .GroupBy(s => (s.StatusId, s.Name, Source: s.Source?.Name ?? "-"))
                      .OrderBy(g => g.Min(s => s.StartMs));
        Console.WriteLine("statuses (id name | source | targets | duration | first..last):");
        foreach (var g in groups)
        {
            var targets = g.Select(s => s.Target).Distinct().Count();
            var dur = g.Select(s => s.Duration).Where(d => d > 0).DefaultIfEmpty(0).Max();
            Console.WriteLine($"  {g.Key.StatusId,5:X4} {g.Key.Name,-28} | {g.Key.Source,-22} | x{g.Count(),-3} on {targets} | {dur,5:0.#}s | " +
                              $"{FormatDuration(g.Min(s => s.StartMs))}..{FormatDuration(g.Max(s => s.StartMs))}");
        }

        Console.WriteLine("map effects:");
        foreach (var m in r.MapEffects.Where(m => m.T >= from && m.T <= to))
            Console.WriteLine($"  {FormatDuration(m.T)} instance={m.Instance:X8} flags={m.Flags:X8} loc={m.Location:X2}");
        Console.WriteLine("directors:");
        foreach (var d in r.Directors.Where(d => d.T >= from && d.T <= to))
            Console.WriteLine($"  {FormatDuration(d.T)} {d.Command:X8} {d.P1:X}");
        return 0;
    }
}

internal static partial class CliApp
{
    /// <summary>rr status-detail &lt;file&gt; &lt;n&gt; &lt;name&gt;: every interval of a status (by name) with target, start, end and the heals landing on the target around its end.</summary>
    private static int StatusDetail(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var name = o.Arg(2, "status name");
        foreach (var s in r.Statuses.Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.StartMs))
        {
            Console.WriteLine($"{s.Name} on {s.Target.Name}: {FormatDuration(s.StartMs)}..{FormatDuration(s.EndMs)} (dur {s.Duration:0.#}s{(s.Removed ? ", removed" : "")})");
            foreach (var a in r.Actions.Where(a => a.T >= s.EndMs - 1500 && a.T <= s.EndMs + 300))
            {
                foreach (var h in a.Hits.Where(h => h.Target == s.Target && (h.Heal > 0 || h.Damage > 0)))
                    Console.WriteLine($"    {FormatDuration(a.T)} {a.Source.Name}: {a.Name} {(h.Heal > 0 ? $"heal {h.Heal:N0}" : $"dmg {h.Damage:N0}")} hp {h.HpBefore:N0}/{h.MaxHp:N0}");
            }
        }

        return 0;
    }
}
