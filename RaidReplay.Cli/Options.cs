using RaidReplay.Core.Indexing;

namespace RaidReplay.Cli;

internal sealed class Options
{
    private readonly List<string> positional = [];
    private readonly Dictionary<string, string?> flags = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                string? value = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];
                o.flags[key] = value;
            }
            else
            {
                o.positional.Add(a);
            }
        }

        return o;
    }

    public IReadOnlyList<string> Positional => positional;

    public string Arg(int index, string name) =>
        index < positional.Count ? positional[index] : throw new CliException($"missing argument <{name}>");

    public string? OptionalArg(int index) => index < positional.Count ? positional[index] : null;

    public bool Has(string flag) => flags.ContainsKey(flag);

    public string? Get(string flag) => flags.GetValueOrDefault(flag);

    public int Int(string flag, int fallback) => int.TryParse(Get(flag), out var v) ? v : fallback;

    public double Double(string flag, double fallback) =>
        double.TryParse(Get(flag), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public string LogsDir
    {
        get
        {
            var dir = Get("logs") ?? Environment.GetEnvironmentVariable("LOGS_PATH") ?? DotEnv("LOGS_PATH");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "Advanced Combat Tracker", "FFXIVLogs");
            }

            return dir;
        }
    }

    public string CacheDir =>
        Get("cache") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "RaidReplay", "index");

    /// <summary>Resolves a log file argument: absolute/relative path, bare file name in LOGS_PATH, or 'latest'.</summary>
    public string ResolveFile(string arg)
    {
        if (arg == "latest")
        {
            return LogLibrary.EnumerateLogs(LogsDir).FirstOrDefault() ??
                   throw new CliException($"no logs in {LogsDir}");
        }

        if (File.Exists(arg))
            return Path.GetFullPath(arg);
        var inLogs = Path.Combine(LogsDir, arg);
        if (File.Exists(inLogs))
            return inLogs;
        var match = LogLibrary.EnumerateLogs(LogsDir).FirstOrDefault(f => Path.GetFileName(f).Contains(arg, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new CliException($"log file not found: {arg}");
    }

    public static string? DotEnv(string key)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(file))
                continue;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('='))
                    continue;
                var k = line[..line.IndexOf('=')].Trim();
                if (k != key)
                    continue;
                return line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }
}
