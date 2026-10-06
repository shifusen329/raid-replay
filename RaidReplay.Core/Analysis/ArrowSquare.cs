using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

public enum ArrowFault : byte
{
    None,

    /// <summary>An arrow was dropped on top of another one: both vanished.</summary>
    Stacked,

    /// <summary>Someone stepped on an arrow before the Confused players needed it.</summary>
    SetOffEarly,

    /// <summary>The next arrow was too far from where the previous one lands.</summary>
    Misplaced,

    /// <summary>An arrow sat on a spot that needs another direction and threw the Confused player off the square.</summary>
    WrongArrow,

    /// <summary>No arrow where the chain lands.</summary>
    Missing,

    /// <summary>The Confused player never stepped on an arrow.</summary>
    NoArrowReached,

    /// <summary>Arrows left over after every chain ended.</summary>
    Unused,
}

/// <summary>A spot of the intended layout: where an arrow pointing <see cref="Heading"/> belongs.</summary>
public sealed class ArrowSpot
{
    public Vector2 Pos { get; init; }
    public float Heading { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Part of the equivalent corner variant (the corner arrow placed one step inside) rather than the plain square.</summary>
    public bool Variant { get; init; }
}

/// <summary>An arrow debuff expiring on a player (which should drop a teleporter where they stand).</summary>
public sealed class ArrowDrop
{
    public required Actor Player { get; init; }
    public int T { get; init; }
    public Vector2 Pos { get; init; }
    public float? Heading { get; set; }

    /// <summary>The player died with the debuff, so it never dropped.</summary>
    public bool Dead { get; init; }

    public Teleporter? Teleporter { get; set; }
    public bool Failed => Teleporter == null && !Dead;
    public PlacedArrow? Arrow { get; set; }

    /// <summary>Arrows that vanished when this one was dropped on top of them.</summary>
    public List<Teleporter> Consumed { get; } = [];

    /// <summary>Other failed drops at the same moment and place (dropped on top of each other).</summary>
    public List<ArrowDrop> Collided { get; } = [];
}

public sealed class Teleporter
{
    public required Actor Actor { get; init; }
    public Vector2 Pos { get; init; }
    public float Heading { get; init; }
    public int SpawnMs { get; init; }
    public int DespawnMs { get; init; }
    public ArrowDrop? Drop { get; set; }
    public Actor? FallbackOwner { get; set; }
    public Actor? Owner => Drop?.Player ?? FallbackOwner;
    public PlacedArrow? Arrow { get; set; }

    public ArrowDrop? ConsumedBy { get; set; }
    public Actor? SetOffBy { get; set; }
    public bool Available { get; set; }
    public ConfusedRoute? UsedBy { get; set; }
    public int UsedMs { get; set; } = -1;
}

/// <summary>One arrow (dropped, lost or never dropped) and the layout spot it was meant for.</summary>
public sealed class PlacedArrow
{
    public Actor? Owner { get; init; }
    public Vector2 Pos { get; init; }
    public float Heading { get; init; }
    public int T { get; init; }
    public ArrowDrop? Drop { get; init; }
    public Teleporter? Teleporter { get; init; }
    public ArrowSpot? Meant { get; set; }
    public float Off => Meant != null ? Vector2.Distance(Meant.Pos, Pos) : float.MaxValue;
}

/// <summary>One mistake in the arrow puzzle and who it is attributed to.</summary>
public sealed class ArrowFinding
{
    public ArrowFault Fault { get; init; }
    public int T { get; init; }
    public Vector2 Where { get; init; }
    public string Text { get; set; } = string.Empty;
    public List<Actor> Culprits { get; } = [];

    /// <summary>Where each culprit should have put their arrow (and when they dropped it).</summary>
    public Dictionary<Actor, (Vector2 Pos, int T, string Note)> Expected { get; } = new();

    /// <summary>The earlier mistake this one comes down to (e.g. a chain broke because that arrow had been stacked on).</summary>
    public ArrowFinding? Cause { get; set; }
}

public sealed class ConfusedRoute
{
    public required Actor Player { get; init; }
    public int StartMs { get; init; }
    public int EndMs { get; init; }
    public Vector2 StartPos { get; init; }
    public Actor? NearestAtStart { get; init; }
    public float NearestDist { get; init; }

    /// <summary>Arrows they came close to whose landing spot they then showed up at, in time order.</summary>
    public List<(Teleporter Tp, int T)> EntryCandidates { get; } = [];

    public Teleporter? Entry { get; set; }
    public int EntryMs { get; set; } = -1;
    public List<Teleporter> Used { get; } = [];
    public Vector2 EndPos { get; set; }
    public int EndHopMs { get; set; } = -1;

    /// <summary>A full chain, or one that ended on an arrow someone had already used (the intended end).</summary>
    public bool Complete { get; set; }

    public ArrowFinding? Break { get; set; }
}

public sealed class ArrowSquareResult
{
    public required ArrowSquareDef Def { get; init; }

    /// <summary>The intended layout this group used (plain square, or with some corners in the inside variant).</summary>
    public List<ArrowSpot> Layout { get; } = [];

    public List<Teleporter> Teleporters { get; } = [];
    public List<ArrowDrop> Drops { get; } = [];
    public List<PlacedArrow> Arrows { get; } = [];
    public List<ConfusedRoute> Routes { get; } = [];

    /// <summary>Stacked / set-off-early / unused arrows (chain breaks are on their route).</summary>
    public List<ArrowFinding> Findings { get; } = [];

    public int ConfusedStartMs { get; set; } = -1;
    public int ConfusedEndMs { get; set; } = -1;

