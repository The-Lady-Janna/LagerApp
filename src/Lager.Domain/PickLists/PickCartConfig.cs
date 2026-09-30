using Lager.Domain.Common;

namespace Lager.Domain.PickLists;

/// <summary>
/// Template/schema for a picking cart: how many vertical levels the cart has,
/// and the inner dimensions of each level. Used to compute when a multi-order
/// "fill the cart" pick list reaches capacity.
/// </summary>
public class PickCartConfig : Entity
{
    public string Name { get; private set; } = string.Empty;
    public int LevelCount { get; private set; }
    public int LevelHeightMm { get; private set; }
    public int LevelWidthMm { get; private set; }
    public int LevelDepthMm { get; private set; }
    public int MaxWeightGrams { get; private set; }

    public long TotalVolumeMm3 => (long)LevelCount * LevelWidthMm * LevelDepthMm * LevelHeightMm;

    private PickCartConfig() { }

    public PickCartConfig(string name, int levelCount, int levelWidthMm, int levelDepthMm, int levelHeightMm, int maxWeightGrams)
    {
        Update(name, levelCount, levelWidthMm, levelDepthMm, levelHeightMm, maxWeightGrams);
    }

    public void Update(string name, int levelCount, int levelWidthMm, int levelDepthMm, int levelHeightMm, int maxWeightGrams)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required", nameof(name));
        if (levelCount <= 0) throw new ArgumentOutOfRangeException(nameof(levelCount));
        if (levelWidthMm <= 0 || levelDepthMm <= 0 || levelHeightMm <= 0) throw new ArgumentOutOfRangeException("Level dimensions must be positive");
        if (maxWeightGrams <= 0) throw new ArgumentOutOfRangeException(nameof(maxWeightGrams));

        Name = name.Trim();
        LevelCount = levelCount;
        LevelWidthMm = levelWidthMm;
        LevelDepthMm = levelDepthMm;
        LevelHeightMm = levelHeightMm;
        MaxWeightGrams = maxWeightGrams;
        Touch();
    }
}
