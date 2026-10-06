using RaidReplay.Core.GameData;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>
/// Standard party slots: MT (T1), OT (T2), H1 (pure healer WHM/AST), H2 (shield healer SCH/SGE), M1, M2 (melee),
/// R1 (physical ranged BRD/MCH/DNC), R2 (caster BLM/SMN/RDM/PCT).
/// </summary>
public static class PartySlots
{
    public static readonly string[] Order = ["MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

    private static readonly HashSet<byte> PureHealers = [24, 33, 6];
    private static readonly HashSet<byte> ShieldHealers = [28, 40];
    private static readonly HashSet<byte> PhysRanged = [23, 31, 38, 5];
    private static readonly HashSet<byte> Casters = [25, 27, 35, 42, 36, 7, 26];

    public static Dictionary<Actor, string> Assign(PullReplay r)
    {
        var slots = new Dictionary<Actor, string>();
        var party = r.Party.ToList();

        // Tanks: MT is the tank the boss auto-attacks most in the first 30 s (tanks swap later); else party order.
        var tanks = party.Where(p => Jobs.Get(p.Job)?.Role == 1).ToList();
        if (tanks.Count > 0)
        {
            var autos = new Dictionary<Actor, int>();
            foreach (var a in r.Actions)
            {
                if (a.T < 0 || a.T > 30000 || a.Source.Kind != ActorKind.Boss || a.Hits.Count != 1)
                    continue;
                var t = a.Hits[0].Target;
                if (tanks.Contains(t) && a.Cast == null)
                    autos[t] = autos.GetValueOrDefault(t) + 1;
            }

            var ordered = tanks.OrderByDescending(t => autos.GetValueOrDefault(t)).ThenBy(party.IndexOf).ToList();
            Fill(slots, ordered, "MT", "OT");
        }

        Fill(slots, party.Where(p => PureHealers.Contains(p.Job)).ToList(), "H1", "H2");
        Fill(slots, party.Where(p => ShieldHealers.Contains(p.Job)).ToList(), "H2", "H1");
        Fill(slots, party.Where(p => Jobs.Get(p.Job)?.Role == 2).ToList(), "M1", "M2");
        Fill(slots, party.Where(p => PhysRanged.Contains(p.Job)).ToList(), "R1", "R2");
        Fill(slots, party.Where(p => Casters.Contains(p.Job)).ToList(), "R2", "R1");

        // Anyone left (unusual comps) takes the first free slot.
        foreach (var p in party.Where(p => !slots.ContainsKey(p)))
        {
            var free = Order.FirstOrDefault(s => !slots.ContainsValue(s));
            if (free != null)
                slots[p] = free;
        }

        return slots;
    }

    private static void Fill(Dictionary<Actor, string> slots, List<Actor> players, params string[] preferred)
    {
        foreach (var p in players)
        {
            if (slots.ContainsKey(p))
                continue;
            var slot = preferred.FirstOrDefault(s => !slots.ContainsValue(s));
            if (slot != null)
                slots[p] = slot;
        }
    }

    /// <summary>Sheet column → party slot (D1–D4 are M1, M2, R1, R2; healer columns go by job).</summary>
    public static Actor? Resolve(string sheetSlot, Dictionary<Actor, string> slots)
    {
        string? slot = sheetSlot switch
        {
            "MT" or "OT" => sheetSlot,
            "D1" => "M1",
            "D2" => "M2",
            "D3" => "R1",
            "D4" => "R2",
            _ => null,
        };
        if (slot != null)
            return slots.FirstOrDefault(kv => kv.Value == slot).Key;
        var job = sheetSlot switch { "WHM" => (byte)24, "AST" => (byte)33, "SCH" => (byte)28, "SGE" => (byte)40, _ => (byte)0 };
        return job == 0 ? null : slots.Keys.FirstOrDefault(a => a.Job == job);
    }
}
