using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PkgRelay.Models;

namespace PkgRelay.Services;

/// <summary>
/// Reads only the unencrypted metadata area of a PS4 PKG. It does not decrypt
/// the PFS payload. The PKG header and entry table are big-endian; param.sfo
/// is little-endian.
/// </summary>
public sealed partial class PkgMetadataService
{
    private const uint Ps4PkgMagic = 0x7F434E54;
    private const uint ParamSfoEntryId = 0x00001000;
    private const uint ParamSfoMagic = 0x46535000; // bytes: 00 50 53 46 (\0PSF)
    private const int PkgEntrySize = 0x20;
    private const int MaxSfoSize = 4 * 1024 * 1024;
    private const int PkgHeaderProbeSize = 0x438;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<PkgMetadata> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);

        if (!info.Exists)
            throw new FileNotFoundException("PKG file not found.", fullPath);

        if (!string.Equals(info.Extension, ".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not a .pkg file.");

        if (_cache.TryGetValue(fullPath, out var cached) &&
            cached.Length == info.Length &&
            cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            return cached.Metadata;
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (stream.Length < PkgHeaderProbeSize)
            return Cache(fullPath, info, Invalid(fullPath, info.Length));

        var header = new byte[PkgHeaderProbeSize];
        var headerRead = await ReadExactlyOrLessAsync(
            stream,
            header,
            0,
            cancellationToken);

        if (headerRead < PkgHeaderProbeSize)
            return Cache(fullPath, info, Invalid(fullPath, info.Length));

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x00, 4));
        if (magic != Ps4PkgMagic)
            return Cache(fullPath, info, Invalid(fullPath, info.Length));

        var declaredPackageSize =
            BinaryPrimitives.ReadUInt64BigEndian(
                header.AsSpan(0x430, 8));

        if (declaredPackageSize > 0 &&
            declaredPackageSize > (ulong)info.Length)
        {
            throw new InvalidDataException(
                $"PKG is truncated. Header declares {declaredPackageSize:N0} bytes, " +
                $"but the file contains only {info.Length:N0} bytes.");
        }

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10, 4));
        var entryTableOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18, 4));

        // The content ID field begins at 0x40 in the complete PS4 PKG header.
        // Older simplified readers often use 0x30; accept both and prefer the
        // value that actually resembles a PS4 content ID.
        var contentId = ReadAscii(header, 0x40, 0x30);
        if (!LooksLikeContentId(contentId))
            contentId = ReadAscii(header, 0x30, 0x24);

        var titleId = ExtractTitleId(contentId);
        if (string.IsNullOrWhiteSpace(titleId))
            titleId = ExtractTitleId(Path.GetFileName(filePath));

        uint contentFlags = 0;
        // content_flags is at 0x78 in the common PS4 PKG header layout.
        if (headerRead >= 0x7C)
            contentFlags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x78, 4));

        var sfo = await TryReadParamSfoAsync(
            stream,
            entryTableOffset,
            entryCount,
            cancellationToken);

        var title = GetSfoString(sfo, "TITLE");
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(filePath);

        var sfoTitleId = GetSfoString(sfo, "TITLE_ID");
        if (!string.IsNullOrWhiteSpace(sfoTitleId))
            titleId = sfoTitleId.Trim().ToUpperInvariant();

        var sfoContentId = GetSfoString(sfo, "CONTENT_ID");
        if (!string.IsNullOrWhiteSpace(sfoContentId))
            contentId = sfoContentId.Trim();

        var category = GetSfoString(sfo, "CATEGORY").Trim().ToLowerInvariant();
        var appVersion = GetSfoString(sfo, "APP_VER").Trim();
        var version = GetSfoString(sfo, "VERSION").Trim();
        var systemVersion = GetSfoUInt32(sfo, "SYSTEM_VER");

        var kind = InferKind(category, contentFlags, Path.GetFileName(filePath));

        var metadata = new PkgMetadata(
            true,
            title,
            titleId,
            contentId,
            kind,
            info.Length,
            category,
            appVersion,
            version,
            systemVersion,
            FormatSystemVersion(systemVersion),
            contentFlags);

        return Cache(fullPath, info, metadata);
    }

    private PkgMetadata Cache(
        string fullPath,
        FileInfo info,
        PkgMetadata metadata)
    {
        _cache[fullPath] =
            new CacheEntry(
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                metadata);

        return metadata;
    }

    private static PkgMetadata Invalid(string filePath, long length) =>
        new(
            false,
            Path.GetFileNameWithoutExtension(filePath),
            ExtractTitleId(Path.GetFileName(filePath)),
            "",
            PkgKind.Unknown,
            length);

    private static async Task<Dictionary<string, SfoValue>> TryReadParamSfoAsync(
        FileStream stream,
        uint tableOffset,
        uint entryCount,
        CancellationToken cancellationToken)
    {
        if (entryCount == 0 || entryCount > 16_384)
            return [];

        var tableSize = checked((long)entryCount * PkgEntrySize);
        if (tableOffset > stream.Length || tableSize > stream.Length - tableOffset)
            return [];

        var entryBuffer = new byte[PkgEntrySize];

        for (uint index = 0; index < entryCount; index++)
        {
            var position = checked((long)tableOffset + (long)index * PkgEntrySize);
            stream.Position = position;

            if (await ReadExactlyOrLessAsync(
                    stream,
                    entryBuffer,
                    0,
                    cancellationToken) != PkgEntrySize)
            {
                return [];
            }

            var id = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(0x00, 4));
            if (id != ParamSfoEntryId)
                continue;

            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(0x10, 4));
            var dataSize = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(0x14, 4));

            if (dataSize == 0 || dataSize > MaxSfoSize)
                return [];

            if (dataOffset > stream.Length || dataSize > stream.Length - dataOffset)
                return [];

            var data = new byte[checked((int)dataSize)];
            stream.Position = dataOffset;

            if (await ReadExactlyOrLessAsync(
                    stream,
                    data,
                    0,
                    cancellationToken) != data.Length)
            {
                return [];
            }

            return ParseSfo(data);
        }

        return [];
    }

    private static Dictionary<string, SfoValue> ParseSfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x14)
            return [];

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (magic != ParamSfoMagic)
            return [];

        var keyTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x08, 4));
        var dataTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x0C, 4));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x10, 4));

        if (entryCount > 4096)
            return [];

        const int entrySize = 0x10;
        var indexTableEnd = 0x14L + (long)entryCount * entrySize;

        if (indexTableEnd > data.Length ||
            keyTableOffset >= data.Length ||
            dataTableOffset >= data.Length)
        {
            return [];
        }

        var result = new Dictionary<string, SfoValue>(StringComparer.OrdinalIgnoreCase);

        for (uint index = 0; index < entryCount; index++)
        {
            var entryOffset = checked(0x14 + (int)index * entrySize);
            var entry = data.Slice(entryOffset, entrySize);

            var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(0x00, 2));
            var format = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(0x02, 2));
            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0x04, 4));
            var valueMaxLength = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0x08, 4));
            var valueOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0x0C, 4));

            var keyAbsolute = checked((long)keyTableOffset + keyOffset);
            if (keyAbsolute < 0 || keyAbsolute >= data.Length)
                continue;

            var key = ReadNullTerminatedUtf8(data, (int)keyAbsolute, data.Length - (int)keyAbsolute);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var valueAbsolute = checked((long)dataTableOffset + valueOffset);
            if (valueAbsolute < 0 || valueAbsolute >= data.Length)
                continue;

            var available = data.Length - (int)valueAbsolute;
            var declaredLength = (long)valueLength;
            var declaredMaxLength = valueMaxLength == 0
                ? declaredLength
                : (long)valueMaxLength;

            var boundedLength = checked((int)Math.Min(
                Math.Min(declaredLength, declaredMaxLength),
                (long)available));

            if (boundedLength < 0)
                continue;

            object? value;

            // Standard PSF integer format.
            if (format == 0x0404 && available >= 4)
            {
                value = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.Slice((int)valueAbsolute, 4));
            }
            else
            {
                value = ReadNullTerminatedUtf8(
                    data,
                    (int)valueAbsolute,
                    boundedLength);
            }

            result[key] = new SfoValue(format, value);
        }

        return result;
    }

    private static string GetSfoString(
        IReadOnlyDictionary<string, SfoValue> sfo,
        string key)
    {
        if (!sfo.TryGetValue(key, out var value) || value.Value is null)
            return "";

        return value.Value switch
        {
            string text => text,
            uint number => number.ToString(),
            _ => value.Value.ToString() ?? ""
        };
    }

    private static uint GetSfoUInt32(
        IReadOnlyDictionary<string, SfoValue> sfo,
        string key)
    {
        if (!sfo.TryGetValue(key, out var value) || value.Value is null)
            return 0;

        if (value.Value is uint number)
            return number;

        if (value.Value is string text && uint.TryParse(text, out number))
            return number;

        return 0;
    }

    private static string ReadNullTerminatedUtf8(
        ReadOnlySpan<byte> data,
        int offset,
        int maxLength)
    {
        if (offset < 0 || offset >= data.Length || maxLength <= 0)
            return "";

        var length = Math.Min(maxLength, data.Length - offset);
        var slice = data.Slice(offset, length);
        var terminator = slice.IndexOf((byte)0);

        if (terminator >= 0)
            slice = slice[..terminator];

        return Encoding.UTF8.GetString(slice).Trim();
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length || length <= 0)
            return "";

        length = Math.Min(length, data.Length - offset);

        return Encoding.ASCII
            .GetString(data, offset, length)
            .TrimEnd('\0', ' ');
    }

    private static bool LooksLikeContentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16)
            return false;

        return TitleIdRegex().IsMatch(value) && value.Contains('-');
    }

    private static string ExtractTitleId(string value)
    {
        var match = TitleIdRegex().Match(value ?? "");
        return match.Success ? match.Value.ToUpperInvariant() : "";
    }

    private static PkgKind InferKind(
        string category,
        uint contentFlags,
        string fileName)
    {
        // Common PS4 param.sfo categories:
        // gd = game/application data, gp = game patch, ac = additional content.
        if (category.Equals("gp", StringComparison.OrdinalIgnoreCase))
            return PkgKind.Update;

        if (category.Equals("ac", StringComparison.OrdinalIgnoreCase))
            return PkgKind.Dlc;

        // PKG content flags can also identify patch packages.
        const uint FirstPatch = 0x00100000;
        const uint SubsequentPatch = 0x40000000;
        const uint DeltaPatchMask = 0x41000000;
        const uint CumulativePatchMask = 0x60000000;

        if ((contentFlags & FirstPatch) != 0 ||
            (contentFlags & SubsequentPatch) != 0 ||
            (contentFlags & DeltaPatchMask) == DeltaPatchMask ||
            (contentFlags & CumulativePatchMask) == CumulativePatchMask)
        {
            return PkgKind.Update;
        }

        if (category.Equals("gd", StringComparison.OrdinalIgnoreCase))
            return PkgKind.Base;

        var name = fileName.ToLowerInvariant();

        if (name.Contains("update") || name.Contains("patch"))
            return PkgKind.Update;

        if (name.Contains("dlc") || name.Contains("addon") || name.Contains("add-on"))
            return PkgKind.Dlc;

        return PkgKind.Base;
    }

    private static string FormatSystemVersion(uint raw)
    {
        if (raw == 0)
            return "";

        // PS4 SYSTEM_VER encodes the visible major/minor fields in the upper bytes.
        var major = (raw >> 24) & 0xFF;
        var minor = (raw >> 16) & 0xFF;
        var patch = (raw >> 8) & 0xFF;

        return patch == 0
            ? $"{major}.{minor:00}"
            : $"{major}.{minor:00}.{patch:00}";
    }

    private static async Task<int> ReadExactlyOrLessAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length - offset)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset + total, buffer.Length - offset - total),
                cancellationToken);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private sealed record CacheEntry(
        long Length,
        long LastWriteUtcTicks,
        PkgMetadata Metadata);

    private sealed record SfoValue(ushort Format, object? Value);

    [GeneratedRegex(@"CUSA\d{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdRegex();
}
