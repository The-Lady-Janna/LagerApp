using Lager.Application.Abstractions;
using Lager.Application.PickLists;
using Lager.Domain.Stock;

namespace Lager.Application.Stock;

/// <summary>Ein Teil einer Ausbuchung: die Bestandszeile (Charge/MHD) und die von ihr entnommene Menge.</summary>
public sealed record BookedPart(StockItem Row, int Quantity);

/// <summary>
/// Der einheitliche Buchungsweg für Bestandsänderungen (Wareneingang, Korrektur, Retoure, Inventur, Nachschub):
/// findet bzw. erzeugt die Bestandszeile LOT-GENAU, ändert ihre Menge und schreibt die zugehörige
/// <see cref="StockMovement"/>. Statisch und ohne DI - die Dienste reichen ihre Repositories durch. Alle Buchungen
/// laufen im selben Change-Tracker und werden vom Aufrufer mit EINEM SaveChanges persistiert.
///
/// Regeln:
///  - Bestandsidentität ist (Artikel, Lagerplatz, Charge): zwei Chargen im selben Lagerplatz sind zwei Zeilen.
///    Die Chargennummer wird normalisiert (leer/Leerraum = keine Charge = null), damit "" und null dieselbe
///    Zeile meinen (ein Unique-Index erfasst NULL nicht).
///  - Dieselbe Charge im selben Lagerplatz hat genau ein MHD: eine Zugangsbuchung mit anderem MHD wirft
///    <see cref="InvalidOperationException"/> mit dem Code lot_expiry_mismatch. Ohne MHD (null) gilt die schon
///    geführte Zeile der Charge, mit ihrem MHD; legt der Zugang eine neue Zeile an, gilt das MHD, mit dem die Charge
///    in anderen Lagerplätzen bzw. zuletzt im Ledger geführt wurde (siehe <see cref="KnownExpiryAsync"/>); eine dort
///    schon vorhandene, entleerte Zeile dieser Charge wird dabei wiederverwendet (Unique-Index).
///  - Charge und MHD im Movement stammen immer aus der betroffenen Bestandszeile, nie aus der Anfrage.
///  - Abgang über den Bestand der Zeile hinaus wirft <see cref="InvalidOperationException"/> (insufficient_stock);
///    es wird nichts still gekappt.
///
/// Fehler-Vertrag: Regelverstoß/Konflikt = InvalidOperationException, ungültige Eingabe = ArgumentException,
/// nicht gefunden = KeyNotFoundException; der maschinenlesbare Code (snake_case) steht in
/// <c>exception.Data["code"]</c>.
///
/// Grenze: Bestandszeilen mit Menge 0 sind über die lot-genaue Suche nur bei exakt gleicher Charge und MHD
/// auffindbar. Eine Zugangsbuchung derselben Charge mit ANDEREM MHD in einen Lagerplatz, dessen Zeile dieser Charge
/// leer ist, wird deshalb nicht als Konflikt erkannt (sie scheitert erst am Unique-Index). Nennt die Buchung kein MHD,
/// findet sie die leere Zeile über das bekannte MHD der Charge (Bestand anderswo, sonst Ledger).
/// </summary>
public static class StockBooking
{
    /// <summary>Obergrenze für die Menge einer einzelnen Buchung (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    /// <summary>
    /// Sucht die Bestandszeile (tracked) zu Artikel, Lagerplatz, Charge und MHD, auch mit Menge 0. Ohne MHD wird eine
    /// schon geführte Zeile derselben Charge verwendet (die Charge behält ihr MHD); nennt die Anfrage ein anderes MHD
    /// als die geführte Zeile, wirft die Methode lot_expiry_mismatch. Kein Treffer = null.
    /// </summary>
    public static async Task<StockItem?> FindAsync(IStockRepository stock, Guid articleId, Guid locationId,
        string? lotNumber, DateTime? expiryDate, CancellationToken ct = default)
    {
        var lot = StockItem.NormalizeLot(lotNumber);
        var exact = await stock.FindAsync(articleId, locationId, lot, expiryDate, ct);
        if (exact is not null || lot is null) return exact;

        var sameLot = (await stock.ListForBinAsync(articleId, locationId, ct))
            .Where(r => StockItem.NormalizeLot(r.LotNumber) == lot)
            .ToList();
        if (sameLot.Count == 0) return null;

        var sameDay = sameLot.FirstOrDefault(r => r.ExpiryDate?.Date == expiryDate?.Date);
        if (sameDay is not null) return sameDay;
        if (expiryDate is null) return sameLot[0];

        var held = sameLot[0].ExpiryDate;
        throw StockErrors.Conflict("lot_expiry_mismatch",
            $"Charge {lot} ist in diesem Lagerplatz " +
            (held is null ? "ohne MHD" : $"mit MHD {held:yyyy-MM-dd}") +
            $" geführt, gebucht werden soll sie mit MHD {expiryDate:yyyy-MM-dd} - eine Charge hat genau ein MHD");
    }

