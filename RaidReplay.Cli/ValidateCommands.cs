using RaidReplay.Core.Analysis;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    private static int ValidateShapes(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var index = IndexOne(o, path);
        var wanted = o.Positional.Skip(1).Select(int.Parse).ToHashSet();
        var pulls = index.Pulls.Where(p => p.HasDirector && p.DurationMs > 5000 && (wanted.Count == 0 || wanted.Contains(p.Ordinal)))
                         .ToList();
        var max = o.Int("max", int.MaxValue);
        var stats = new Dictionary<uint, ShapeStats>();
        var data = GameDataFor(o);
        var reg = Registry(o);
        var n = 0;
        var detail = o.Get("detail") is { } dh ? Convert.ToUInt32(dh.Replace("0x", ""), 16) : 0u;
        foreach (var p in pulls.Take(max))
        {
            var r = PullLoader.Load(p, data, reg);
            ShapeValidator.Accumulate(r, stats, detail, detail != 0 ? Console.WriteLine : null);
            Console.Error.Write($"\r  {++n}/{Math.Min(max, pulls.Count)} pulls");
        }

        Console.Error.WriteLine();
        Console.WriteLine($"{"id",-5} {"name",-34} {"shape",-24} {"n",4} {"TP",5} {"FP",5} {"FN",5} {"prec",5} {"rec",5}  {"hitD",5} {"missD",5} {"hitA",5} {"missA",5} {"hitS",5} {"missS",5}  src");
        foreach (var s in stats.Values.OrderBy(s => s.ActionId))
        {
            Console.WriteLine($"{s.ActionId:X4}  {Trunc(s.Name, 34),-34} {s.Shape,-24} {s.Instances,4} {s.TruePositive,5} {s.FalsePositive,5} {s.FalseNegative,5} " +
                              $"{s.Precision,5:0.00} {s.Recall,5:0.00}  {s.MaxHitDist,5:0.0} {Fmt(s.MinMissDist),5} {s.MaxHitAngle,5:0} {Fmt(s.MinMissAngle),5} " +
                              $"{s.MaxHitSide,5:0.0} {Fmt(s.MinMissSide),5}  {s.Source}");
        }

        return 0;
    }

    private static string Fmt(float v) => v == float.MaxValue ? "-" : v.ToString("0.0");

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
