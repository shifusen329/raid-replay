using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RaidReplay.Core.Encounters;

/// <summary>An encounter definition with id-keyed lookups resolved.</summary>
public sealed class CompiledEncounter
{
    public CompiledEncounter(EncounterDefinition def, string source, string hash)
    {
        Def = def;
        Source = source;
        Hash = hash;
        foreach (var t in def.Match.TerritoryIds)
            Territories.Add(t);
        foreach (var (k, v) in def.Abilities)
            Abilities[Parse(k, "abilities")] = v;
        foreach (var (k, v) in def.HeadMarkers)
            HeadMarkers[Parse(k, "headMarkers")] = v;
        foreach (var (k, v) in def.Tethers)
            Tethers[Parse(k, "tethers")] = v;
        foreach (var (k, v) in def.Statuses)
            Statuses[Parse(k, "statuses")] = v;
        foreach (var (cmd, values) in def.Director)
        {
            var map = new Dictionary<uint, string>();
            foreach (var (p1, label) in values)
                map[Parse(p1, "director")] = label;
            Director[Parse(cmd, "director")] = map;
        }

        foreach (var rule in def.Actors)
        {
            if (rule.BnpcBase is { } b)
                ActorRules[b] = rule;
            foreach (var bb in rule.BnpcBases ?? [])
                ActorRules[bb] = rule;
            if (rule.Name != null)
                NamedRules.Add(rule);
        }

        foreach (var e in def.Eobjs)
            Eobjs[e.Base] = e;
        foreach (var f in def.FailureActions)
            FailureActions.Add(f);
    }

    public EncounterDefinition Def { get; }
    public string Source { get; }
    public string Hash { get; }
    public string Key => Def.Key;
    public HashSet<uint> Territories { get; } = [];
    public Dictionary<uint, AbilityDef> Abilities { get; } = new();
    public Dictionary<uint, HeadMarkerDef> HeadMarkers { get; } = new();
    public Dictionary<uint, TetherDef> Tethers { get; } = new();
    public Dictionary<uint, string> Statuses { get; } = new();
    public Dictionary<uint, Dictionary<uint, string>> Director { get; } = new();
    public Dictionary<uint, ActorRule> ActorRules { get; } = new();
    public List<ActorRule> NamedRules { get; } = [];
    public Dictionary<uint, EobjDef> Eobjs { get; } = new();
    public HashSet<uint> FailureActions { get; } = [];

    public ActorRule? RuleFor(uint bnpcBase, string name)
    {
        if (bnpcBase != 0 && ActorRules.TryGetValue(bnpcBase, out var rule))
            return rule;
        foreach (var r in NamedRules)
        {
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && r.BnpcBase == null && r.BnpcBases == null)
                return r;
        }

        return null;
    }

    /// <summary>Ability definition by id, or by an unbound name match.</summary>
    public AbilityDef? AbilityFor(uint actionId, string? name)
    {
        if (Abilities.TryGetValue(actionId, out var def))
            return def;
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var u in Def.UnboundAbilities)
            {
                if (u.As != null && name.Contains(u.NameMatch, StringComparison.OrdinalIgnoreCase))
                    return u.As;
            }
        }

        return null;
    }

    private static uint Parse(string key, string section) =>
        HexId.TryParse(key, out var v) ? v : throw new FormatException($"{section}: invalid hex key '{key}'");
}

public sealed class EncounterLoadError
{
    public required string Source { get; init; }
    public required string Message { get; init; }
}

/// <summary>Built-in (embedded) packs plus user packs from a folder; user packs override by key.</summary>
public sealed class EncounterRegistry
{
    private readonly Dictionary<uint, CompiledEncounter> byTerritory = new();

    public EncounterRegistry(IEnumerable<CompiledEncounter> encounters, IEnumerable<EncounterLoadError> errors)
    {
        Encounters = encounters.ToList();
        Errors = errors.ToList();
        foreach (var e in Encounters)
        {
            foreach (var t in e.Territories)
                byTerritory[t] = e;
        }
    }

    public static EncounterRegistry Empty { get; } = new([], []);

    public IReadOnlyList<CompiledEncounter> Encounters { get; }
    public IReadOnlyList<EncounterLoadError> Errors { get; }

    public CompiledEncounter? ForTerritory(uint territory) => byTerritory.GetValueOrDefault(territory);