    /// <summary>
    /// Das MHD, mit dem die Charge des Artikels geführt wird: zuerst aus dem Bestand über alle Lagerplätze; gibt es dort
    /// keine Zeile der Charge mit MHD (z. B. ganz ausverkauft), aus dem Ledger. Nur wenn alle Fundstellen dasselbe MHD
    /// tragen, sonst null (auch ohne Charge). Für Buchungen, die das MHD nicht nennen (Retouren) - eine Charge hat genau
    /// ein MHD, FEFO soll die Ware nicht als "ohne MHD" ans Ende stellen.
    /// </summary>
    public static async Task<DateTime?> KnownExpiryAsync(IStockRepository stock, IStockMovementRepository movements,
        Guid articleId, string? lotNumber, CancellationToken ct = default)
    {
        var lot = StockItem.NormalizeLot(lotNumber);
        if (lot is null) return null;
        var expiries = (await stock.ListForArticleAsync(articleId, ct))
            .Where(r => StockItem.NormalizeLot(r.LotNumber) == lot && r.ExpiryDate is not null)
            .Select(r => r.ExpiryDate!.Value)
            .ToList();
        if (expiries.Count == 0)
            expiries = (await movements.ListExpiriesForLotAsync(articleId, lot, ct)).ToList();
        return expiries.Select(e => e.Date).Distinct().Count() == 1 ? expiries[0] : null;
    }

    /// <summary>
    /// Bucht <paramref name="delta"/> (positiv = Zugang, negativ = Abgang) auf die lot-genaue Bestandszeile und
    /// schreibt die Movement. Ein Zugang erzeugt die Zeile bei Bedarf; ein Abgang braucht die Zeile mit genug Bestand.
    /// Liefert die betroffene Zeile.
    /// </summary>
    /// <param name="costCents">Kosten-Snapshot je Stück (Cent) für die Bewertung.</param>
    public static async Task<StockItem> BookAsync(
        IStockRepository stock, IStockMovementRepository movements,
        Guid articleId, Guid locationId, int delta, StockMovementReason reason,
        string? referenceType, Guid? referenceId,
        string? lotNumber, DateTime? expiryDate, int costCents,
        CancellationToken ct = default)
    {
        ValidateDelta(delta);

        var row = await FindAsync(stock, articleId, locationId, lotNumber, expiryDate, ct);
        if (row is null)
        {
            if (delta < 0)
                throw StockErrors.Conflict("stock_not_found",
                    "Kein Bestand zu diesem Artikel" + DescribeLot(lotNumber, expiryDate) + " im Lagerplatz vorhanden");

            if (expiryDate is null)
            {
                // Ohne MHD (Retoure): das bekannte MHD der Charge übernehmen. Gibt es in diesem Lagerplatz schon eine
                // (entleerte) Zeile der Charge mit diesem MHD, wird sie wiederverwendet - der Unique-Index auf
                // (Artikel, Lagerplatz, Charge) lässt keine zweite Zeile zu.
                var known = await KnownExpiryAsync(stock, movements, articleId, lotNumber, ct);
                if (known is not null)
                {
                    expiryDate = known;
                    row = await stock.FindAsync(articleId, locationId, lotNumber, known, ct);
                }
            }

            if (row is null)
            {
                row = new StockItem(articleId, locationId, delta, lotNumber, expiryDate);
                await stock.AddAsync(row, ct);
                await AddMovementAsync(movements, row, delta, reason, referenceType, referenceId, costCents, ct);
                return row;
            }
        }

        await BookOnAsync(movements, row, delta, reason, referenceType, referenceId, costCents, ct);
        return row;
    }

    /// <summary>
    /// Wie <see cref="BookAsync"/> auf einer bereits gefundenen Bestandszeile (spart die zweite Suche).
    /// </summary>
    public static async Task BookOnAsync(
        IStockMovementRepository movements, StockItem row, int delta, StockMovementReason reason,
        string? referenceType, Guid? referenceId, int costCents,
        CancellationToken ct = default)
    {
        ValidateDelta(delta);
        if (delta > 0)
        {
            row.Add(delta);
        }
        else
        {
            if (row.Quantity < -delta)
                throw StockErrors.Conflict("insufficient_stock",
                    $"Nicht genug Bestand: {row.Quantity} vorhanden, {-delta} benötigt" + DescribeLot(row.LotNumber, row.ExpiryDate));
            row.Remove(-delta);
        }

        await AddMovementAsync(movements, row, delta, reason, referenceType, referenceId, costCents, ct);
    }

