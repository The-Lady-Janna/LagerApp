using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Schema und Altdaten für den vollständigen Bestell-Lebenszyklus (WP13). Neue Datenbanken bekommen alles aus dem
/// EF-Modell (OrderConfiguration); dieser Schritt zieht Bestandsdatenbanken nach:
///
///  - <c>Orders.Priority</c> (int, Standard 0; erlaubt 0..3 prüft die Domain), <c>Orders.DueDate</c> (UTC, optional),
///    <c>Orders.ExternalReference</c> (Kennung im Quellsystem) samt eindeutigem Index <c>IX_Orders_ExternalReference</c>
///    (NULLs kollidieren nicht: viele Bestellungen ohne Referenz sind erlaubt);
///  - Altdaten: Bestellungen, die durch den früher fehlenden Lebenszyklus auf Picking stehen geblieben sind, obwohl
///    ihre Pickliste längst verpackt (Completed) ist, werden auf Packed gesetzt; gepackte Bestellungen, deren Sendungen
///    alle versendet oder zugestellt sind, auf Shipped. Der Bestand ist dort ohnehin schon gebucht, es ändert sich
///    nur der Status - keine Buchung, keine Picklisten.
///
/// Nur rohes SQL mit den Helfern aus <see cref="SchemaSql"/> (der Schritt läuft auf Datenbanken, denen spätere Spalten
/// noch fehlen), idempotent: ein zweiter Lauf findet nichts mehr zu tun.
/// </summary>
public sealed class OrderLifecycleStep : ISchemaUpgradeStep
{
    public const string ExternalReferenceIndex = "IX_Orders_ExternalReference";

    // Status-Ids der Enums (Spalten speichern int): OrderStatus, PickListStatus, ShipmentStatus.
    private const int OrderPicking = 1, OrderPicked = 2, OrderPacked = 3, OrderShipped = 4;
    private const int PickListCompleted = 2;
    private const int ShipmentReady = 0, ShipmentLabeled = 1, ShipmentShipped = 2, ShipmentDelivered = 3;

    public int Order => 1300;
    public string Name => "1300_OrderLifecycle";

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (await SchemaSql.TableExistsAsync(db, "Orders"))
        {
            await SchemaSql.EnsureColumnAsync(db, "Orders", "Priority", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
            await SchemaSql.EnsureColumnAsync(db, "Orders", "DueDate", "TEXT NULL", "DATETIME(6) NULL");
            await SchemaSql.EnsureColumnAsync(db, "Orders", "ExternalReference", "TEXT NULL", "VARCHAR(128) NULL");
            await SchemaSql.EnsureIndexAsync(db, "Orders", ExternalReferenceIndex, unique: true, "ExternalReference");
        }

        await PromoteLegacyOrdersAsync(db, logger);
    }

    private async Task PromoteLegacyOrdersAsync(LagerDbContext db, ILogger logger)
    {
        if (!await SchemaSql.TableExistsAsync(db, "Orders")
            || !await SchemaSql.TableExistsAsync(db, "PickItems")
            || !await SchemaSql.TableExistsAsync(db, "PickLists"))
            return;

        var now = DateTime.UtcNow;
        // Wie der Provider Guids schreibt: SQLite (Microsoft.Data.Sqlite) als GROSS geschriebenen Text.
        var token = SchemaSql.IsMySql(db) ? Guid.NewGuid().ToString() : Guid.NewGuid().ToString().ToUpperInvariant();

        // Bestellung steht auf Picking (oder Picked), aber eine ihrer Picklisten ist verpackt: Bestand ist gebucht -> Packed.
        var packed = await SchemaSql.ExecuteAsync(db,
            "UPDATE Orders SET Status = @packed, UpdatedAt = @now, ConcurrencyToken = @token " +
            "WHERE Status IN (@picking, @picked) AND EXISTS (" +
            "SELECT 1 FROM PickItems i INNER JOIN PickLists p ON p.Id = i.PickListId " +
            "WHERE i.OrderId = Orders.Id AND p.Status = @completed)",
            ("@packed", OrderPacked), ("@now", now), ("@token", token),
            ("@picking", OrderPicking), ("@picked", OrderPicked), ("@completed", PickListCompleted));
        if (packed > 0)
            logger.LogWarning("{Step}: {Count} Bestellung(en) mit verpackter Pickliste von Picking auf Packed gesetzt.", Name, packed);

        if (!await SchemaSql.TableExistsAsync(db, "Shipments")) return;

        // Gepackt, mindestens eine Sendung ist raus und keine mehr offen (Ready/Labeled) -> Shipped.
        var shipped = await SchemaSql.ExecuteAsync(db,
            "UPDATE Orders SET Status = @shipped, UpdatedAt = @now, ConcurrencyToken = @token " +
            "WHERE Status = @packed " +
            "AND EXISTS (SELECT 1 FROM Shipments s WHERE s.OrderId = Orders.Id AND s.Status IN (@sShipped, @sDelivered)) " +
            "AND NOT EXISTS (SELECT 1 FROM Shipments s WHERE s.OrderId = Orders.Id AND s.Status IN (@sReady, @sLabeled))",
            ("@shipped", OrderShipped), ("@packed", OrderPacked), ("@now", now), ("@token", token),
            ("@sShipped", ShipmentShipped), ("@sDelivered", ShipmentDelivered),
            ("@sReady", ShipmentReady), ("@sLabeled", ShipmentLabeled));
        if (shipped > 0)
            logger.LogWarning("{Step}: {Count} Bestellung(en) mit versendeten Sendungen von Packed auf Shipped gesetzt.", Name, shipped);
    }
}
