using System.Text;

namespace RaidReplay.Core.Parsing;

/// <summary>
/// Pipe-delimited view over one log line. Field 0 is the line type, field 1 the timestamp, the last
/// field the checksum. The caller supplies the split buffer (usually stackalloc'd).
/// </summary>
public readonly ref struct LineFields
{
    private readonly ReadOnlySpan<byte> line;
    private readonly ReadOnlySpan<int> starts;

    public LineFields(ReadOnlySpan<byte> line, Span<int> buffer)
    {
        this.line = line;
        var max = buffer.Length - 1;
        var n = 0;
        var pos = 0;
        buffer[0] = 0;
        while (n < max)
        {
            var idx = line[pos..].IndexOf((byte)'|');
            n++;
            if (idx < 0)
            {
                buffer[n] = line.Length + 1;
                break;
            }

            pos += idx + 1;
            buffer[n] = pos;
        }

        Count = n;
        starts = buffer[..(n + 1)];
    }

    /// <summary>Number of fields split (capped at the buffer size - 1).</summary>
    public int Count { get; }

    public ReadOnlySpan<byte> Line => line;

    public ReadOnlySpan<byte> this[int i] =>
        (uint)i < (uint)Count ? line.Slice(starts[i], Math.Max(0, starts[i + 1] - starts[i] - 1)) : default;

    public bool Has(int i) => (uint)i < (uint)Count;

    public bool IsEmpty(int i) => this[i].IsEmpty;

    public uint Hex(int i) => Utf8Num.Hex(this[i]);

    public int Int(int i) => Utf8Num.Int(this[i]);

    public long Long(int i) => Utf8Num.Long(this[i]);

    public float Float(int i) => Utf8Num.Float(this[i]);

    public long Ticks => Utf8Num.TimestampUtcTicks(this[1]);

    public bool Is(int i, ReadOnlySpan<byte> text) => this[i].SequenceEqual(text);

    public string Str(int i, StringPool? pool = null) =>
        pool != null ? pool.Get(this[i]) : Encoding.UTF8.GetString(this[i]);

    /// <summary>The number of fields excluding the trailing checksum (meaningful only if not capped).</summary>
    public int DataCount => Count - 1;
}

/// <summary>Interns UTF-8 byte sequences (actor/ability names) to shared strings.</summary>
public sealed class StringPool
{
    private readonly Dictionary<ulong, Entry> map = new();

    public string Get(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        var hash = Fnv1A(bytes);
        if (map.TryGetValue(hash, out var entry))
        {
            for (var e = entry; e != null; e = e.Next)
            {
                if (bytes.SequenceEqual(e.Bytes))
                    return e.Value;
            }
        }

        var created = new Entry(bytes.ToArray(), Encoding.UTF8.GetString(bytes), entry);
        map[hash] = created;
        return created.Value;
    }

    private static ulong Fnv1A(ReadOnlySpan<byte> bytes)
    {
        var h = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 1099511628211UL;
        }

        return h;
    }

    private sealed class Entry(byte[] bytes, string value, Entry? next)
    {
        public readonly byte[] Bytes = bytes;
        public readonly string Value = value;
        public readonly Entry? Next = next;
    }
}
