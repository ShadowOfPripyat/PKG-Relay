using System.Buffers.Binary;
using System.Text.Json;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class SendingRegressionTests
{
    [Fact]
    public void ValidJsonStringsAreNeverNormalized()
    {
        const string json = """{"message":"error:0xFF, retry"}""";
        using var document = RpiJson.Parse(json);
        Assert.Equal("error:0xFF, retry", document.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{"message":"error:0xFF, retry","length_total":0x1000}""")]
    [InlineData("""{"message":"error:0xFF, retry","length_total":0X1000}""")]
    public void HexNormalizationPreservesStrings(string json)
    {
        using var document = RpiJson.Parse(json);
        Assert.Equal("error:0xFF, retry", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(4096, document.RootElement.GetProperty("length_total").GetInt64());
    }

    [Theory]
    [InlineData("error_result")]
    [InlineData("error")]
    public void ReadsQuotedHexProgressAndErrors(string errorName)
    {
        using var document = RpiJson.Parse($$"""{"{{errorName}}":"0x80990088","length_total":"0x2000","transferred_total":"0X1000"}""");
        var progress = RpiProgress.FromJson(document.RootElement);
        Assert.NotEqual(0, progress.Error);
        Assert.Equal(8192, progress.LengthTotal);
        Assert.Equal(4096, progress.TransferredTotal);
        Assert.Equal(50, progress.Percent);
    }

    [Theory]
    [InlineData(-25, 0)]
    [InlineData(125, 100)]
    public void PreparationProgressStaysWithinBounds(int value, double expected)
    {
        using var document = JsonDocument.Parse($$"""{"preparing_percent":{{value}}}""");
        Assert.Equal(expected, RpiProgress.FromJson(document.RootElement).Percent);
    }

    [Fact]
    public async Task HeaderPatchFlagsIdentifyUpdatesWithoutSfo()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pkg");
        try
        {
            var bytes = CreatePkgHeader();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x78), 0x00100000);
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Equal(PkgKind.Update, (await new PkgMetadataService().ReadAsync(path)).Kind);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidSfoOffsetsDoNotCrashMetadataReading(bool corruptKeyOffset)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pkg");
        try
        {
            var bytes = CreatePkgHeader();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x10), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), 0x1000);
            Array.Resize(ref bytes, 0x1100);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x430), (ulong)bytes.Length);
            var entry = bytes.AsSpan(0x1000);
            BinaryPrimitives.WriteUInt32BigEndian(entry, 0x1000);
            BinaryPrimitives.WriteUInt32BigEndian(entry[0x10..], 0x1020);
            BinaryPrimitives.WriteUInt32BigEndian(entry[0x14..], 0x80);
            var sfo = bytes.AsSpan(0x1020);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo, 0x46535000);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo[8..], corruptKeyOffset ? uint.MaxValue : 0x24);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo[12..], 0x40);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo[16..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(sfo[22..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo[24..], 8);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo[32..], corruptKeyOffset ? 0 : uint.MaxValue);
            "TITLE\0"u8.CopyTo(sfo[0x24..]);
            await File.WriteAllBytesAsync(path, bytes);
            var metadata = await new PkgMetadataService().ReadAsync(path);
            Assert.NotNull(metadata);
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreatePkgHeader()
    {
        var bytes = new byte[0x1000];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x7F434E54);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x430), (ulong)bytes.Length);
        return bytes;
    }
}
