using System;

/// <summary>
/// One colour per chain, derived from the chain's id alone, so that it stays the same when other chains are added, removed or
/// renamed. A map can have several hundred chains, so the colours are spread over hue, saturation and brightness together with a
/// low-discrepancy sequence (the R3 sequence, based on the plastic number): consecutive ids land far apart and no two ids repeat.
/// Plain floats, no engine types, so the rule can be tested without Unity.
/// </summary>
public static class SC2ChainPalette
{
    // 1/g, 1/g^2, 1/g^3 for g = the plastic number (the real root of g^3 = g + 1)
    private const double StepHue = 0.8191725133961645;
    private const double StepSaturation = 0.6710436067037893;
    private const double StepValue = 0.5497004779019703;

    private const float MinSaturation = 0.55f, MaxSaturation = 1f;
    private const float MinValue = 0.72f, MaxValue = 1f;

    public static void Rgb(int chainId, out float r, out float g, out float b)
    {
        var hue = Fraction(0.5 + StepHue * chainId);
        var saturation = MinSaturation + (MaxSaturation - MinSaturation) * (float)Fraction(0.5 + StepSaturation * chainId);
        var value = MinValue + (MaxValue - MinValue) * (float)Fraction(0.5 + StepValue * chainId);
        HsvToRgb((float)hue, saturation, value, out r, out g, out b);
    }

    /// <summary>Reads "rrggbb" (a leading # is allowed). False for anything else, so a damaged value falls back to the automatic colour.</summary>
    public static bool TryParseHex(string hex, out float r, out float g, out float b)
    {
        r = g = b = 0f;
        if (string.IsNullOrEmpty(hex)) return false;
        var text = hex[0] == '#' ? hex.Substring(1) : hex;
        if (text.Length != 6) return false;
        if (!int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var packed)) return false;
        r = ((packed >> 16) & 0xFF) / 255f;
        g = ((packed >> 8) & 0xFF) / 255f;
        b = (packed & 0xFF) / 255f;
        return true;
    }

    public static string ToHex(float r, float g, float b) =>
        Channel(r).ToString("x2") + Channel(g).ToString("x2") + Channel(b).ToString("x2");

    private static int Channel(float v) => (int)Math.Round(Math.Min(1f, Math.Max(0f, v)) * 255f);

    private static double Fraction(double x) => x - Math.Floor(x);

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        var sector = h * 6f;
        var index = (int)Math.Floor(sector) % 6;
        var f = sector - (float)Math.Floor(sector);
        var p = v * (1f - s);
        var q = v * (1f - s * f);
        var t = v * (1f - s * (1f - f));
        switch (index)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }
}
