using System.Globalization;
using System.Text;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("21|x", 21)]
    [InlineData("00|x", 0)]
    [InlineData("261|x", 261)]
    [InlineData("|x", -1)]
    [InlineData("2a|x", -1)]
    [InlineData("12345|x", -1)]
    public void LineTypeParse(string line, int expected) =>
        Assert.Equal(expected, LineType.Parse(Encoding.UTF8.GetBytes(line)));

    [Theory]
    [InlineData("4000EA7E", 0x4000EA7Eu)]
    [InlineData("e0000000", 0xE0000000u)]
    [InlineData("D000047", 0xD000047u)]
    [InlineData("", 0u)]
    [InlineData("xyz", 0u)]
    public void Hex(string s, uint expected) => Assert.Equal(expected, Utf8Num.Hex(Encoding.UTF8.GetBytes(s)));

    [Theory]
    [InlineData("100.4135", 100.4135f)]
    [InlineData("-2.3936", -2.3936f)]
    [InlineData("0.00", 0f)]
    [InlineData("-36893490000000000000.0000", -3.689349E19f)]
    [InlineData("1.5E2", 150f)]
    public void Float(string s, float expected) =>
        Assert.Equal(expected, Utf8Num.Float(Encoding.UTF8.GetBytes(s)), (MathF.Abs(expected) * 1e-6f) + 1e-4f);

    [Fact]
    public void FloatIsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(1.5e2f, Utf8Num.Float("1.5E2"u8), 0.001f);
            Assert.Equal(99.99f, Utf8Num.Float("99.99"u8), 0.001f);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FloatEmptyIsNaN() => Assert.True(float.IsNaN(Utf8Num.Float(""u8)));

    [Fact]
    public void TimestampToUtc()
    {
        var ticks = Utf8Num.TimestampUtcTicks("2026-10-05T14:33:11.1234567-05:00"u8);
        var expected = new DateTimeOffset(2026, 10, 5, 14, 33, 11, TimeSpan.FromHours(-5)).UtcTicks + 1234567;
        Assert.Equal(expected, ticks);
        var plus = Utf8Num.TimestampUtcTicks("2026-10-05T21:03:11.0000000+01:30"u8);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 21, 3, 11, new TimeSpan(1, 30, 0)).UtcTicks, plus);
        Assert.Equal(0, Utf8Num.TimestampUtcTicks("garbage"u8));
    }

    [Theory]
    [InlineData(0x1FFFu, -2.356f)]
    [InlineData(0x5FFFu, -0.785f)]
    [InlineData(0x9FFFu, 0.785f)]
    [InlineData(0xDFFFu, 2.356f)]
    [InlineData(0xFFFFu, 3.1416f)]
    [InlineData(0x0198u, -3.102f)]
    [InlineData(0xFF38u, 3.12f)]
    public void RotationHeading(uint rot, float expected) => Assert.Equal(expected, Utf8Num.RotationToHeading(rot), 0.01f);

    [Theory]
    [InlineData(0x423F400Fu, 999999)]
    [InlineData(0x53C80000u, 0x53C8)]
    [InlineData(0x855D4002u, 0x2855D)]
    public void DamageDecoding(uint value, int expected) => Assert.Equal(expected, Effects.Amount(value));

    [Fact]
    public void FieldsSplit()
    {
        var line = "20|2026-10-05T14:33:15.2320000-05:00|4000E198|Kefka|C622|Light of Judgment|4000E198|Kefka|4.700|99.99|99.99|0.00|-3.09|1d7b"u8;
        Span<int> buf = stackalloc int[32];
        var f = new LineFields(line, buf);
        Assert.Equal(14, f.Count);
        Assert.Equal(0x4000E198u, f.Hex(F20.SourceId));
        Assert.Equal("Light of Judgment", f.Str(F20.ActionName));
        Assert.Equal(4.7f, f.Float(F20.CastTime), 0.001f);
        Assert.Equal(-3.09f, f.Float(F20.Heading), 0.001f);
        Assert.True(f[40].IsEmpty);
    }

    [Fact]
    public void FieldsSplitCapped()
    {
        Span<int> buf = stackalloc int[4];
        var f = new LineFields("a|b|c|d|e|f"u8, buf);
        Assert.Equal(3, f.Count);
        Assert.Equal("b", f.Str(1));
    }

    [Fact]
    public void ReaderHandlesCrLfLfAndPartialLastLine()
    {
        var dir = TestEnv.TempDir();
        var path = Path.Combine(dir, "x.log");
        File.WriteAllBytes(path, "01|a\r\n02|b\n03|c\r\n04|partial"u8.ToArray());
        var c = new Collect();
        var r = LogLineReader.Read(path, 0, -1, LineTypeSet.All, c);
        Assert.Equal(["01|a", "02|b", "03|c"], c.Lines);
        Assert.Equal(17, r.EndOffset); // resume point = start of the incomplete line
        File.AppendAllText(path, "\r\n");
        var c2 = new Collect();
        LogLineReader.Read(path, r.EndOffset, -1, LineTypeSet.All, c2);
        Assert.Equal(["04|partial"], c2.Lines);
    }

    [Fact]
    public void ReaderFiltersByType()
    {
        var dir = TestEnv.TempDir();
        var path = Path.Combine(dir, "x.log");
        File.WriteAllText(path, "00|chat\r\n21|ability\r\n38|status\r\n");
        var c = new Collect();
        LogLineReader.Read(path, 0, -1, new LineTypeSet(LineType.Ability), c);
        Assert.Equal(["21|ability"], c.Lines);
    }

    private sealed class Collect : ILineConsumer
    {
        public List<string> Lines { get; } = [];

        public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
        {
            Lines.Add(Encoding.UTF8.GetString(line));
            return true;
        }
    }
}
