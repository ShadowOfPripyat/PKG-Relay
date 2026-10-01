using System.Globalization;
using System.Text.Json;

namespace PkgRelay.Models;

public sealed record RpiProgress(
    long Bits,
    long Error,
    long Length,
    long Transferred,
    long LengthTotal,
    long TransferredTotal,
    int NumIndex,
    int NumTotal,
    int RestSeconds,
    int RestSecondsTotal,
    int PreparingPercent,
    int LocalCopyPercent)
{
    public double Percent
    {
        get
        {
            if (LengthTotal > 0)
            {
                return Math.Clamp(
                    (double)TransferredTotal / LengthTotal * 100.0,
                    0,
                    100);
            }

            return Math.Clamp(
                Math.Max(PreparingPercent, LocalCopyPercent),
                0,
                100);
        }
    }

    public bool LooksComplete =>
        LengthTotal > 0 &&
        TransferredTotal >= LengthTotal &&
        RestSecondsTotal <= 0;

    public static RpiProgress FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("RPI progress response must be a JSON object.");

        return new RpiProgress(
            GetLong(root, "bits"),
            GetLong(root, "error_result", "error"),
            GetLong(root, "length"),
            GetLong(root, "transferred"),
            GetLong(root, "length_total"),
            GetLong(root, "transferred_total"),
            (int)GetLong(root, "num_index"),
            (int)GetLong(root, "num_total"),
            GetRemainingSeconds(root, "rest_sec"),
            GetRemainingSeconds(root, "rest_sec_total"),
            (int)GetLong(root, "preparing_percent"),
            (int)GetLong(root, "local_copy_percent"));
    }

    private static int GetRemainingSeconds(JsonElement root, string name)
    {
        var value = GetLong(root, name);
        return value is >= 0 and < int.MaxValue ? (int)value : -1;
    }

    private static long GetLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var numeric))
                return numeric;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(text.AsSpan(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out numeric))
                    return numeric;

                if (long.TryParse(text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out numeric))
                    return numeric;
            }
        }

        return 0;
    }
}
