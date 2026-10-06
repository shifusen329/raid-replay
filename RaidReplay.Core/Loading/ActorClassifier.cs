using RaidReplay.Core.Encounters;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Loading;

/// <summary>Assigns <see cref="ActorKind"/> using encounter pack rules first, then generic traits.</summary>
public static class ActorClassifier
{
    public const uint CommonHelperBase = 9020;

    public static void Classify(PullReplay replay, CompiledEncounter? enc, HashSet<uint> partyIds)
    {
        var engaged = new HashSet<Actor>();
        foreach (var a in replay.Actions)
        {
            if (!a.Source.IsPlayer)
                continue;
            foreach (var h in a.Hits)
            {
                if (!h.Target.IsPlayer)
                    engaged.Add(h.Target);
            }
        }

        foreach (var a in replay.Actors)
        {
            if (a.IsPlayer)
            {
                a.Kind = ActorKind.Player;
                continue;
            }

            if (a.ObjectType == 7)
            {
                a.Kind = ActorKind.EventObject;
                var eobj = enc?.Eobjs.GetValueOrDefault(a.BNpcBaseId);
                a.Render = eobj is { Render: true };
                a.Label = eobj?.Label;
                a.Role = "eobj";
                a.Conf = eobj?.Conf;
                continue;
            }

            if (a.OwnerId != 0 && a.OwnerId != 0xE0000000)
            {
                a.Kind = ActorKind.Pet;
                continue;
            }

            var rule = enc?.RuleFor(a.BNpcBaseId, a.Name);
            if (rule != null)
            {
                a.Role = rule.Role;
                a.Label = rule.Label;
                a.Render = rule.Render && rule.Role is not ("helper" or "emitter" or "ignore");
                a.Conf = rule.Conf;
                if (rule.Hitbox is { } hb)
                    a.Radius = hb;
                a.Kind = rule.Role switch
                {
                    "boss" => ActorKind.Boss,
                    "helper" or "emitter" => ActorKind.Helper,
                    "pet" => ActorKind.Pet,
                    "ignore" => ActorKind.Other,
                    _ => ActorKind.Enemy,
                };
                continue;
            }

            if (a.ObjectType is 3 or 4 or 5 or 6 or 9 or 10 or 12 or 13 or 14 or 15)
            {
                a.Kind = ActorKind.Other;
                a.Render = false;
                continue;
            }

            if (a.BNpcBaseId == CommonHelperBase || a.Level <= 1 || a.MaxHp is > 0 and <= 100)
            {
                a.Kind = ActorKind.Helper;
                a.Render = false;
                continue;
            }

            a.Kind = ActorKind.Enemy;
        }

        // Generic boss pick when the pack named none: the engaged enemy with the most max HP (per distinct BNpcBase).
        if (!replay.Actors.Any(a => a.Kind == ActorKind.Boss))
        {
            var top = engaged.Where(a => a.Kind == ActorKind.Enemy).OrderByDescending(a => a.MaxHp).FirstOrDefault();
            if (top != null)
            {
                foreach (var a in replay.Actors)
                {
                    if (a.Kind == ActorKind.Enemy && a.BNpcBaseId == top.BNpcBaseId && a.Name == top.Name)
                        a.Kind = ActorKind.Boss;
                }
            }
        }

        // Unengaged, never-targetable enemies with no visible presence are likely helpers too.
        foreach (var a in replay.Actors)
        {
            if (a.Kind == ActorKind.Enemy && a.Role == null && !engaged.Contains(a) && a.MaxHp > 0 &&
                replay.Actors.Any(b => b.Kind == ActorKind.Boss && b.Name == a.Name && b.MaxHp > a.MaxHp * 10))
            {
                a.Kind = ActorKind.Helper;
                a.Render = false;
            }
        }
    }
}