    public IEnumerable<Teleporter> Unused => Teleporters.Where(t => t.Available && t.UsedBy == null);

    /// <summary>Every arrow dropped and every arrow used by a Confused player.</summary>
    public bool Solved => Routes.Count > 0 && Drops.Count > 0 && Drops.All(d => d.Teleporter is { UsedBy: not null });

    public IEnumerable<ArrowFinding> AllFindings =>
        Findings.Where(f => f.Fault != ArrowFault.Unused)
                .Concat(Routes.Where(r => r.Break != null).Select(r => r.Break!))
                .Concat(Findings.Where(f => f.Fault == ArrowFault.Unused));

    public bool OnSpot(PlacedArrow a) => a.Off <= Def.Tolerance;
}

/// <summary>
/// Reconstructs an arrow-teleporter puzzle: which arrow each player dropped (and which ones vanished because they were
/// dropped on top of each other or stepped on early), then follows every Confused player through the arrows actually on
/// the ground. Every arrow is matched to the layout spot it was meant for, so when a chain breaks (or two arrows wipe
/// each other out) the fault goes to whoever's arrow was out of place — not to whoever happened to trip over it.
/// </summary>
public static class ArrowSquare
{
    public static ArrowSquareResult? Evaluate(PullReplay r)
    {
        var def = r.Encounter?.Def.ArrowPuzzle;
        if (def == null)
            return null;
        var result = new ArrowSquareResult { Def = def };
        foreach (var a in r.Actors.Where(a => a.BNpcBaseId == def.TeleporterEobj && a.SpawnMs <= r.EndMs).OrderBy(a => a.SpawnMs))
        {
            if (a.Track.TrySample(a.SpawnMs + 50, out var pos, out var h))
                result.Teleporters.Add(new Teleporter { Actor = a, Pos = pos, Heading = h, SpawnMs = a.SpawnMs, DespawnMs = a.DespawnMs });
        }

        CollectDrops(r, def, result);
        if (result.Teleporters.Count == 0 && result.Drops.Count(d => !d.Dead) == 0)
            return null;
        MatchLayout(def, result);
        FindStacks(result);

        var firstDrop = result.Drops.Count > 0 ? result.Drops.Min(d => d.T) : result.Teleporters.Min(t => t.SpawnMs);
        var confused = r.Statuses.Where(s => s.Target.IsPlayer && s.Name.Equals(def.ConfusedStatus, StringComparison.OrdinalIgnoreCase) &&
                                             s.StartMs >= firstDrop && s.StartMs <= firstDrop + 60000)
                        .GroupBy(s => s.Target).Select(g => g.OrderBy(s => s.StartMs).First()).ToList();
        if (confused.Count > 0)
        {
            result.ConfusedStartMs = confused.Min(c => c.StartMs);
            result.ConfusedEndMs = Math.Min(confused.Max(c => c.EndMs), r.LastMs);
            FindEarlyUse(r, result);
            foreach (var t in result.Teleporters)
                t.Available = t.SpawnMs <= result.ConfusedStartMs && t.DespawnMs >= result.ConfusedStartMs - 200 && t.ConsumedBy == null && t.SetOffBy == null;
            RunChains(r, def, result, confused);
            FindUnused(r, result);
        }

        return result;
    }

    // ---- drops, layout, stacking, early use ----------------------------------------------------------------

    private static void CollectDrops(PullReplay r, ArrowSquareDef def, ArrowSquareResult result)
    {
        var dirs = new Dictionary<uint, float>();
        foreach (var (id, d) in def.ArrowDirections)
        {
            if (HexId.TryParse(id, out var v) && DirHeading(d) is { } h)
                dirs[v] = h;
        }

        foreach (var s in r.Statuses.Where(s => s.Target.IsPlayer && s.Name.StartsWith(def.ArrowStatus, StringComparison.OrdinalIgnoreCase) &&
                                                s.EndMs <= r.LastMs))
        {
            if (!s.Target.Track.TrySample(s.EndMs, out var at, out _))
                continue;
            result.Drops.Add(new ArrowDrop
            {
                Player = s.Target, T = s.EndMs, Pos = at, Heading = dirs.TryGetValue(s.StatusId, out var h) ? h : null,
                Dead = !ShapeValidator.IsAlive(r, s.Target, s.EndMs - 1),
            });
        }

        result.Drops.Sort((a, b) => a.T.CompareTo(b.T));

        // Each teleporter appears under its dropper ~0.7s after the debuff expires (closest pairs first).
        var pairs = (from d in result.Drops
                     where !d.Dead
                     from t in result.Teleporters
                     where t.SpawnMs >= d.T - 500 && t.SpawnMs <= d.T + 2000
                     let dist = Vector2.Distance(d.Pos, t.Pos)
                     where dist <= 4.5f
                     orderby dist
                     select (d, t)).ToList();
        foreach (var (d, t) in pairs)
        {
            if (d.Teleporter != null || t.Drop != null)
                continue;
            d.Teleporter = t;
            t.Drop = d;
            d.Heading ??= t.Heading;
        }

        foreach (var t in result.Teleporters.Where(t => t.Drop == null))
        {
            t.FallbackOwner = r.Party.Select(p => (p, d: p.Track.TrySample(t.SpawnMs, out var pp, out _) ? Vector2.Distance(pp, t.Pos) : float.MaxValue))
                               .Where(x => x.d <= 4).MinBy(x => x.d).p;
        }

        foreach (var d in result.Drops.Where(d => d.Heading != null))
        {
            var t = d.Teleporter;
            d.Arrow = new PlacedArrow { Owner = d.Player, Pos = t?.Pos ?? d.Pos, Heading = t?.Heading ?? d.Heading!.Value, T = d.T, Drop = d, Teleporter = t };
            if (t != null)
                t.Arrow = d.Arrow;
            result.Arrows.Add(d.Arrow);
        }

        foreach (var t in result.Teleporters.Where(t => t.Arrow == null))
        {
            t.Arrow = new PlacedArrow { Owner = t.Owner, Pos = t.Pos, Heading = t.Heading, T = t.SpawnMs, Teleporter = t };
            result.Arrows.Add(t.Arrow);
        }
    }

