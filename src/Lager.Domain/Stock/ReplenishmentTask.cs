using Lager.Domain.Common;

namespace Lager.Domain.Stock;

/// <summary>
/// "Move N pieces of article X from reserve bin A to hot-pick bin B."
/// Created by the replenishment scanner when a hot-pick bin drops below its
/// configured threshold. A worker confirms it via <see cref="Complete"/>,
/// which the application service then translates into actual stock moves.
///
/// Statusmaschine (erzwungen in <see cref="Complete"/> / <see cref="Cancel"/>):
///
///   Status     | Complete()  | Cancel()
///   -----------+-------------+-----------
///   Open       | Completed   | Cancelled
///   Completed  | Fehler      | Fehler      (endgültig: Bestand ist bereits verschoben)
///   Cancelled  | Fehler      | Fehler      (endgültig)
///
/// Fehler = InvalidOperationException.
/// </summary>
public class ReplenishmentTask : Entity
{
    /// <summary>Obergrenze für gebuchte Mengen (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    public Guid ArticleId { get; private set; }
    public string ArticleSku { get; private set; } = string.Empty;
    public Guid SourceBinId { get; private set; }
    public string SourceBinCode { get; private set; } = string.Empty;
    public Guid TargetBinId { get; private set; }
    public string TargetBinCode { get; private set; } = string.Empty;
    public int SuggestedQty { get; private set; }
    public int? CompletedQty { get; private set; }
    public ReplenishmentStatus Status { get; private set; } = ReplenishmentStatus.Open;
    public DateTime? CompletedAt { get; private set; }

    private ReplenishmentTask() { }

    public ReplenishmentTask(
        Guid articleId, string articleSku,
        Guid sourceBinId, string sourceBinCode,
        Guid targetBinId, string targetBinCode,
        int suggestedQty)
    {
        if (suggestedQty <= 0) throw new ArgumentException("Menge muss > 0 sein", nameof(suggestedQty));
        ArticleId = articleId;
        ArticleSku = articleSku;
        SourceBinId = sourceBinId;
        SourceBinCode = sourceBinCode;
        TargetBinId = targetBinId;
        TargetBinCode = targetBinCode;
        SuggestedQty = suggestedQty;
    }

    public void Complete(int actualQty)
    {
        if (Status != ReplenishmentStatus.Open)
            throw new InvalidOperationException("Aufgabe nicht mehr offen");
        if (actualQty <= 0 || actualQty > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(actualQty), actualQty, $"Menge muss > 0 und <= {MaxQuantity} sein");
        CompletedQty = actualQty;
        Status = ReplenishmentStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        // Nur offene Aufgaben: nach Complete ist der Bestand bereits physisch verschoben.
        if (Status == ReplenishmentStatus.Completed)
            throw new InvalidOperationException("Abgeschlossene Aufgabe kann nicht storniert werden");
        if (Status == ReplenishmentStatus.Cancelled)
            throw new InvalidOperationException("Aufgabe ist bereits storniert");
        Status = ReplenishmentStatus.Cancelled;
        Touch();
    }
}

public enum ReplenishmentStatus
{
    Open = 0,
    Completed = 1,
    Cancelled = 9
}
