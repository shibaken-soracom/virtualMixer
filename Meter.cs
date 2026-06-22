namespace VirtualMixer;

/// <summary>
/// Level-meter math shared by the live view and the snapshot printer. The bar is scaled
/// on a dBFS axis (FloorDb..0) rather than linearly, so quiet signals are still visible.
/// </summary>
public static class Meter
{
    public const double FloorDb = -60.0;

    /// <summary>Peak (linear 0..1) as dBFS; negative infinity for digital silence.</summary>
    public static double Db(float peak) =>
        peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);

    /// <summary>Bar fill fraction 0..1 on the FloorDb..0 dBFS scale.</summary>
    public static double Fraction(float peak)
    {
        double db = Db(peak);
        if (double.IsNegativeInfinity(db)) return 0;
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>Right-aligned dBFS text, e.g. "-12.3" or "  -inf" (5 chars wide).</summary>
    public static string DbText(float peak)
    {
        double db = Db(peak);
        return double.IsNegativeInfinity(db) ? "  -inf" : $"{db,5:0.0}";
    }

    /// <summary>Plain (uncoloured) bar string of the given cell width using block glyphs.</summary>
    public static string Bar(float peak, int width)
    {
        int filled = (int)Math.Round(Fraction(peak) * width);
        filled = Math.Clamp(filled, 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }
}
