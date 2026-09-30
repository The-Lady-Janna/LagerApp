using Lager.Domain.Stock;

namespace Lager.Application.Abstractions;

public interface IStockMovementRepository : IRepository<StockMovement>
{
    /// <summary>
    /// Read-only: die verschiedenen MHD, mit denen die Charge des Artikels im Ledger gebucht wurde (nur Buchungen mit
    /// MHD). Für Buchungen ohne MHD-Angabe (Retouren): eine Charge hat genau ein MHD, auch wenn ihr Bestand ausverkauft ist.
    /// </summary>
    Task<IReadOnlyList<DateTime>> ListExpiriesForLotAsync(Guid articleId, string lotNumber, CancellationToken ct = default);
}