    /// <summary>
    /// Bucht <paramref name="quantity"/> Stück aus einem Lagerplatz aus, verteilt auf seine Bestandszeilen (Chargen):
    /// nicht abgelaufene Ware zuerst, darin FEFO (frühestes MHD, ohne MHD zuletzt). Je Zeile eine Movement mit deren
    /// Charge/MHD. Alles oder nichts: reicht der Bestand des Lagerplatzes nicht, wirft die Methode
    /// insufficient_stock, ohne etwas zu ändern. Liefert die entnommenen Teile (für die Gegenbuchung beim Umlagern).
    /// </summary>
    /// <param name="lotlessFirst">Die Zeile ohne Charge/MHD zuerst leeren, dann FEFO (Korrekturen ohne Chargenangabe).</param>
    public static async Task<IReadOnlyList<BookedPart>> BookOutAsync(
        IStockRepository stock, IStockMovementRepository movements,
        Guid articleId, Guid locationId, int quantity, StockMovementReason reason,
        string? referenceType, Guid? referenceId, int costCents,
        bool lotlessFirst = false, CancellationToken ct = default)
    {
        ValidateDelta(quantity);
        if (quantity < 0) throw StockErrors.Invalid("quantity_invalid", "Die auszubuchende Menge muss größer 0 sein");

        var rows = StockAllocator.OrderForBooking(await stock.ListForBinAsync(articleId, locationId, ct))
            .Where(r => r.Quantity > 0)
            .ToList();
        if (lotlessFirst)
            rows = rows.OrderBy(r => r.LotNumber is null && r.ExpiryDate is null ? 0 : 1).ToList(); // stabil: FEFO bleibt darin erhalten

        long available = 0;
        foreach (var r in rows) available += r.Quantity;
        if (available < quantity)
            throw StockErrors.Conflict("insufficient_stock", $"Nicht genug Bestand: {available} vorhanden, {quantity} benötigt");

        var parts = new List<BookedPart>();
        var missing = quantity;
        foreach (var row in rows)
        {
            if (missing == 0) break;
            var take = Math.Min(missing, row.Quantity);
            await BookOnAsync(movements, row, -take, reason, referenceType, referenceId, costCents, ct);
            parts.Add(new BookedPart(row, take));
            missing -= take;
        }
        return parts;
    }

    /// <summary>
    /// Sperr-/Ausschussbuchung: schreibt Zugang und Abgang derselben Menge (netto 0) mit Charge und MHD, ohne eine
    /// Bestandszeile zu ändern. Die Ware taucht im Ledger auf (Reason ReturnB bzw. ReturnScrap), erhöht den
    /// Verkaufsbestand aber nicht - die Summe der Deltas je Artikel bleibt gleich der Summe der Bestandsmengen.
    /// </summary>
    public static async Task RecordBlockedAsync(
        IStockRepository stock, IStockMovementRepository movements, Guid articleId, Guid locationId, int quantity,
        StockMovementReason reason, string? referenceType, Guid? referenceId,
        string? lotNumber, DateTime? expiryDate, int costCents,
        CancellationToken ct = default)
    {
        ValidateDelta(quantity);
        if (quantity < 0) throw StockErrors.Invalid("quantity_invalid", "Die Menge muss größer 0 sein");

        var lot = StockItem.NormalizeLot(lotNumber);
        expiryDate ??= await KnownExpiryAsync(stock, movements, articleId, lot, ct);
        var cost = Math.Max(0, costCents);
        await movements.AddAsync(new StockMovement(articleId, locationId, +quantity, cost, reason,
            referenceType, referenceId, lot, expiryDate), ct);
        await movements.AddAsync(new StockMovement(articleId, locationId, -quantity, cost, reason,
            referenceType, referenceId, lot, expiryDate), ct);
    }

    private static Task AddMovementAsync(IStockMovementRepository movements, StockItem row, int delta,
        StockMovementReason reason, string? referenceType, Guid? referenceId, int costCents, CancellationToken ct) =>
        movements.AddAsync(new StockMovement(
            row.ArticleId, row.StorageLocationId, delta, Math.Max(0, costCents),
            reason, referenceType, referenceId, row.LotNumber, row.ExpiryDate), ct);

    private static void ValidateDelta(int delta)
    {
        if (delta == 0)
            throw StockErrors.Invalid("quantity_zero", "Die Menge darf nicht 0 sein - nichts zu buchen");
        if (Math.Abs((long)delta) > MaxQuantity)
            throw StockErrors.Invalid("quantity_out_of_range", $"Die Menge darf höchstens {MaxQuantity} betragen");
    }

    private static string DescribeLot(string? lotNumber, DateTime? expiryDate)
    {
        var lot = StockItem.NormalizeLot(lotNumber);
        if (lot is null && expiryDate is null) return string.Empty;
        return " (" + (lot is null ? "ohne Charge" : $"Charge {lot}") + (expiryDate is null ? string.Empty : $", MHD {expiryDate:yyyy-MM-dd}") + ")";
    }
}

/// <summary>
/// Fehler nach dem Vertrag der Fachfehler: Typ nach Art des Verstoßes, maschinenlesbarer Code in
/// <c>exception.Data["code"]</c> (snake_case). Die zentrale Fehlerabbildung der API macht daraus 409/400/404.
/// </summary>
internal static class StockErrors
{
    public static InvalidOperationException Conflict(string code, string message) =>
        With(new InvalidOperationException(message), code);

    public static ArgumentException Invalid(string code, string message, string? paramName = null) =>
        With(paramName is null ? new ArgumentException(message) : new ArgumentException(message, paramName), code);

    public static KeyNotFoundException NotFound(string code, string message) =>
        With(new KeyNotFoundException(message), code);

    private static T With<T>(T exception, string code) where T : Exception
    {
        exception.Data["code"] = code;
        return exception;
    }
}
