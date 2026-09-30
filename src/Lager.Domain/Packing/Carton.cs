namespace Lager.Domain.Packing;

/// <summary>
/// Kartontyp. Die Abmessungen sind Innenmaße. <paramref name="MaxWeightGrams"/> ist das zulässige
/// Bruttogewicht (Tara des Kartons plus Inhalt); die Nutzlast ist <see cref="MaxPayloadGrams"/>.
/// </summary>
public record CartonType(string Name, int InnerLengthMm, int InnerWidthMm, int InnerHeightMm, int MaxWeightGrams, int TareWeightGrams)
{
    public long InnerVolumeMm3 => (long)InnerLengthMm * InnerWidthMm * InnerHeightMm;

    /// <summary>Nutzlast in Gramm: zulässiges Bruttogewicht minus Tara.</summary>
    public int MaxPayloadGrams => MaxWeightGrams - TareWeightGrams;
}

public static class StandardCartons
{
    public static readonly IReadOnlyList<CartonType> All = new[]
    {
        new CartonType("S", 200, 150, 100, 5_000, 100),
        new CartonType("M", 400, 300, 200, 15_000, 200),
        new CartonType("L", 600, 400, 300, 30_000, 400),
        new CartonType("XL", 800, 600, 400, 60_000, 700),
    };
}