    /// <summary>
    /// Picks the layout (plain square, or any corners in the inside variant) that best fits where the arrows were put,
    /// and matches every arrow to the spot of its direction it was meant for (least total distance).
    /// </summary>
    private static void MatchLayout(ArrowSquareDef def, ArrowSquareResult result)
    {
        var (plain, corners) = BuildSpots(def);
        var maxCost = 2.5f * def.Step;
        (float Cost, List<ArrowSpot> Layout, Dictionary<PlacedArrow, ArrowSpot> Match)? best = null;
        for (var mask = 0; mask < 1 << corners.Count; mask++)
        {
            var layout = new List<ArrowSpot>(plain);
            for (var c = 0; c < corners.Count; c++)
            {
                if ((mask & (1 << c)) == 0)
                    continue;
                var (before, corner, turning, inside) = corners[c];
                layout[layout.IndexOf(before)] = turning;
                layout[layout.IndexOf(corner)] = inside;
            }

            // Dead men drop no arrows: their position says nothing about where the arrow would have gone.
            var match = new Dictionary<PlacedArrow, ArrowSpot>();
            var cost = 0.01f * BitCount(mask);
            foreach (var group in result.Arrows.Where(a => a.Drop is not { Dead: true }).GroupBy(a => Quadrant(a.Heading)))
            {
                var arrows = group.ToList();
                var spots = layout.Where(s => Quadrant(s.Heading) == group.Key).ToList();
                cost += Assign(arrows, spots, maxCost, match);
            }

            if (best == null || cost < best.Value.Cost)
                best = (cost, layout, match);
        }

        result.Layout.AddRange(best!.Value.Layout);
        foreach (var (a, s) in best.Value.Match)
            a.Meant = s;

        // A dead player's arrow was meant for a spot of its direction that nobody else filled.
        foreach (var a in result.Arrows.Where(a => a.Drop is { Dead: true }))
        {
            a.Meant = result.Layout.Where(s => Quadrant(s.Heading) == Quadrant(a.Heading) && result.Arrows.All(x => x.Meant != s))
                            .MinBy(s => Vector2.Distance(s.Pos, a.Pos));
        }
    }

    /// <summary>Exact least-cost matching of a handful of arrows to the spots of their direction (unmatched arrows cost <paramref name="maxCost"/>).</summary>
    private static float Assign(List<PlacedArrow> arrows, List<ArrowSpot> spots, float maxCost, Dictionary<PlacedArrow, ArrowSpot> match)
    {
        var bestCost = float.MaxValue;
        var bestPick = new int[arrows.Count];
        var pick = new int[arrows.Count];
        var taken = new bool[spots.Count];

        void Search(int i, float cost)
        {
            if (cost >= bestCost)
                return;
            if (i == arrows.Count)
            {
                bestCost = cost;
                pick.CopyTo(bestPick, 0);
                return;
            }

            for (var j = 0; j < spots.Count; j++)
            {
                if (taken[j])
                    continue;
                taken[j] = true;
                pick[i] = j;
                Search(i + 1, cost + Math.Min(maxCost, Vector2.Distance(arrows[i].Pos, spots[j].Pos)));
                taken[j] = false;
            }

            pick[i] = -1;
            Search(i + 1, cost + maxCost + 0.5f);
        }

        Search(0, 0);
        for (var i = 0; i < arrows.Count; i++)
        {
            if (bestPick[i] >= 0)
                match[arrows[i]] = spots[bestPick[i]];
        }

        return bestCost;
    }

    /// <summary>
    /// A drop that produced no teleporter while a nearby one vanished at that moment landed on top of it: both are gone.
    /// Two drops on top of each other at the same moment both fail.
    /// </summary>
    private static void FindStacks(ArrowSquareResult result)
    {
        var failed = result.Drops.Where(d => d.Failed).ToList();
        var pairs = (from d in failed
                     from t in result.Teleporters
                     where t.SpawnMs < d.T + 300 && t.DespawnMs >= d.T - 300 && t.DespawnMs <= d.T + 2000
                     let dist = Vector2.Distance(d.Pos, t.Pos)
                     where dist <= 7
                     orderby dist
                     select (d, t)).ToList();
        foreach (var (d, t) in pairs)
        {
            if (t.ConsumedBy != null)
                continue;
            t.ConsumedBy = d;
            d.Consumed.Add(t);
        }

        foreach (var a in failed)
        {
            foreach (var b in failed)
            {
                if (a != b && Math.Abs(a.T - b.T) <= 400 && Vector2.Distance(a.Pos, b.Pos) <= 5)
                    a.Collided.Add(b);
            }
        }

        var seen = new HashSet<ArrowDrop>();
        foreach (var d in failed)
        {
            if (!seen.Add(d))
                continue;
            var group = new List<ArrowDrop> { d };
            for (var i = 0; i < group.Count; i++)
            {
                foreach (var c in group[i].Collided.Where(seen.Add))
                    group.Add(c);
            }

            result.Findings.Add(StackFinding(result, group));
        }
    }

