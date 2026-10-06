#if LUMINA
using System.IO.Compression;
using System.Numerics;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr map-png &lt;mapId&gt; &lt;out.png&gt; [--log file --pull n --at sec]: renders the map texture with overlays
    /// (arena circle r=20 at 100,100, waymarks, player positions) to verify the world→texture transform.</summary>
    private static int MapPng(Options o)
    {
        var mapId = uint.Parse(o.Arg(0, "mapId"));
        var outPath = o.Arg(1, "out.png");
        GameDataFor(o);
        var game = o.Get("game") ?? Environment.GetEnvironmentVariable("FFXIV_GAME") ?? DefaultGamePath;
        var lumina = new Lumina.GameData(Path.Combine(game, "sqpack"), new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var map = gameData!.GetMap(mapId) ?? throw new CliException($"map {mapId} not found");
        var tex = lumina.GetFile<Lumina.Data.Files.TexFile>(map.TexturePath) ?? throw new CliException($"texture not found: {map.TexturePath}");
        var w = tex.Header.Width;
        var h = tex.Header.Height;
        var bgra = tex.ImageData;
        Console.WriteLine($"{map.TexturePath} {w}x{h} sizeFactor={map.SizeFactor} offset=({map.OffsetX},{map.OffsetY})");

        Vector2 ToPx(float x, float y) => new(((x + map.OffsetX) * map.SizeFactor / 100f) + (w / 2f), ((y + map.OffsetY) * map.SizeFactor / 100f) + (h / 2f));
        void Dot(Vector2 p, int r, byte cr, byte cg, byte cb)
        {
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var x = (int)p.X + dx;
                var y = (int)p.Y + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || (dx * dx) + (dy * dy) > r * r)
                    continue;
                var i = ((y * w) + x) * 4;
                bgra[i] = cb;
                bgra[i + 1] = cg;
                bgra[i + 2] = cr;
                bgra[i + 3] = 255;
            }
        }

        for (var a = 0; a < 720; a++)
        {
            var ang = a * MathF.PI / 360;
            Dot(ToPx(100 + (20 * MathF.Sin(ang)), 100 + (20 * MathF.Cos(ang))), 2, 255, 0, 0);
        }

        Dot(ToPx(100, 100), 5, 255, 255, 0);
        if (o.Get("log") is { } log)
        {
            o2(log);
        }

        void o2(string logArg)
        {
            var opts = Options.Parse([logArg, o.Get("pull") ?? "1"]);
            var r = PullLoader.Load(FindPull(opts, 0, 1, out _), gameData, Registry(o));
            var t = (int)(o.Double("at", 5) * 1000);
            foreach (var wm in r.InitialWaymarks)
                Dot(ToPx(wm.X, wm.Y), 7, 0, 160, 255);
            foreach (var p in r.Party)
            {
                if (p.Track.TrySample(t, out var pos, out _))
                    Dot(ToPx(pos.X, pos.Y), 5, 0, 255, 0);
            }
        }

        WritePng(outPath, w, h, bgra);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static void WritePng(string path, int w, int h, byte[] bgra)
    {
        using var fs = File.Create(path);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w);
        BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            var row = new byte[(w * 4) + 1];
            for (var y = 0; y < h; y++)
            {
                row[0] = 0;
                for (var x = 0; x < w; x++)
                {
                    var i = ((y * w) + x) * 4;
                    row[1 + (x * 4)] = bgra[i + 2];
                    row[2 + (x * 4)] = bgra[i + 1];
                    row[3 + (x * 4)] = bgra[i];
                    row[4 + (x * 4)] = bgra[i + 3];
                }

                z.Write(row);
            }
        }

        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = PngCrc.Compute(t, data);
        var c = new byte[4];
        BigEndian(c, 0, crc);
        s.Write(c);
    }

    private static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }
}


internal static class PngCrc
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }

        return t;
    }

    public static uint Compute(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a)
            c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b)
            c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
#endif
