using System.Diagnostics;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    private const string Usage = """
        rr — Raid Replay command line

        Usage: rr <command> [options]

          stats <file>                       line-type histogram, malformed counts, timestamp disorder, MB/s
          index [--max N]                    index the N newest logs in LOGS_PATH (cached)
          pulls <file|index> [--all]         list pulls of a log file (newest file if 'latest')
          dump-pull <file> <n> [--events]    reconstruct pull #n and print a summary
          dump-frame <file> <n> --at <sec>   print actor positions/AoEs at a time
          dump-aoes <file> <n>               list inferred AoEs of a pull
          validate-shapes <file> [n...]      compare predicted AoE hit sets vs actual hits
          phases <file>                      list detected phases per pull
          validate-encounters                validate built-in and user encounter packs
          sanitize <file> <n> <out>          write an anonymized excerpt of pull #n

        Options:
          --logs <dir>     log folder (default: LOGS_PATH env or .env)
          --cache <dir>    index cache folder (default: %LOCALAPPDATA%\RaidReplay\index)
          --game <dir>     FFXIV 'game' folder for Lumina data (default: auto-detect)
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var opts = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "stats" => Stats(opts),
                "index" => Index(opts),
                "pulls" => Pulls(opts),
                "dump-pull" => DumpPull(opts),
                "dump-frame" => DumpFrame(opts),
                "dump-aoes" => DumpAoes(opts),
                "validate-shapes" => ValidateShapes(opts),
                "phases" => Phases(opts),
                "validate-encounters" => ValidateEncounters(opts),
                "sanitize" => Sanitize(opts),
                "inspect" => Inspect(opts),
                "track" => Track(opts),
                "analyze" => Analyze(opts),
                "learn" => Learn(opts),
                "arrows" => Arrows(opts),
                "arrows-calibrate" => ArrowsCalibrate(opts),
                "stack-survey" => StackSurvey(opts),
                "validate-contact" => ValidateContact(opts),
                "watch" => Watch(opts),
                "simulate" => Simulate(opts),
#if LUMINA
                "map-png" => MapPng(opts),
#endif
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (CliException e)
        {
            return Fail(e.Message);
        }
        finally
        {
            await Console.Out.FlushAsync();
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static int Stats(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var consumer = new StatsConsumer();
        var sw = Stopwatch.StartNew();
        var result = LogLineReader.Read(path, 0, -1, LineTypeSet.All, consumer);
        sw.Stop();
        var mb = result.BytesRead / 1024.0 / 1024.0;
        Console.WriteLine($"{Path.GetFileName(path)}: {result.LinesSeen:N0} lines, {mb:N1} MB in {sw.Elapsed.TotalSeconds:N2}s ({mb / sw.Elapsed.TotalSeconds:N0} MB/s)");
        Console.WriteLine($"malformed timestamps: {consumer.BadTimestamps}, max backward jump (non-chat): {consumer.MaxBackward / 10000.0:N0} ms (type {consumer.MaxBackwardType})");
        foreach (var (type, count) in consumer.Counts.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {type,3}: {count,10:N0}  {consumer.Bytes[type] / 1024.0 / 1024.0,8:N1} MB");
        return 0;
    }

    private sealed class StatsConsumer : ILineConsumer
    {
        public readonly Dictionary<int, long> Counts = new();
        public readonly Dictionary<int, long> Bytes = new();
        public long BadTimestamps;
        public long MaxBackward;
        public int MaxBackwardType;
        private long last;

        public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
        {
            Counts[type] = Counts.GetValueOrDefault(type) + 1;
            Bytes[type] = Bytes.GetValueOrDefault(type) + line.Length + 2;
            Span<int> buf = stackalloc int[4];
            var f = new LineFields(line, buf);
            var ticks = f.Ticks;
            if (ticks == 0)
            {
                BadTimestamps++;
                return true;
            }

            if (type != LineType.Chat)
            {
                if (last - ticks > MaxBackward)
                {
                    MaxBackward = last - ticks;
                    MaxBackwardType = type;
                }

                last = Math.Max(last, ticks);
            }

            return true;
        }
    }

    private static int Index(Options o)
    {
        var library = o.CreateLibrary();
        var max = o.Int("max", int.MaxValue);
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var task = Task.Run(() => library.Refresh(cts.Token, max));
        while (!task.Wait(1000))
        {
            var current = library.Files.FirstOrDefault(f => f.Status == FileIndexStatus.Indexing);
            if (current != null)
                Console.Error.Write($"\r  indexing {current.FileName} {current.Progress:P0}      ");
        }

        Console.Error.WriteLine();
        foreach (var f in library.Files)
        {
            var idx = f.Index;
            Console.WriteLine(
                $"{f.FileName,-32} {f.Size / 1048576.0,8:N1} MB  {f.Status,-8} {idx?.Pulls.Count ?? 0,4} pulls  {idx?.IndexSeconds ?? 0,6:N2}s {f.Error}");
        }

        var total = library.Files.Sum(f => f.Size) / 1048576.0;
        Console.WriteLine($"{library.Files.Count} files, {total:N0} MB, {library.AllPulls.Count} pulls in {sw.Elapsed.TotalSeconds:N1}s");
        return 0;
    }
}

internal sealed class CliException(string message) : Exception(message);
