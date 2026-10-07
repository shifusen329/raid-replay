using System.Globalization;
using System.Text;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Export;

/// <summary>What <see cref="PullExcerpt.Write"/> wrote.</summary>
/// <param name="Lines">Log lines after the synthesized header.</param>
/// <param name="LogNames">The name each player got in the excerpt (entity id → "Player1".."PlayerN"; the party first).</param>
public sealed record ExcerptInfo(int Lines, IReadOnlyDictionary<uint, string> LogNames);

/// <summary>
/// A self-contained, anonymized excerpt of one pull: a synthesized state header (zone, map, party, combatants, waymarks
/// from the pull's checkpoint) followed by the pull's lines. Used for test fixtures (<c>rr sanitize</c>) and for the
/// log attached to a feedback report.
/// <list type="bullet">
/// <item>Chat (00), status lists (38/42) and plugin debug lines are dropped.</item>
/// <item>Player names become Player1..N, player entity ids become 10000001..; world names/ids are removed.</item>
/// <item>Every line's ACT checksum is zeroed: the checksum covers the original fields (incl. names), so keeping it
///   would let anyone confirm a guessed name offline.</item>
/// <item>Timestamps are shifted so the excerpt starts on 2000-01-01 UTC (relative timing is preserved); this hides the
///   real date, time zone and play schedule.</item>
/// </list>
/// </summary>
public static class PullExcerpt
{
    /// <summary>Shifted start of every excerpt (UTC).</summary>
    public static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string ZeroHash = "0000000000000000";
    private const string NewLine = "\r\n";

    private static readonly HashSet<int> Dropped =
    [
        LineType.Chat, LineType.StatusList, LineType.StatusList3, 249, 250, 251, 252, 253, 254, 256, LineType.RsvData,
        LineType.SystemLogMessage,
    ];