    /// <summary>Hash of the packs that apply to the given territories (used to invalidate cached indexes).</summary>
    public string HashFor(IEnumerable<uint> territories)
    {
        var parts = territories.Select(ForTerritory).Where(e => e != null).Select(e => e!.Hash).Distinct().Order();
        var joined = string.Join(",", parts);
        return joined.Length == 0 ? string.Empty : Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    public static EncounterDefinition Parse(string json) =>
        JsonSerializer.Deserialize(json, EncounterJsonContext.Default.EncounterDefinition) ??
        throw new JsonException("empty encounter definition");

    public static string Serialize(EncounterDefinition def) =>
        JsonSerializer.Serialize(def, EncounterJsonContext.Default.EncounterDefinition);

    public static CompiledEncounter Compile(string json, string source)
    {
        var def = Parse(json);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)))[..16];
        var compiled = new CompiledEncounter(def, source, hash);
        var problems = EncounterValidator.Validate(def);
        if (problems.Count > 0)
            throw new FormatException(string.Join("; ", problems));
        return compiled;
    }

    /// <summary>Loads embedded packs, then user packs from <paramref name="userDir"/> (if any).</summary>
    public static EncounterRegistry Load(string? userDir = null)
    {
        var result = new Dictionary<string, CompiledEncounter>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<EncounterLoadError>();
        var asm = typeof(EncounterRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("RaidReplay.Core.Packs.", StringComparison.Ordinal)))
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var compiled = Compile(reader.ReadToEnd(), "builtin:" + name["RaidReplay.Core.Packs.".Length..]);
                result[compiled.Key] = compiled;
            }
            catch (Exception e)
            {
                errors.Add(new EncounterLoadError { Source = name, Message = e.Message });
            }
        }

        if (userDir != null && Directory.Exists(userDir))
        {
            foreach (var file in Directory.EnumerateFiles(userDir, "*.json"))
            {
                try
                {
                    var compiled = Compile(File.ReadAllText(file), file);
                    result[compiled.Key] = compiled;
                }
                catch (Exception e)
                {
                    errors.Add(new EncounterLoadError { Source = file, Message = e.Message });
                }
            }
        }

        return new EncounterRegistry(result.Values, errors);
    }

    /// <summary>Writes the embedded packs to a folder so users can copy and edit them.</summary>
    public static void ExportBuiltins(string dir)
    {
        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("RaidReplay.Core.Packs.", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(dir, "builtin-" + name["RaidReplay.Core.Packs.".Length..]));
            stream.CopyTo(file);
        }
    }
}

public static class EncounterValidator
{
    private static readonly HashSet<string> ShapeTypes =
        ["circle", "donut", "cone", "rect", "cross", "halfRoom", "knockback", "gaze", "arrow", "none"];

    private static readonly HashSet<string> Origins =
        ["caster", "castLoc", "target", "animTarget", "location", "fixed", "point", "statusHolder", "headMarkerHolder", "eobj"];

    private static readonly HashSet<string> Headings = ["cast", "resolution", "towardTarget", "fixed", "actor"];

    private static readonly HashSet<string> Categories =
    [
        "danger", "fake", "hiddenDanger", "tower", "stack", "spread", "tankbuster", "raidwide", "knockback", "gaze",
        "failure", "bait", "info", "ignore", "hazard", "soak", "debuff", "enrage",
    ];

    private static readonly HashSet<string> Roles = ["boss", "enemy", "helper", "clone", "emitter", "pet", "ignore"];

