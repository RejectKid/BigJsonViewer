using System.Globalization;

namespace BigJsonViewer.CorpusGenerator;

public static class SizeParser
{
    public const long Kibibyte = 1L << 10;
    public const long Mebibyte = 1L << 20;
    public const long Gibibyte = 1L << 30;
    public const long Tebibyte = 1L << 40;

    private static readonly Dictionary<string, long> Multipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 1,
        ["B"] = 1,
        ["KB"] = 1_000,
        ["MB"] = 1_000_000,
        ["GB"] = 1_000_000_000,
        ["TB"] = 1_000_000_000_000,
        ["KIB"] = Kibibyte,
        ["MIB"] = Mebibyte,
        ["GIB"] = Gibibyte,
        ["TIB"] = Tebibyte
    };

    public static long Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        var split = 0;
        while (split < trimmed.Length && (char.IsDigit(trimmed[split]) || trimmed[split] is '.' or ','))
        {
            split++;
        }

        if (split == 0 || !decimal.TryParse(trimmed[..split], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            throw new FormatException($"Invalid size '{value}'. Examples: 500MB, 2GiB, 1048576.");
        }

        var suffix = trimmed[split..].Trim();
        if (!Multipliers.TryGetValue(suffix, out var multiplier))
        {
            throw new FormatException($"Unknown size suffix '{suffix}'. Use B, KB, MB, GB, TB, KiB, MiB, GiB, or TiB.");
        }

        var bytes = amount * multiplier;
        if (bytes < 1 || bytes > long.MaxValue || bytes != decimal.Truncate(bytes))
        {
            throw new FormatException($"Size '{value}' does not resolve to a positive whole number of bytes.");
        }

        return (long)bytes;
    }
}
