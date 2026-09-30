using Lager.Domain.Common;

namespace Lager.Domain.Stock;

public class StockItem : Entity
{
    public Guid ArticleId { get; private set; }
    public Guid StorageLocationId { get; private set; }
    public int Quantity { get; private set; }
    public string? LotNumber { get; private set; }
    public DateTime? ExpiryDate { get; private set; }

    private StockItem() { }

    public StockItem(Guid articleId, Guid storageLocationId, int quantity, string? lotNumber = null, DateTime? expiryDate = null)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        ArticleId = articleId;
        StorageLocationId = storageLocationId;
        Quantity = quantity;
        LotNumber = NormalizeLot(lotNumber);
        ExpiryDate = expiryDate;
    }

    /// <summary>
    /// Vereinheitlicht die Chargennummer: leer bzw. nur Leerraum wird zu null, sonst getrimmt.
    /// So bezeichnen "" und null dieselbe (chargenlose) Bestandszeile.
    /// </summary>
    public static string? NormalizeLot(string? lotNumber) =>
        string.IsNullOrWhiteSpace(lotNumber) ? null : lotNumber.Trim();

    /// <summary>
    /// Abgelaufen, wenn das MHD vor dem heutigen Tag (UTC) liegt. Am Ablauftag selbst ist die Ware noch verwendbar.
    /// </summary>
    public bool IsExpired(DateTime? todayUtc = null) =>
        ExpiryDate is DateTime expiry && expiry.Date < (todayUtc ?? DateTime.UtcNow).Date;

    public void Add(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > int.MaxValue - Quantity)
            throw new InvalidOperationException($"Bestand würde den Maximalwert überschreiten ({Quantity} + {amount})");
        Quantity += amount;
        Touch();
    }

    public void Remove(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > Quantity) throw new InvalidOperationException($"Cannot remove {amount} from stock of {Quantity}");
        Quantity -= amount;
        Touch();
    }
}