    public static List<string> Validate(EncounterDefinition def)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(def.Key))
            problems.Add("key is required");
        if (def.Match.TerritoryIds.Count == 0)
            problems.Add("match.territoryIds is empty");
        foreach (var rule in def.Actors)
        {
            if (!Roles.Contains(rule.Role))
                problems.Add($"actors: unknown role '{rule.Role}'");
            if (rule.BnpcBase == null && rule.BnpcBases == null && rule.Name == null)
                problems.Add("actors: rule needs bnpcBase, bnpcBases or name");
        }

        foreach (var (id, a) in def.Abilities)
        {
            CheckShape(a.Shape, $"abilities[{id}]", problems);
            CheckCategory(a.Category, $"abilities[{id}]", problems);
            if (a.Telegraph != null && a.Telegraph.Mode is not ("cast" or "lookback" or "none"))
                problems.Add($"abilities[{id}]: unknown telegraph mode '{a.Telegraph.Mode}'");
            if (a.SoakGroup != null && (a.SoakGroup != "role" || a.Soakers is null or <= 0))
                problems.Add($"abilities[{id}]: soakGroup must be \"role\" and needs soakers");
            if (a.Bait is not (null or "farthest" or "closest"))
                problems.Add($"abilities[{id}]: bait must be \"farthest\" or \"closest\"");
            if (a.OnlyTarget && a.Category != "bait")
                problems.Add($"abilities[{id}]: onlyTarget is for category \"bait\"");
            foreach (var p in a.Positions ?? [])
            {
                if (p.Group is not ("support" or "dps" or "any"))
                    problems.Add($"abilities[{id}].positions: group must be support, dps or any");
                foreach (var s in new[] { p.Holder, p.Soakers })
                {
                    if (s.Waymark != null && Model.PullReplay.WaymarkSlot(s.Waymark) < 0)
                        problems.Add($"abilities[{id}].positions: unknown waymark '{s.Waymark}' (A-D, 1-4)");
                    if (s.Waymark == null && s.Default == null)
                        problems.Add($"abilities[{id}].positions: a spot needs a waymark or a default");
                    if (s.Offset.Length != 2 || s.Default is { Length: not 2 })
                        problems.Add($"abilities[{id}].positions: offset/default must be [x, y]");
                }
            }
        }

        foreach (var t in def.Triggers)
        {
            CheckShape(t.Draw, $"triggers[{t.Id}]", problems);
            CheckCategory(t.Category, $"triggers[{t.Id}]", problems);
        }

        foreach (var e in def.Eobjs)
            CheckShape(e.Draw, $"eobjs[{e.Base}]", problems);

        var phaseIds = new HashSet<string>();
        foreach (var p in def.Phases)
        {
            if (!phaseIds.Add(p.Id))
                problems.Add($"phases: duplicate id '{p.Id}'");
        }

        foreach (var m in def.Mechanics)
        {
            if (m.Phase != null && !phaseIds.Contains(m.Phase))
                problems.Add($"mechanics[{m.Id}]: unknown phase '{m.Phase}'");
        }

        string[] sheetSlots = ["MT", "OT", "WHM", "AST", "SCH", "SGE", "D1", "D2", "D3", "D4", "Extras"];
        foreach (var mm in def.Mitigation?.Mechanics ?? [])
        {
            if (mm.Hits.Count == 0 && mm.HitNames.Count == 0)
                problems.Add($"mitigation[{mm.Id}]: needs hits or hitNames");
            if (mm.Phase != null && def.Phases.All(p => p.Id != mm.Phase))
                problems.Add($"mitigation[{mm.Id}]: unknown phase '{mm.Phase}'");
            foreach (var item in mm.Plan)
            {
                if (!sheetSlots.Contains(item.Slot))
                    problems.Add($"mitigation[{mm.Id}]: unknown slot '{item.Slot}'");
                foreach (var token in item.Use.Concat(item.Carry))
                {
                    if (!Analysis.MitigationCatalog.IsKnown(token))
                        problems.Add($"mitigation[{mm.Id}]: unknown ability '{token}'");
                }
            }
        }

        foreach (var cp in def.CleansePulses)
        {
            if (cp.PulseActions.Count == 0 || cp.VulnStatus.Length == 0 || cp.Cleanses.Count == 0 || cp.WindowS <= 0)
                problems.Add($"cleansePulses[{cp.Label}]: needs pulseActions, vulnStatus, cleanses and a positive windowS");
            foreach (var c in cp.Cleanses.Where(c => c.By is not ("heal" or "lethal") || c.Status.Length == 0))
                problems.Add($"cleansePulses[{cp.Label}]: cleanse '{c.Status}' needs a status and by = heal or lethal");
        }

        if (def.ArrowPuzzle is { } ap)
        {
            if (ap.TeleporterEobj.Value == 0 || ap.ArrowStatus.Length == 0)
                problems.Add("arrowPuzzle: needs teleporterEobj and arrowStatus");
            if (ap.Center.Length != 2 || ap.HalfSize <= 0 || ap.Step <= 0 || ap.TriggerRadius <= 0 || ap.HopMs <= 0 || ap.MaxChain < 0)
                problems.Add("arrowPuzzle: center must be [x, y]; halfSize, step, triggerRadius and hopMs must be positive");
            foreach (var (id, dir) in ap.ArrowDirections)
            {
                if (!HexId.TryParse(id, out _))
                    problems.Add($"arrowPuzzle.arrowDirections: '{id}' is not a hex status id");
                if (dir is not ("N" or "E" or "S" or "W"))
                    problems.Add($"arrowPuzzle.arrowDirections[{id}]: '{dir}' must be N, E, S or W");
            }
        }

        return problems;
    }

    private static void CheckShape(ShapeDef? s, string where, List<string> problems)
    {
        if (s == null)
            return;
        if (!ShapeTypes.Contains(s.Type))
            problems.Add($"{where}: unknown shape type '{s.Type}'");
        if (s.Origin != null && !Origins.Contains(s.Origin))
            problems.Add($"{where}: unknown origin '{s.Origin}'");
        if (s.Heading != null && !Headings.Contains(s.Heading))
            problems.Add($"{where}: unknown heading '{s.Heading}'");
    }

    private static void CheckCategory(string? c, string where, List<string> problems)
    {
        if (c != null && !Categories.Contains(c))
            problems.Add($"{where}: unknown category '{c}'");
    }
}
