using System.Text;
using RaidReplay.Core.Export;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>
    /// rr sanitize &lt;file&gt; &lt;n&gt; &lt;out&gt;: writes a self-contained, anonymized excerpt of pull #n (see
    /// <see cref="PullExcerpt"/>): names, ids, worlds, checksums and dates are replaced; chat and status lists dropped.
    /// </summary>
    private static int Sanitize(Options o)
    {
        var pull = FindPull(o, 0, 1, out var path);
        var outPath = o.Arg(2, "out");
        ExcerptInfo info;
        using (var writer = new StreamWriter(outPath, false, new UTF8Encoding(false)))
            info = PullExcerpt.Write(pull, writer, path);
        Console.WriteLine($"wrote {outPath}: {info.Lines:N0} lines, {new FileInfo(outPath).Length / 1024.0:N0} KB " +
                          $"({info.LogNames.Count} players anonymized)");
        return 0;
    }
}
