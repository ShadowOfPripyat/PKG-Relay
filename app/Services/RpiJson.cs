using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PkgRelay.Services;

public static class RpiJson
{
    public static JsonDocument Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new JsonException("RPI returned an empty response.");

        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(NormalizeHexNumbers(raw));
        }
    }

    private static string NormalizeHexNumbers(string input)
    {
        var output = new StringBuilder(input.Length);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (inString)
            {
                output.Append(c);

                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                output.Append(c);
                continue;
            }

            if (c == '0' &&
                i + 2 < input.Length &&
                (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                var start = i + 2;
                var end = start;

                while (end < input.Length && Uri.IsHexDigit(input[end]))
                    end++;

                if (end > start)
                {
                    var hex = input[start..end];
                    if (!ulong.TryParse(
                        hex,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var value))
                    {
                        throw new JsonException("RPI returned an unsupported hexadecimal number.");
                    }

                    output.Append(
                        value.ToString(CultureInfo.InvariantCulture));

                    i = end - 1;
                    continue;
                }
            }

            output.Append(c);
        }

        return output.ToString();
    }
}
