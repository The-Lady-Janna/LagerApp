using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence;

/// <summary>
/// Zeile der Zähler-Tabelle "PickListSequence". Jede Zeile ist ein eigener Nummernkreis (siehe
/// <see cref="NumberSequences"/>); <see cref="NextValue"/> ist die Nummer, die als Nächstes vergeben wird.
/// </summary>
public class PickListSequence
{
    public int Id { get; set; } = 1;
    public long NextValue { get; set; } = 1;
}

/// <summary>
/// Feste Zeilen-Ids der Nummernkreise und der atomare Zähler dazu. Vorher standen die Ids als "Magic-Numbers"
/// in den Repositories, und das Hochzählen war ein Lesen-Ändern-Schreiben ohne Sperre: zwei parallele Requests
/// bekamen dieselbe Nummer, der Verlierer scheiterte am Unique-Index mit HTTP 500.
/// </summary>
public static class NumberSequences
{
    public const int PickList = 1;
    public const int PickWave = 2;
    public const int PurchaseOrder = 3;
    public const int ReturnShipment = 4;
    public const int Shipment = 5;

    /// <summary>Versuche, falls das erstmalige Anlegen der Zähler-Zeile mit einem parallelen Aufrufer kollidiert.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// Liefert die nächste Nummer des Kreises (beginnend bei 1) und zählt hoch - atomar in der Datenbank:
    /// <c>UPDATE ... SET NextValue = NextValue + 1</c> und Auslesen laufen in einer Transaktion (bzw. der
    /// bereits offenen), die Datenbank serialisiert parallele Aufrufer. Nur beim allerersten Aufruf muss die
    /// Zeile per INSERT angelegt werden; verliert dort ein Aufrufer gegen einen parallelen (Unique-Verletzung),
    /// wiederholt er den Vorgang (max. 3 Versuche) und zählt dann einfach hoch.
    /// Die Nummer ist sofort und unabhängig vom SaveChanges des Aufrufers vergeben; bricht der ab, bleibt
    /// eine Lücke in der Nummerierung - nie ein Duplikat.
    /// </summary>
    public static async Task<long> NextAsync(LagerDbContext db, int sequenceId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryNextAsync(db, sequenceId, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is DbException or DbUpdateException)
            {
                // Erst-Insert der Zähler-Zeile kollidiert mit einem parallelen Aufrufer: die Zeile gibt es jetzt, noch einmal.
            }
        }
    }

    /// <summary>Setzt den Kreis auf 1 zurück (die nächste Nummer ist wieder 1). Nur bei leerer Tabelle der nummerierten Objekte sinnvoll.</summary>
    public static Task<int> ResetAsync(LagerDbContext db, int sequenceId, CancellationToken ct = default) =>
        db.PickListSequences.Where(s => s.Id == sequenceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextValue, 1L), ct);

    private static async Task<long> TryNextAsync(LagerDbContext db, int sequenceId, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;

        long value;
        var updated = await db.PickListSequences.Where(s => s.Id == sequenceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextValue, x => x.NextValue + 1), ct);
        if (updated == 0)
        {
            // Erste Vergabe dieses Kreises: Zeile anlegen; NextValue zeigt schon auf die übernächste Nummer.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO PickListSequence (Id, NextValue) VALUES ({sequenceId}, 2)", ct);
            value = 1;
        }
        else
        {
            var next = await db.PickListSequences.AsNoTracking()
                .Where(s => s.Id == sequenceId)
                .Select(s => s.NextValue)
                .SingleAsync(ct);
            value = next - 1;
        }

        if (tx is not null) await tx.CommitAsync(ct);
        return value;
    }
}