    private static ArrowFinding StackFinding(ArrowSquareResult result, List<ArrowDrop> group)
    {
        var consumed = group.SelectMany(g => g.Consumed).Distinct().ToList();
        var t0 = group.Max(g => g.T);
        if (consumed.Count == 0 && group.Count == 1)
        {
            // Nothing vanished with it: the arrow just never appeared. Report without blame.
            var lone = group[0];
            return new ArrowFinding
            {
                Fault = ArrowFault.Missing, T = lone.T, Where = lone.Pos,
                Text = $"{lone.Player.Name}'s {DirName(lone.Heading)}arrow at {P(lone.Pos)} never appeared",
            };
        }

        // Judge every arrow involved against the spot it was meant for: the one(s) out of place get the blame, even if
        // a correctly placed arrow was the one dropped on top.
        var members = group.Select(g => g.Arrow).Concat(consumed.Select(t => t.Arrow)).Where(a => a != null).Select(a => a!).ToList();
        var wrong = members.Where(a => !result.OnSpot(a) && a.Owner != null).ToList();
        var owners = members.Select(a => a.Owner).Where(o => o != null).Distinct().ToList();
        var f = new ArrowFinding { Fault = ArrowFault.Stacked, T = t0, Where = group[0].Pos };
        var gap = members.Count > 1 ? members.Skip(1).Min(a => Vector2.Distance(a.Pos, members[0].Pos)) : 0;
        var listed = string.Join(" and ", members.Select(a => $"{Possessive(a.Owner)} {DirName(a.Heading)}arrow at {P(a.Pos)}"));

        List<PlacedArrow> blamed;
        string text;
        if (wrong.Count > 0 && wrong.Count < members.Count)
        {
            blamed = wrong;
            var right = members.Except(wrong).Select(a => $"{Possessive(a.Owner)} {DirName(a.Heading)}arrow on {a.Meant!.Name}").ToList();
            text = $"{listed} overlapped ({gap:0.0}y apart) and vanished. " +
                   $"{string.Join("; ", wrong.Select(w => $"{Possessive(w.Owner)} {DirName(w.Heading)}arrow was out of place ({OffText(result, w)})"))}" +
                   (right.Count > 0 ? $"; {string.Join(" and ", right)} {(right.Count == 1 ? "was" : "were")} where it belonged" : "");
        }
        else if (wrong.Count > 0)
        {
            blamed = wrong;
            text = $"{listed} overlapped ({gap:0.0}y apart) and vanished; none was on its spot " +
                   $"({string.Join("; ", wrong.Select(w => $"{Possessive(w.Owner)}: {OffText(result, w)}"))})";
        }
        else
        {
            // Everything looks on its spot (position noise): the later drop is what removed them.
            blamed = group.Select(g => g.Arrow).Where(a => a != null).Select(a => a!).ToList();
            text = $"{listed} overlapped ({gap:0.0}y apart) and vanished; {string.Join(" and ", group.Select(g => g.Player.Name).Distinct())} dropped last";
        }

        if (owners.Count == 1)
            text = $"{owners[0]!.Name} dropped an arrow on top of their own ({gap:0.0}y apart) — both vanished ({OffText(result, group[0].Arrow)})";
        f.Text = text;
        foreach (var a in blamed.Where(a => a.Owner != null).DistinctBy(a => a.Owner))
            Blame(f, a.Owner!, a);
        return f;
    }

