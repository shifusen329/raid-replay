using System.Buffers;

namespace RaidReplay.Core.Parsing;

/// <summary>Receives raw lines that passed the reader's type filter.</summary>
public interface ILineConsumer
{
    /// <summary>Handle one complete line (no trailing CR/LF). Return false to stop reading.</summary>
    bool OnLine(long offset, int type, ReadOnlySpan<byte> line);
}

public readonly record struct ReadResult(long EndOffset, long BytesRead, long LinesSeen, bool Stopped, bool Cancelled);

/// <summary>
/// Streams an ACT network log in large chunks. Opens the file with sharing so the live log that ACT
/// is still writing can be read. Only complete lines are delivered; the returned <see cref="ReadResult.EndOffset"/>
/// is the byte offset just past the last complete line, i.e. the resume point for a growing file.
/// </summary>
public static class LogLineReader
{
    private const int DefaultBufferSize = 4 << 20;

    public static ReadResult Read<T>(
        string path, long start, long end, LineTypeSet filter, T consumer, CancellationToken ct = default,
        Action<long>? progress = null)
        where T : ILineConsumer
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
                                      FileOptions.SequentialScan);
        if (end < 0 || end > fs.Length)
            end = fs.Length;
        if (start < 0)
            start = 0;
        if (start >= end)
            return new ReadResult(start, 0, 0, false, false);

        fs.Seek(start, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var bufferStart = start; // file offset of buffer[0]
        var filled = 0;
        long lines = 0;
        var nextProgress = start + (8 << 20);
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                    return new ReadResult(bufferStart, bufferStart - start, lines, false, true);

                var want = (int)Math.Min(buffer.Length - filled, end - (bufferStart + filled));
                var read = want > 0 ? fs.Read(buffer, filled, want) : 0;
                if (read <= 0)
                    break;
                filled += read;

                var span = buffer.AsSpan(0, filled);
                var pos = 0;
                while (true)
                {
                    var idx = span[pos..].IndexOf((byte)'\n');
                    if (idx < 0)
                        break;
                    var line = span.Slice(pos, idx);
                    if (!line.IsEmpty && line[^1] == (byte)'\r')
                        line = line[..^1];
                    var lineOffset = bufferStart + pos;
                    pos += idx + 1;
                    lines++;
                    if (line.IsEmpty)
                        continue;
                    var type = LineType.Parse(line);
                    if (type >= 0 && filter.Contains(type) && !consumer.OnLine(lineOffset, type, line))
                        return new ReadResult(lineOffset, lineOffset - start, lines, true, false);
                }

                if (pos == 0 && filled == buffer.Length)
                {
                    // A single line longer than the buffer: grow.
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                    continue;
                }

                // Shift the partial remainder to the front.
                var remain = filled - pos;
                if (remain > 0)
                    Buffer.BlockCopy(buffer, pos, buffer, 0, remain);
                bufferStart += pos;
                filled = remain;

                if (progress != null && bufferStart >= nextProgress)
                {
                    progress(bufferStart);
                    nextProgress = bufferStart + (8 << 20);
                }
            }

            // bufferStart now points at the first byte of an incomplete trailing line (or end).
            return new ReadResult(bufferStart, bufferStart - start, lines, false, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