    /// <summary>
    /// Writes the excerpt of <paramref name="pull"/>, read from <paramref name="path"/> (default: the pull's own log
    /// file), to <paramref name="writer"/> with CRLF line endings.
    /// </summary>
    public static ExcerptInfo Write(PullSummary pull, TextWriter writer, string? path = null, CancellationToken ct = default)
    {
        var snap = pull.Checkpoint;
        var map = new Anonymizer(snap.Ticks);

        // Party first (stable Player1..8 order), then any other players in the snapshot.
        foreach (var p in pull.Party)
            map.AddPlayer(p.Id, p.Name, snap.Combatants.FirstOrDefault(c => c.Id == p.Id)?.World);
        foreach (var c in snap.Combatants.Where(c => c.IsPlayer))
            map.AddPlayer(c.Id, c.Name, c.World);

        var ts = map.Timestamp(snap.Ticks);
        void Header(params string[] fields)
        {
            writer.Write(string.Join("|", fields));
            writer.Write("|" + ZeroHash + NewLine);
        }

        Header("01", ts, $"{snap.ZoneId:X}", snap.ZoneName);
        Header("40", ts, snap.MapId.ToString(CultureInfo.InvariantCulture), "", snap.MapName, "");
        if (snap.Combatants.FirstOrDefault(c => c.Id == snap.PrimaryPlayerId) is { } me)
            Header("02", ts, map.Id(me.Id), map.Name(me.Name));
        foreach (var c in snap.Combatants)
        {
            Header("03", ts, map.Id(c.Id), map.Name(c.Name), $"{c.Job:X2}", $"{c.Level:X2}", map.Id(c.OwnerId, 4),
                   c.IsPlayer ? "00" : $"{c.WorldId:X2}", "", c.BNpcNameId.ToString(CultureInfo.InvariantCulture),
                   c.BNpcBaseId.ToString(CultureInfo.InvariantCulture), c.Hp.ToString(CultureInfo.InvariantCulture),
                   c.MaxHp.ToString(CultureInfo.InvariantCulture), "10000", "10000", "", "", F(c.X), F(c.Y), F(c.Z), F(c.Heading));
        }

        Header(["11", ts, snap.Party.Count.ToString(CultureInfo.InvariantCulture), ..snap.Party.Select(id => map.Id(id))]);
        foreach (var w in snap.Waymarks)
            Header("28", ts, "Add", w.Slot.ToString(CultureInfo.InvariantCulture), "00000000", "", F(w.X), F(w.Y), F(w.Z));

        var consumer = new Consumer(writer, map, ct);
        LogLineReader.Read(path ?? pull.FilePath, pull.CheckpointOffset, pull.TailEndOffset, LineTypeSet.All, consumer, ct);
        ct.ThrowIfCancellationRequested();
        writer.Flush();
        return new ExcerptInfo(consumer.Lines, map.LogNames());

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Consistent replacement of player identities and timestamps.</summary>
    private sealed class Anonymizer(long originTicks)
    {
        private readonly Dictionary<uint, uint> ids = new();
        private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
        private readonly HashSet<string> worlds = new(StringComparer.Ordinal);
        private readonly long shift = originTicks - Epoch.Ticks;

        public void AddPlayer(uint id, string name, string? world)
        {
            if ((id >> 28) != 1 || ids.ContainsKey(id))
                return;
            var n = ids.Count + 1;
            ids[id] = 0x10000000u + (uint)n;
            if (name.Length > 0)
                names.TryAdd(name, $"Player{n}");
            if (!string.IsNullOrEmpty(world))
                worlds.Add(world!);
        }

        private static uint? PlayerId(string field) =>
            field.Length == 8 && field[0] == '1' && uint.TryParse(field, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;

        public Dictionary<uint, string> LogNames() => ids.ToDictionary(kv => kv.Key, kv => $"Player{kv.Value - 0x10000000u}");

        public string Id(uint id, int width = 8) =>
            ids.TryGetValue(id, out var mapped) ? mapped.ToString("X8") : id.ToString($"X{width}");

        public string Name(string s)
        {
            foreach (var (real, fake) in names)
                s = s.Replace(real, fake, StringComparison.Ordinal);
            return s;
        }

        public string Timestamp(long utcTicks) =>
            new DateTime(utcTicks - shift, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "+00:00";

        /// <summary>Rewrites one original log line: names, ids, worlds, timestamp, checksum.</summary>
        public string Line(string line)
        {
            var f = line.Split('|');
            if (f.Length < 3)
                return line;
            var ticks = Utf8Num.TimestampUtcTicks(Encoding.ASCII.GetBytes(f[1]));
            if (ticks != 0)
                f[1] = Timestamp(ticks);

            // A player first seen during the pull (not in the checkpoint) gets the next PlayerN before this line and every
            // later one is rewritten.
            if (f[0] == "03" && f.Length > F03.World + 1 && PlayerId(f[F03.Id]) is { } added)
                AddPlayer(added, f[F03.Name], f[F03.World]);
            else if (f[0] == "261" && f.Length > 5 && f[2] == "Add" && PlayerId(f[3]) is { } added261)
            {
                var name = Array.IndexOf(f, "Name", 4);
                AddPlayer(added261, name > 0 && name + 1 < f.Length - 1 ? f[name + 1] : string.Empty, null);
            }

            f[^1] = ZeroHash;
            var isMemory = f[0] == "261";
            for (var i = 2; i < f.Length - 1; i++)
            {
                var field = f[i];
                if (field.Length == 8 && field[0] == '1' && uint.TryParse(field, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) &&
                    ids.TryGetValue(id, out var mapped))
                {
                    f[i] = mapped.ToString("X8");
                }
                else if (worlds.Contains(field))
                {
                    f[i] = string.Empty;
                }
                else if (isMemory && field is "WorldID" or "CurrentWorldID" && i + 1 < f.Length - 1)
                {
                    f[i + 1] = "0";
                    i++;
                }
                else if (field.Length > 0)
                {
                    f[i] = Name(field);
                }
            }

            // AddCombatant/RemoveCombatant for players: world id (hex) and world name.
            if (f[0] is "03" or "04" && f.Length > F03.World && f[F03.Id].StartsWith('1') && f[F03.Id].Length == 8)
            {
                f[F03.WorldId] = "00";
                f[F03.World] = string.Empty;
            }

            return string.Join('|', f);
        }
    }

    private sealed class Consumer(TextWriter writer, Anonymizer map, CancellationToken ct) : ILineConsumer
    {
        public int Lines { get; private set; }

        public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
        {
            if (Dropped.Contains(type))
                return !ct.IsCancellationRequested;
            writer.Write(map.Line(Encoding.UTF8.GetString(line)));
            writer.Write(NewLine);
            Lines++;
            return !ct.IsCancellationRequested;
        }
    }
}