    private static void FindEarlyUse(PullReplay r, ArrowSquareResult result)
    {
        var def = result.Def;
        var early = new List<(Teleporter Tp, ArrowFinding F, Actor? User)>();
        foreach (var t in result.Teleporters.Where(t => t.ConsumedBy == null && t.DespawnMs < result.ConfusedStartMs - 200 && t.DespawnMs > t.SpawnMs))
        {
            // Teleporters vanish ~2s after they are used: the user is whoever crossed it shortly before.
            (Actor? P, int T) user = (null, t.DespawnMs);
            foreach (var p in r.Party)
            {
                for (var ms = t.DespawnMs - 3500; ms <= t.DespawnMs; ms += 100)
                {
                    if (ms >= user.T)
                        break;
                    if (ShapeValidator.IsAlive(r, p, ms) && p.Track.TrySample(ms, out var pp, out _) && Vector2.Distance(pp, t.Pos) <= def.TriggerRadius + 0.75f)
                    {
                        user = (p, ms);
                        break;
                    }
                }
            }

            t.SetOffBy = user.P;
            var f = new ArrowFinding { Fault = ArrowFault.SetOffEarly, T = user.T, Where = t.Pos };
            var when = WipeAnalyzer.FormatMs(user.T);
            var whose = $"{Possessive(t.Owner)} {DirName(t.Heading)}arrow at {P(t.Pos)}";

            // Knocked there? The holder and the soakers have fixed spots; whoever was off theirs when the knockback went off
            // sent the push the wrong way.
            var kb = user.P == null
                         ? null
                         : r.Actions.Where(a => a.T >= user.T - 3000 && a.T <= user.T + 300)
                            .SelectMany(a => a.Hits).LastOrDefault(h => h.Target == user.P && h.Knockback);
            var kbAoe = kb != null ? r.Aoes.FirstOrDefault(a => a.Action == kb.Action) : null;
            var spots = kbAoe != null ? StackPositions.For(r, kbAoe) : null;
            if (t.Arrow != null && !result.OnSpot(t.Arrow) && t.Owner != null)
            {
                f.Text = $"{whose} was out of place ({OffText(result, t.Arrow)}); {user.P?.Name ?? "someone"} ran into it at {when} and used it up";
                Blame(f, t.Owner, t.Arrow);
            }
            else if (kb != null && spots != null)
            {
                var holder = spots.Holder;
                var origin = kbAoe!.Placement(kbAoe.ResolveMs).Origin;
                var off = new List<string>();
                foreach (var p in new[] { holder, user.P }.Where(p => p != null).Distinct())
                {
                    var d = spots.Off(p!, kbAoe.ResolveMs);
                    if (d <= spots.Tolerance)
                        continue;
                    f.Culprits.Add(p!);
                    p!.Track.TrySample(kbAoe.ResolveMs, out var at, out _);
                    var role = p == holder ? "holding it" : "taking it";
                    off.Add($"{p.Name} ({role}) was at {P(at)}, {d:0.0}y from their spot {P(spots.SpotOf(p))}");
                    f.Expected[p] = (spots.SpotOf(p), kbAoe.ResolveMs, $"{(p == holder ? "holder" : "soaker")} spot for {kb.Action.Name}");
                }

                var push = $"{kb.Action.Name}{(holder != null ? $" on {holder.Name}" : "")} at {WipeAnalyzer.FormatMs(kbAoe.ResolveMs)} " +
                           $"pushed {user.P!.Name} {Compass(Angles.Toward(origin, user.P.Track.TrySample(kb.T, out var from, out _) ? from : origin))}";
                if (off.Count > 0)
                {
                    f.Text = $"{push} into {whose}, using it up at {when} before the Confused players needed it: {string.Join("; ", off)}";
                }
                else
                {
                    // Everyone was on their spot: the push itself was fine, so they walked into the arrow afterwards.
                    f.Culprits.Add(user.P);
                    f.Text = $"{user.P.Name} stepped on {whose} at {when}, after {push} from the right spots, before the Confused players needed it";
                }
            }
            else
            {
                if (user.P != null)
                    f.Culprits.Add(user.P);
                f.Text = $"{user.P?.Name ?? "Someone"} {(kb != null ? $"was knocked into (by {kb.Action.Name})" : "stepped on")} {whose} at {when}, " +
                         "before the Confused players needed it";
            }

            early.Add((t, f, user.P));
            result.Findings.Add(f);
        }

        // A player sent through several arrows in a row moves faster than positions are logged: an arrow nobody was seen
        // on belongs to the same teleport chain as an early-used arrow one jump before or after it.
        bool Linked(Teleporter a, Teleporter b) =>
            Math.Abs(a.DespawnMs - b.DespawnMs) <= 2500 &&
            (Vector2.Distance(a.Pos + (Angles.Dir(a.Heading) * def.Step), b.Pos) <= def.TriggerRadius + 1 ||
             Vector2.Distance(b.Pos + (Angles.Dir(b.Heading) * def.Step), a.Pos) <= def.TriggerRadius + 1);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (t, f, _) in early.Where(e => e.User == null).ToList())
            {
                var known = early.FirstOrDefault(e => e.User != null && Linked(t, e.Tp));
                if (known.Tp == null)
                    continue;
                var i = early.FindIndex(e => e.Tp == t);
                early[i] = (t, f, known.User);
                t.SetOffBy = known.User;
                f.Culprits.Clear();
                f.Culprits.AddRange(known.F.Culprits);
                foreach (var (k, v) in known.F.Expected)
                    f.Expected[k] = v;
                var root = known.F.Text.Contains(" went in the same teleport chain: ", StringComparison.Ordinal)
                               ? known.F.Text[(known.F.Text.IndexOf(" went in the same teleport chain: ", StringComparison.Ordinal) + 34)..]
                               : known.F.Text;
                f.Text = $"{Possessive(t.Owner)} {DirName(t.Heading)}arrow at {P(t.Pos)} went in the same teleport chain: {root}";
                f.Cause = known.F.Cause ?? known.F;
                changed = true;
            }
        }
    }

    // ---- confused chains -----------------------------------------------------------------------------------

    private static void RunChains(PullReplay r, ArrowSquareDef def, ArrowSquareResult result, List<StatusInterval> confused)
    {
        var available = result.Teleporters.Where(t => t.Available).ToList();
        var queue = new PriorityQueue<(ConfusedRoute Route, Teleporter Tp, bool IsEntry), int>();
        foreach (var c in confused)
        {
            c.Target.Track.TrySample(c.StartMs, out var start, out _);
            var nearest = r.Party.Where(p => p != c.Target && ShapeValidator.IsAlive(r, p, c.StartMs))
                           .Select(p => (p, d: p.Track.TrySample(c.StartMs, out var pp, out _) ? Vector2.Distance(pp, start) : float.MaxValue))
                           .MinBy(x => x.d);
            var route = new ConfusedRoute
            {
                Player = c.Target, StartMs = c.StartMs, EndMs = Math.Min(c.EndMs, r.LastMs), StartPos = start,
                NearestAtStart = nearest.p, NearestDist = nearest.d, EndPos = start,
            };
            route.EntryCandidates.AddRange(EntryCandidates(c.Target, c.StartMs, route.EndMs, available, def));
            result.Routes.Add(route);
            if (route.EntryCandidates.Count > 0)
                queue.Enqueue((route, route.EntryCandidates[0].Tp, true), route.EntryCandidates[0].T);
        }

        // Teleports in time order, so two chains racing for one arrow resolve the way they did in game.
        while (queue.TryDequeue(out var item, out var time))
        {
            var (route, tp, isEntry) = item;
            if (tp.UsedBy != null)
            {
                if (isEntry)
                {
                    // Walked over an arrow someone had already used: their real first arrow is a later one, if any.
                    var later = route.EntryCandidates.FirstOrDefault(e => e.T > time && e.Tp.UsedBy == null);
                    if (later.Tp != null)
                        queue.Enqueue((route, later.Tp, true), later.T);
                    continue;
                }

                route.Complete = true;
                route.EndPos = tp.Pos;
                route.EndHopMs = time;
                continue;
            }

            if (isEntry)
            {
                route.Entry = tp;
                route.EntryMs = time;
            }

            tp.UsedBy = route;
            tp.UsedMs = time;
            route.Used.Add(tp);
            var land = tp.Pos + (Angles.Dir(tp.Heading) * def.Step);
            route.EndPos = land;
            route.EndHopMs = time + def.HopMs;
            if (def.MaxChain > 0 && route.Used.Count >= def.MaxChain)
            {
                // A full chain: whatever is under the landing spot is left for someone else.
                route.Complete = true;
                continue;
            }

            var next = available.Where(a => a.UsedBy == null && Vector2.Distance(a.Pos, land) <= def.TriggerRadius)
                                .MinBy(a => Vector2.Distance(a.Pos, land));
            if (next != null)
            {
                queue.Enqueue((route, next, false), time + def.HopMs);
                continue;
            }

            var closest = available.Where(a => Vector2.Distance(a.Pos, land) <= def.Step * 0.75f).MinBy(a => Vector2.Distance(a.Pos, land));
            route.Complete = closest is { UsedBy: not null } && closest.UsedMs <= time + def.HopMs;
        }

        foreach (var route in result.Routes)
        {
            if (route.Entry == null)
                route.Break = NoArrowReached(r, result, route);
            else if (!route.Complete)
                route.Break = ChainBreak(result, route);
        }
    }

    /// <summary>
    /// Arrows a Confused player may have stepped on first. Positions are coarse (a teleport looks like a fast walk), so
    /// being close is not enough: they must then show up at that arrow's landing spot. Earliest first.
    /// </summary>
    private static IEnumerable<(Teleporter Tp, int T)> EntryCandidates(Actor player, int startMs, int endMs, List<Teleporter> available, ArrowSquareDef def)
    {
        var samples = new List<(int T, Vector2 Pos)>();
        for (var t = startMs; t <= endMs + 500; t += 100)
        {
            if (player.Track.TrySample(t, out var pos, out _))
                samples.Add((t, pos));
        }

        // The first time they come near an arrow and then show up at its landing spot shortly after.
        var found = new List<(Teleporter Tp, int T)>();
        foreach (var tp in available)
        {
            var land = tp.Pos + (Angles.Dir(tp.Heading) * def.Step);
            for (var i = 0; i < samples.Count && samples[i].T <= endMs; i++)
            {
                if (Vector2.Distance(samples[i].Pos, tp.Pos) > def.TriggerRadius + 1.5f)
                    continue;
                var t0 = samples[i].T;
                if (samples.Any(s => s.T >= t0 && s.T <= t0 + 2500 && Vector2.Distance(s.Pos, land) <= 2f))
                {
                    found.Add((tp, t0));
                    break;
                }
            }
        }

        return found.OrderBy(x => x.T);
    }

    private static ArrowFinding NoArrowReached(PullReplay r, ArrowSquareResult result, ConfusedRoute route)
    {
        var f = new ArrowFinding { Fault = ArrowFault.NoArrowReached, T = route.StartMs, Where = route.StartPos };
        f.Culprits.Add(route.Player);
        var nearestArrow = result.Teleporters.Where(t => t.Available).Select(t => Vector2.Distance(t.Pos, route.StartPos)).DefaultIfEmpty(-1).Min();
        var dead = r.Party.Where(p => p != route.Player && !ShapeValidator.IsAlive(r, p, route.StartMs)).Select(p => p.Name).ToList();
        f.Text = $"{route.Player.Name} never stepped on an arrow: started at {P(route.StartPos)}" +
                 (nearestArrow >= 0 ? $", {nearestArrow:0.0}y from the nearest one" : "") +
                 (route.NearestAtStart != null ? $", and walked toward {route.NearestAtStart.Name} ({route.NearestDist:0.0}y, the closest player)" : "") +
                 (dead.Count > 0 ? $" — {string.Join(", ", dead)} {(dead.Count == 1 ? "was" : "were")} dead" : "");
        return f;
    }

    /// <summary>The chain stopped on empty ground: find whose arrow should have been under the landing spot.</summary>
    private static ArrowFinding ChainBreak(ArrowSquareResult result, ConfusedRoute route)
    {
        var def = result.Def;
        var from = route.Used[^1];
        var land = route.EndPos;
        var hops = route.Used.Count;
        var f = new ArrowFinding { Fault = ArrowFault.Missing, T = route.EndHopMs, Where = land };
        var lead = $"{route.Player.Name}'s chain broke after {hops} arrow{(hops == 1 ? "" : "s")}, landing at {P(land)}: ";
        var spot = result.Layout.Where(s => Vector2.Distance(s.Pos, land) <= def.Tolerance + 1.5f).MinBy(s => Vector2.Distance(s.Pos, land));
        var fromArrow = from.Arrow;
        var fromOff = fromArrow != null && !result.OnSpot(fromArrow);

        // Thrown off the layout: the arrow they used was out of place.
        if (spot == null)
        {
            if (fromArrow?.Owner != null)
                Blame(f, fromArrow.Owner, fromArrow);
            return With(f, ArrowFault.Misplaced, lead + FromText(result, from, land));
        }

        var meant = result.Arrows.FirstOrDefault(a => a.Meant == spot);
        if (meant == null)
            return With(f, ArrowFault.Missing, lead + $"no arrow was meant for {spot.Name} {P(spot.Pos)} (nobody had one left for it)");

        // The arrow meant for this spot was lost before the confusion. A death is its own incident, not a placement error.
        if (meant.Drop is { Dead: true } dead)
        {
            return With(f, ArrowFault.Missing, lead + $"the {DirName(meant.Heading)}arrow for {spot.Name} was {Possessive(meant.Owner)}, " +
                                               $"who was dead when it was due ({WipeAnalyzer.FormatMs(dead.T)}), so it never dropped");
        }

        var lost = result.Findings.FirstOrDefault(x => x.Fault is ArrowFault.Stacked or ArrowFault.SetOffEarly or ArrowFault.Missing &&
                                                       (meant.Drop is { Failed: true } d && x.T == d.T && x.Where == d.Pos ||
                                                        meant.Teleporter is { } tp && (tp.ConsumedBy != null && x.Fault == ArrowFault.Stacked && x.T == tp.ConsumedBy.T ||
                                                                                       tp.SetOffBy != null && x.Fault == ArrowFault.SetOffEarly && x.Where == tp.Pos)));
        if (lost == null && meant.Teleporter is { ConsumedBy: { } by })
            lost = result.Findings.FirstOrDefault(x => x.Fault == ArrowFault.Stacked && x.T >= by.T && x.T - by.T <= 400 && Vector2.Distance(x.Where, by.Pos) <= 5);
        if (lost == null && meant.Drop is { Failed: true } md)
            lost = result.Findings.FirstOrDefault(x => x.Fault is ArrowFault.Stacked or ArrowFault.Missing && x.T >= md.T && x.T - md.T <= 400 && Vector2.Distance(x.Where, md.Pos) <= 5);
        if (lost != null)
        {
            foreach (var c in lost.Culprits)
                f.Culprits.Add(c);
            foreach (var (k, v) in lost.Expected)
                f.Expected[k] = v;
            var inherited = With(f, lost.Fault, lead + $"the {DirName(meant.Heading)}arrow for {spot.Name} was gone — {lost.Text}");
            inherited.Cause = lost.Cause ?? lost;
            return inherited;
        }

        // The arrow is on the ground but out of reach: whichever of the two arrows is out of place.
        var parts = new List<string>();
        var meantOff = !result.OnSpot(meant);
        var gap = Vector2.Distance(meant.Pos, land);
        if (meantOff || !fromOff)
        {
            if (meant.Owner != null)
                Blame(f, meant.Owner, meant);
            parts.Add($"the {DirName(meant.Heading)}arrow for {spot.Name} {P(spot.Pos)} was {Possessive(meant.Owner)}, {gap:0.0}y away at {P(meant.Pos)}" +
                      (meant.Teleporter?.UsedBy is { } other && other != route ? $" (used by {other.Player.Name})" : ""));
        }

        if (fromOff)
        {
            if (fromArrow!.Owner != null)
                Blame(f, fromArrow.Owner, fromArrow);
            parts.Add(FromText(result, from, land));
        }

        return With(f, fromOff && !meantOff ? ArrowFault.Misplaced : meantOff ? ArrowFault.Misplaced : ArrowFault.Missing,
                    lead + string.Join("; ", parts) + $" (a teleport only chains within {def.TriggerRadius:0.#}y)");
    }

    /// <summary>Arrows nobody used once every chain ended (the puzzle needs all of them).</summary>
    private static void FindUnused(PullReplay r, ArrowSquareResult result)
    {
        var unused = result.Unused.ToList();
        if (unused.Count == 0 || result.Solved)
            return;
        var explained = result.AllFindings.SelectMany(f => f.Culprits).ToHashSet();
        var f = new ArrowFinding { Fault = ArrowFault.Unused, T = result.ConfusedEndMs, Where = unused[0].Pos };
        var parts = new List<string>();
        foreach (var t in unused)
        {
            var a = t.Arrow;
            var where = a?.Meant != null ? a.Meant.Name : P(t.Pos);
            if (a != null && !result.OnSpot(a) && t.Owner != null)
            {
                parts.Add($"{Possessive(t.Owner)} {DirName(t.Heading)}arrow ({OffText(result, a)})");
                if (!explained.Contains(t.Owner))
                    Blame(f, t.Owner, a);
            }
            else
            {
                parts.Add($"{where} ({Possessive(t.Owner)})");
            }
        }

        var expected = result.Def.MaxChain > 0 ? (int)Math.Ceiling(result.Drops.Count / (double)result.Def.MaxChain) : 0;
        var missingRoutes = expected > result.Routes.Count ? $"; only {result.Routes.Count} of {expected} Confused players were alive to use them" : "";
        f.Text = $"{unused.Count} arrow{(unused.Count == 1 ? "" : "s")} never used: {string.Join(", ", parts)}{missingRoutes}";
        result.Findings.Add(f);
    }

    private static ArrowFinding With(ArrowFinding f, ArrowFault fault, string text)
    {
        var copy = new ArrowFinding { Fault = fault, T = f.T, Where = f.Where, Text = text };
        copy.Culprits.AddRange(f.Culprits);
        foreach (var (k, v) in f.Expected)
            copy.Expected[k] = v;
        return copy;
    }

    private static string FromText(ArrowSquareResult result, Teleporter from, Vector2 land)
    {
        var a = from.Arrow;
        var here = result.Layout.Where(s => Vector2.Distance(s.Pos, from.Pos) <= result.Def.Tolerance).MinBy(s => Vector2.Distance(s.Pos, from.Pos));
        if (here != null && !SameWay(here.Heading, from.Heading))
        {
            return $"{Possessive(from.Owner)} {Compass(from.Heading)} arrow sat on {here.Name}, which needs a {Compass(here.Heading)} arrow" +
                   (a?.Meant != null ? $" (it belonged on {a.Meant.Name} {P(a.Meant.Pos)})" : "") + $", so it threw them to {P(land)}";
        }

        return $"{Possessive(from.Owner)} arrow at {P(from.Pos)} was out of place ({(a != null ? OffText(result, a) : "?")}), so its jump landed at {P(land)}, clear of the next arrow";
    }

    private static void Blame(ArrowFinding f, Actor who, PlacedArrow? arrow)
    {
        if (!f.Culprits.Contains(who))
            f.Culprits.Add(who);
        if (arrow?.Meant is { } spot)
            f.Expected.TryAdd(who, (spot.Pos, arrow.T, $"{DirName(arrow.Heading)}arrow spot {spot.Name}"));
    }

    // ---- layout ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The plain square (from the NW corner: north side, east, south, west; reversed for counter-clockwise), and per
    /// corner its equivalent variant: the arrow before the corner turns early onto a spot one step inside the corner
    /// that points along the old side again (before → inside → the spot after the corner).
    /// </summary>
    private static (List<ArrowSpot> Plain, List<(ArrowSpot Before, ArrowSpot Corner, ArrowSpot Turning, ArrowSpot Inside)> Corners) BuildSpots(ArrowSquareDef def)
    {
        var c = new Vector2(def.Center[0], def.Center[1]);
        var h = def.HalfSize;
        var perSide = Math.Max(1, (int)MathF.Round(2 * h / def.Step));
        var cornersXY = new[] { new Vector2(-h, -h), new Vector2(h, -h), new Vector2(h, h), new Vector2(-h, h) };
        string[] sideNames = ["N", "E", "S", "W"];
        string[] cornerNames = ["NW", "NE", "SE", "SW"];
        var points = new List<(Vector2 P, string Name, bool Corner)>();
        for (var side = 0; side < 4; side++)
        {
            var a = cornersXY[side];
            var b = cornersXY[(side + 1) % 4];
            for (var k = 0; k < perSide; k++)
            {
                var name = k == 0 ? cornerNames[side] : perSide % 2 == 0 && k == perSide / 2 ? sideNames[side] : $"{sideNames[side]}{k}";
                points.Add((c + a + ((b - a) * k / perSide), name, k == 0));
            }
        }

        if (!def.Clockwise)
            points.Reverse();
        var n = points.Count;
        var plain = new List<ArrowSpot>();
        for (var i = 0; i < n; i++)
            plain.Add(new ArrowSpot { Pos = points[i].P, Name = points[i].Name, Heading = Angles.Toward(points[i].P, points[(i + 1) % n].P) });

        var corners = new List<(ArrowSpot, ArrowSpot, ArrowSpot, ArrowSpot)>();
        for (var i = 0; i < n; i++)
        {
            if (!points[i].Corner || n < 3)
                continue;
            var before = plain[(i - 1 + n) % n];
            var corner = plain[i];
            var inside = before.Pos + (Angles.Dir(corner.Heading) * def.Step);
            corners.Add((before, corner,
                         new ArrowSpot { Pos = before.Pos, Heading = corner.Heading, Name = $"{before.Name} (turning)", Variant = true },
                         new ArrowSpot { Pos = inside, Heading = before.Heading, Name = $"inside {corner.Name}", Variant = true }));
        }

        return (plain, corners);
    }

    private static string OffText(ArrowSquareResult result, PlacedArrow? a)
    {
        if (a?.Meant == null)
            return "no spot left for it";
        var here = result.Layout.Where(s => Vector2.Distance(s.Pos, a.Pos) <= result.Def.Tolerance).MinBy(s => Vector2.Distance(s.Pos, a.Pos));
        if (here != null && here != a.Meant)
            return $"on {here.Name}; it belonged on {a.Meant.Name}, {a.Off:0.0}y away";
        var d = a.Pos - new Vector2(result.Def.Center[0], result.Def.Center[1]);
        var inside = result.Def.HalfSize - Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return $"{a.Off:0.0}y from {a.Meant.Name}{(inside > 0.75f && !a.Meant.Variant ? $", {inside:0.0}y inside the square" : inside < -0.75f ? $", {-inside:0.0}y outside the square" : "")}";
    }

    private static int Quadrant(float heading) => ((int)MathF.Round(heading / (MathF.PI / 2)) % 4 + 4) % 4;

    private static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    private static bool SameWay(float a, float b) => MathF.Abs(Angles.Wrap(a - b)) <= MathF.PI / 4;

    private static float? DirHeading(string d) => d switch
    {
        "N" => MathF.PI,
        "S" => 0,
        "E" => MathF.PI / 2,
        "W" => -MathF.PI / 2,
        _ => null,
    };

    private static string DirName(float? heading) => heading is { } h ? $"{Compass(h)} " : string.Empty;

    private static string Possessive(Actor? a) => a != null ? $"{a.Name}'s" : "an unknown player's";

    private static string P(Vector2 v) => $"({v.X:0.0}, {v.Y:0.0})";

    public static string Compass(float heading)
    {
        string[] names = ["S", "SE", "E", "NE", "N", "NW", "W", "SW"];
        var idx = (int)Math.Round(heading / (Math.PI / 4));
        return names[((idx % 8) + 8) % 8];
    }
}
