using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Infrastructure.Persistence;

/// <summary>
/// Der einzige Pfad der Schema-Evolution (EF-Migrationen gibt es nicht). Neue Datenbanken legt EnsureCreated aus dem
/// Modell an; bestehende bekommen jede Änderung hier, provider-bewusst (SQLite und MySQL) und idempotent.
///
/// Ablauf: Der Upgrader führt zuerst die <see cref="LegacyBaselineStep">Baseline</see> aus (alles, was es vor der
/// Einführung der Schritte gab) und danach alle <see cref="ISchemaUpgradeStep"/>-Implementierungen aus dem Ordner
/// <c>SchemaSteps</c>, sortiert nach <see cref="ISchemaUpgradeStep.Order"/>. Jeder Schritt läuft genau einmal pro
/// Datenbank; angewendete Schritte stehen in der Tabelle <c>__LagerSchemaVersion</c>. Spätere Änderungen legen NUR eine
/// neue Step-Datei an, diese Datei bleibt unverändert.
///
/// Fehler werden nicht verschluckt: Ein kritischer Schritt bricht den Start mit einer klaren Meldung ab, ein nicht
/// kritischer wird geloggt und beim nächsten Start erneut versucht. Bei SQLite laufen Schritt und Versionsvermerk in
/// einer Transaktion.
///
/// Bekannte Einschränkung: Tabellen, die dieser Upgrader (statt EnsureCreated) angelegt hat, haben keine
/// Fremdschlüssel; das EF-Modell deklariert sie nur für neu angelegte Datenbanken. Ein Tabellen-Rebuild für Legacy-
/// Datenbanken findet bewusst nicht statt.
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>Tabelle mit den angewendeten Schritten (Name, Order, Zeitpunkt UTC).</summary>
    public const string VersionTable = "__LagerSchemaVersion";

    /// <summary>Name der Baseline in <see cref="VersionTable"/>.</summary>
    public const string BaselineStepName = "0000_LegacyBaseline";

    private static readonly string[] LegacyWallColumns =
        { "StartXMm", "StartYMm", "StartZMm", "EndXMm", "EndYMm", "EndZMm" };

    /// <summary>Bringt die Datenbank auf den Stand der App (Baseline + alle Schritte), ohne Logger.</summary>
    public static Task UpgradeAsync(LagerDbContext db, CancellationToken ct = default) =>
        UpgradeAsync(db, NullLogger.Instance, additionalSteps: null, ct);

    /// <summary>
    /// Bringt die Datenbank auf den Stand der App. <paramref name="additionalSteps"/> sind zusätzliche Schritte
    /// über die per Reflection gefundenen hinaus (für Tests; im Betrieb null).
    /// </summary>
    public static async Task UpgradeAsync(LagerDbContext db, ILogger logger, IEnumerable<ISchemaUpgradeStep>? additionalSteps, CancellationToken ct = default)
    {
        if (!SchemaSql.IsSqlite(db) && !SchemaSql.IsMySql(db))
        {
            logger.LogWarning("Provider {Provider} wird vom SchemaUpgrader nicht unterstützt, kein Upgrade.", db.Database.ProviderName);
            return;
        }

        var plan = BuildPlan(additionalSteps);
        await EnsureVersionTableAsync(db);
        var applied = new HashSet<string>(
            await SchemaSql.QueryAsync(db, $"SELECT Name FROM {VersionTable}", r => SchemaSql.Str(r, 0)!),
            StringComparer.OrdinalIgnoreCase);

        var pending = plan.Where(s => !applied.Contains(s.Name)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Datenbankschema ist aktuell ({Count} Schritte angewendet).", applied.Count);
            return;
        }

        foreach (var step in pending)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyStepAsync(db, logger, step, ct);
        }
    }

    /// <summary>
    /// Namen aller Schritte, die diese App-Version kennt (Baseline + gefundene Schritte). Ein Backup, dessen
    /// <c>__LagerSchemaVersion</c> weitere Namen enthält, stammt von einer neueren Programmversion.
    /// </summary>
    public static IReadOnlyList<string> GetKnownStepNames() => BuildPlan(null).Select(s => s.Name).ToList();

    private static IReadOnlyList<ISchemaUpgradeStep> BuildPlan(IEnumerable<ISchemaUpgradeStep>? additionalSteps)
    {
        var steps = new List<ISchemaUpgradeStep> { new LegacyBaselineStep() };
        steps.AddRange(SchemaStepCatalog.Discover());
        if (additionalSteps is not null) steps.AddRange(additionalSteps);

        SchemaStepCatalog.Validate(steps);
        var badOrder = steps.FirstOrDefault(s => s is not LegacyBaselineStep && s.Order <= 0);
        if (badOrder is not null)
            throw new InvalidOperationException($"Schema-Step '{badOrder.Name}': Order muss größer als 0 sein (0 gehört der Baseline).");

        return steps.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    private static Task EnsureVersionTableAsync(LagerDbContext db) =>
        SchemaSql.ExecuteAsync(db, SchemaSql.IsMySql(db)
            ? $"CREATE TABLE IF NOT EXISTS {VersionTable} (Name VARCHAR(190) NOT NULL PRIMARY KEY, StepOrder INT NOT NULL, AppliedAt DATETIME(6) NOT NULL) ENGINE=InnoDB;"
            : $"CREATE TABLE IF NOT EXISTS {VersionTable} (Name TEXT NOT NULL PRIMARY KEY, StepOrder INTEGER NOT NULL, AppliedAt TEXT NOT NULL);");

    private static async Task ApplyStepAsync(LagerDbContext db, ILogger logger, ISchemaUpgradeStep step, CancellationToken ct)
    {
        logger.LogInformation("Schema-Step {Step} (Order {Order}) wird angewendet.", step.Name, step.Order);
        try
        {
            if (SchemaSql.IsSqlite(db))
            {
                // Schritt und Versionsvermerk in einer Transaktion: Bei einem Fehler bleibt die Datenbank unverändert.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await step.ApplyAsync(db, logger);
                await RecordAsync(db, step);
                await tx.CommitAsync(ct);
            }
            else
            {
                // MySQL: DDL committet implizit, Transaktionen helfen dort nicht. Schritte müssen wiederholbar sein.
                await step.ApplyAsync(db, logger);
                await RecordAsync(db, step);
            }

            logger.LogInformation("Schema-Step {Step} angewendet.", step.Name);
        }
        catch (Exception ex) when (!step.IsCritical && ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Nicht kritischer Schema-Step {Step} ist fehlgeschlagen und wird beim nächsten Start erneut versucht. Die App startet ohne ihn.",
                step.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Kritischer Schema-Step {Step} ist fehlgeschlagen, der Start wird abgebrochen.", step.Name);
            throw new InvalidOperationException(
                $"Schema-Step '{step.Name}' ist fehlgeschlagen; die Datenbank konnte nicht aktualisiert werden: {ex.Message}", ex);
        }
    }

    private static Task RecordAsync(LagerDbContext db, ISchemaUpgradeStep step)
    {
        // IGNORE: Starten zwei Instanzen gleichzeitig, ist der Vermerk der anderen kein Fehler.
        var insert = SchemaSql.IsMySql(db) ? "INSERT IGNORE" : "INSERT OR IGNORE";
        return SchemaSql.ExecuteAsync(db,
            $"{insert} INTO {VersionTable} (Name, StepOrder, AppliedAt) VALUES (@n, @o, @a)",
            ("@n", step.Name), ("@o", step.Order), ("@a", DateTime.UtcNow));
    }

    // ---- Baseline (alles bis zur Einführung der Schritte) ---------------------------------------------------------

    /// <summary>
    /// Die bisherigen Upgrade-Anweisungen als erster Schritt. Sie sind idempotent (Existenzprüfung vor jeder Änderung),
    /// darum ist es unschädlich, dass sie auf Datenbanken aus der Zeit vor <c>__LagerSchemaVersion</c> noch einmal laufen.
    /// </summary>
    private sealed class LegacyBaselineStep : ISchemaUpgradeStep
    {
        public int Order => 0;
        public string Name => BaselineStepName;
        public Task ApplyAsync(LagerDbContext db, ILogger logger) => ApplyLegacyBaselineAsync(db, logger);
    }

    /// <summary>
    /// Führt nur die Baseline aus (ohne Versionsvermerk, ohne die Schritte aus <c>SchemaSteps</c>). Der normale Weg ist
    /// <see cref="UpgradeAsync(LagerDbContext, ILogger, IEnumerable{ISchemaUpgradeStep}?, CancellationToken)"/>; diese Methode
    /// existiert für Tests und Diagnose (z. B. der Drift-Test zwischen dem DDL hier und dem EF-Modell).
    /// </summary>
    public static async Task ApplyLegacyBaselineAsync(LagerDbContext db, ILogger logger)
    {
        var isMySql = SchemaSql.IsMySql(db);
        string Pick(string sqlite, string mysql) => isMySql ? mysql : sqlite;
        Task Exec(string sql) => SchemaSql.ExecuteAsync(db, sql);
        Task Column(string table, string column, string sqliteType, string mysqlType) =>
            SchemaSql.EnsureColumnAsync(db, table, column, sqliteType, mysqlType);

        await Exec(Pick(SqliteWallsDdl, MySqlWallsDdl));
        await Exec(Pick(SqlitePickPointsDdl, MySqlPickPointsDdl));
        await Exec(Pick(SqlitePickCartConfigsDdl, MySqlPickCartConfigsDdl));
        await Exec(Pick(SqliteAuditEntriesDdl, MySqlAuditEntriesDdl));
        await UpgradeWallsAsync(db);
        await Column("PickLists", "WaypointsJson", "TEXT NULL", "LONGTEXT NULL");
        await Column("PickLists", "PickCartConfigId", "TEXT NULL", "CHAR(36) NULL");
        await Column("PickItems", "ConfirmedQuantity", "INTEGER NULL", "INT NULL");
        await Column("PickItems", "ConfirmedAt", "TEXT NULL", "DATETIME(6) NULL");
        foreach (var table in SchemaSql.EntityTables(db))
            await EnsureConcurrencyTokenAsync(db, logger, table);
        await Column("Articles", "MinStock", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Column("Articles", "ReorderPoint", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Column("Articles", "MaxStock", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Exec(Pick(SqliteInboundShipmentsDdl, MySqlInboundShipmentsDdl));
        await Exec(Pick(SqliteInboundLinesDdl, MySqlInboundLinesDdl));
        await Exec(Pick(SqliteInventoryCountsDdl, MySqlInventoryCountsDdl));
        await Exec(Pick(SqliteInventoryLinesDdl, MySqlInventoryLinesDdl));
        // Welle 6
        await Column("StorageLocations", "BinType", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Column("StorageLocations", "ReplenishmentThreshold", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Exec(Pick(SqlitePickWavesDdl, MySqlPickWavesDdl));
        await Exec(Pick(SqliteReplenishmentTasksDdl, MySqlReplenishmentTasksDdl));
        // Welle 4 - Auth
        await Exec(Pick(SqliteUsersDdl, MySqlUsersDdl));
        // Welle 4 - Procurement (Suppliers, POs, Returns)
        await Column("Articles", "PrimarySupplierId", "TEXT NULL", "CHAR(36) NULL");
        await Column("Articles", "PurchasePriceCents", "INTEGER NOT NULL DEFAULT 0", "INT NOT NULL DEFAULT 0");
        await Exec(Pick(SqliteSuppliersDdl, MySqlSuppliersDdl));
        await Exec(Pick(SqlitePurchaseOrdersDdl, MySqlPurchaseOrdersDdl));
        await Exec(Pick(SqlitePurchaseOrderLinesDdl, MySqlPurchaseOrderLinesDdl));
        await Exec(Pick(SqliteReturnShipmentsDdl, MySqlReturnShipmentsDdl));
        await Exec(Pick(SqliteReturnLinesDdl, MySqlReturnLinesDdl));
        // Welle 5 - Customers, Bundles, Alternativen, Saison
        await Column("Articles", "AlternativeSkusCsv", "TEXT NULL", "VARCHAR(1000) NULL");
        await Column("Articles", "ValidFrom", "TEXT NULL", "DATETIME(6) NULL");
        await Column("Articles", "ValidUntil", "TEXT NULL", "DATETIME(6) NULL");
        await Column("Orders", "CustomerId", "TEXT NULL", "CHAR(36) NULL");
        await Column("Orders", "ShippingAddressId", "TEXT NULL", "CHAR(36) NULL");
        await Exec(Pick(SqliteCustomersDdl, MySqlCustomersDdl));
        await Exec(Pick(SqliteCustomerAddressesDdl, MySqlCustomerAddressesDdl));
        await Exec(Pick(SqliteBundleComponentsDdl, MySqlBundleComponentsDdl));
        // Welle 7
        await Exec(Pick(SqliteShipmentsDdl, MySqlShipmentsDdl));
        // StockMovement-Ledger (post-Welle-10)
        await Exec(Pick(SqliteStockMovementsDdl, MySqlStockMovementsDdl));
        // Nummernkreise
        await Exec(Pick(SqlitePickListSequenceDdl, MySqlPickListSequenceDdl));
        await EnsureIndexesAsync(db);
    }

    /// <summary>
    /// Ergänzt die ConcurrencyToken-Spalte auf bestehenden Tabellen und füllt NULLs mit einer zufälligen Guid pro Zeile
    /// (der erste Concurrency-Check einer Legacy-Zeile findet so einen Wert). Die Tabellenliste stammt aus dem EF-Modell
    /// (alle Entity-Typen), nicht aus einer von Hand gepflegten Liste. Fehler werden nicht verschluckt.
    ///
    /// SQLite: Microsoft.Data.Sqlite schreibt und bindet Guids als GROSS geschriebenen Text, die Prüfung
    /// <c>WHERE ConcurrencyToken = @original</c> ist dort case-sensitiv. Der Backfill erzeugt darum Großbuchstaben
    /// (<c>hex()</c> liefert sie); bereits klein geschriebene Alt-Werte korrigiert der Schritt
    /// <see cref="NormalizeConcurrencyTokenCaseStep"/>. MySQL: <c>UUID()</c>, dort vergleicht CHAR(36) case-insensitiv.
    /// </summary>
    private static async Task EnsureConcurrencyTokenAsync(LagerDbContext db, ILogger logger, string table)
    {
        var columns = await SchemaSql.GetColumnsAsync(db, table);
        if (columns.Count == 0) return; // Tabelle fehlt: EnsureCreated bzw. das DDL weiter unten legt sie samt Spalte an

        var isMySql = SchemaSql.IsMySql(db);
        if (!columns.Contains("ConcurrencyToken"))
            await SchemaSql.EnsureColumnAsync(db, table, "ConcurrencyToken", "TEXT NULL", "CHAR(36) NULL");

        var q = SchemaSql.Q(db, table);
        var fillSql = isMySql
            ? $"UPDATE {q} SET `ConcurrencyToken` = UUID() WHERE `ConcurrencyToken` IS NULL"
            : $"UPDATE {q} SET \"ConcurrencyToken\" = {SqliteRandomGuidExpression} WHERE \"ConcurrencyToken\" IS NULL";
        var filled = await SchemaSql.ExecuteAsync(db, fillSql);
        if (filled > 0)
            logger.LogInformation("ConcurrencyToken in {Table}: {Count} leere Werte aufgefüllt.", table, filled);
    }

    /// <summary>Zufällige Guid (Version 4) als GROSS geschriebener Text, wie ihn Microsoft.Data.Sqlite schreibt.</summary>
    private const string SqliteRandomGuidExpression =
        "hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || " +
        "substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6))";

    /// <summary>
    /// Hot-Path-Indizes, die das EF-Modell deklariert, die bestehende Tabellen aber nicht bekommen. Ob ein Index schon
    /// existiert, prüft <see cref="SchemaSql.EnsureIndexAsync"/> provider-bewusst (MySQL kennt CREATE INDEX IF NOT
    /// EXISTS nicht). Kein try/catch: Ein Fehler ist ein Fehler.
    /// </summary>
    private static async Task EnsureIndexesAsync(LagerDbContext db)
    {
        await SchemaSql.EnsureIndexAsync(db, "StockItems", "IX_StockItems_ArticleId", false, "ArticleId");
        await SchemaSql.EnsureIndexAsync(db, "StockItems", "IX_StockItems_StorageLocationId", false, "StorageLocationId");
        await SchemaSql.EnsureIndexAsync(db, "StockItems", "IX_StockItems_ExpiryDate", false, "ExpiryDate");
        await SchemaSql.EnsureIndexAsync(db, "Orders", "IX_Orders_Status_CreatedAt", false, "Status", "CreatedAt");
        await SchemaSql.EnsureIndexAsync(db, "PickItems", "IX_PickItems_PickListId", false, "PickListId");
        await SchemaSql.EnsureIndexAsync(db, "PickItems", "IX_PickItems_OrderId", false, "OrderId");
    }

    private static async Task UpgradeWallsAsync(LagerDbContext db)
    {
        var isMySql = SchemaSql.IsMySql(db);
        var cols = await SchemaSql.GetColumnsAsync(db, "Walls");
        if (cols.Count == 0) return; // Walls-Tabelle existiert nicht - passiert nach EnsureCreated und dem DDL oben nicht.

        if (!cols.Contains("PointsJson"))
        {
            await SchemaSql.ExecuteAsync(db, $"ALTER TABLE Walls ADD COLUMN PointsJson {(isMySql ? "LONGTEXT" : "TEXT")} NULL;");
            cols.Add("PointsJson");
        }

        // Sind die alten Start/End-Spalten noch da, wird PointsJson daraus befüllt.
        if (cols.Contains("StartXMm") && cols.Contains("EndXMm"))
            await SchemaSql.ExecuteAsync(db, isMySql ? MySqlWallsPointsBackfill : SqliteWallsPointsBackfill);

        // Jede Zeile ohne Wert bekommt eine leere Polylinie, damit EF beim Lesen nicht an NULL scheitert.
        await SchemaSql.ExecuteAsync(db, "UPDATE Walls SET PointsJson = '[]' WHERE PointsJson IS NULL;");

        // Alte NOT-NULL-Spalten entfernen, damit neue INSERTs sie nicht verletzen.
        foreach (var col in LegacyWallColumns)
        {
            if (cols.Contains(col))
                await SchemaSql.ExecuteAsync(db, $"ALTER TABLE Walls DROP COLUMN {SchemaSql.Q(db, col)};");
        }
    }

    private const string SqlitePickListSequenceDdl = @"
CREATE TABLE IF NOT EXISTS PickListSequence (
    Id INTEGER NOT NULL PRIMARY KEY,
    NextValue INTEGER NOT NULL
);
";

    private const string MySqlPickListSequenceDdl = @"
CREATE TABLE IF NOT EXISTS PickListSequence (
    Id INT NOT NULL PRIMARY KEY,
    NextValue BIGINT NOT NULL
) ENGINE=InnoDB;
";

    private const string SqliteWallsDdl = @"
CREATE TABLE IF NOT EXISTS Walls (
    Id TEXT NOT NULL PRIMARY KEY,
    WarehouseId TEXT NOT NULL,
    Label TEXT NULL,
    PointsJson TEXT NULL,
    ThicknessMm INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Walls_WarehouseId ON Walls(WarehouseId);
";

    private const string SqliteWallsPointsBackfill = @"
UPDATE Walls
SET PointsJson = '[{""XMm"":' || StartXMm || ',""YMm"":' || StartYMm || ',""ZMm"":' || StartZMm ||
                 '},{""XMm"":' || EndXMm   || ',""YMm"":' || EndYMm   || ',""ZMm"":' || EndZMm   || '}]'
WHERE (PointsJson IS NULL OR PointsJson = '');
";

    private const string SqlitePickPointsDdl = @"
CREATE TABLE IF NOT EXISTS PickPoints (
    Id TEXT NOT NULL PRIMARY KEY,
    WarehouseId TEXT NOT NULL,
    Label TEXT NOT NULL,
    Type INTEGER NOT NULL,
    PosXMm INTEGER NOT NULL,
    PosYMm INTEGER NOT NULL,
    PosZMm INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_PickPoints_WarehouseId ON PickPoints(WarehouseId);
";

    private const string MySqlPickPointsDdl = @"
CREATE TABLE IF NOT EXISTS PickPoints (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    WarehouseId CHAR(36) NOT NULL,
    Label VARCHAR(128) NOT NULL,
    Type INT NOT NULL,
    PosXMm INT NOT NULL,
    PosYMm INT NOT NULL,
    PosZMm INT NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    INDEX IX_PickPoints_WarehouseId (WarehouseId)
) ENGINE=InnoDB;
";

    private const string MySqlWallsDdl = @"
CREATE TABLE IF NOT EXISTS Walls (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    WarehouseId CHAR(36) NOT NULL,
    Label VARCHAR(128) NULL,
    PointsJson LONGTEXT NULL,
    ThicknessMm INT NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    INDEX IX_Walls_WarehouseId (WarehouseId)
) ENGINE=InnoDB;
";

    private const string SqlitePickCartConfigsDdl = @"
CREATE TABLE IF NOT EXISTS PickCartConfigs (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    LevelCount INTEGER NOT NULL,
    LevelHeightMm INTEGER NOT NULL,
    LevelWidthMm INTEGER NOT NULL,
    LevelDepthMm INTEGER NOT NULL,
    MaxWeightGrams INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_PickCartConfigs_Name ON PickCartConfigs(Name);
";

    private const string MySqlPickCartConfigsDdl = @"
CREATE TABLE IF NOT EXISTS PickCartConfigs (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    Name VARCHAR(128) NOT NULL,
    LevelCount INT NOT NULL,
    LevelHeightMm INT NOT NULL,
    LevelWidthMm INT NOT NULL,
    LevelDepthMm INT NOT NULL,
    MaxWeightGrams INT NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    UNIQUE INDEX IX_PickCartConfigs_Name (Name)
) ENGINE=InnoDB;
";

    private const string SqliteInboundShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS InboundShipments (
    Id TEXT NOT NULL PRIMARY KEY,
    ShipmentNumber TEXT NOT NULL,
    SupplierReference TEXT NULL,
    Notes TEXT NULL,
    Status INTEGER NOT NULL,
    ReceivedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_InboundShipments_ShipmentNumber ON InboundShipments(ShipmentNumber);
CREATE INDEX IF NOT EXISTS IX_InboundShipments_Status_CreatedAt ON InboundShipments(Status, CreatedAt);
";

    private const string SqliteInboundLinesDdl = @"
CREATE TABLE IF NOT EXISTS InboundLines (
    Id TEXT NOT NULL PRIMARY KEY,
    InboundShipmentId TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    TargetBinId TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    LotNumber TEXT NULL,
    ExpiryDate TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_InboundLines_InboundShipmentId ON InboundLines(InboundShipmentId);
";

    private const string MySqlInboundShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS InboundShipments (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    ShipmentNumber VARCHAR(64) NOT NULL,
    SupplierReference VARCHAR(128) NULL,
    Notes VARCHAR(1000) NULL,
    Status INT NOT NULL,
    ReceivedAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_InboundShipments_ShipmentNumber (ShipmentNumber),
    INDEX IX_InboundShipments_Status_CreatedAt (Status, CreatedAt)
) ENGINE=InnoDB;
";

    private const string MySqlInboundLinesDdl = @"
CREATE TABLE IF NOT EXISTS InboundLines (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    InboundShipmentId CHAR(36) NOT NULL,
    ArticleId CHAR(36) NOT NULL,
    TargetBinId CHAR(36) NOT NULL,
    Quantity INT NOT NULL,
    LotNumber VARCHAR(64) NULL,
    ExpiryDate DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_InboundLines_InboundShipmentId (InboundShipmentId)
) ENGINE=InnoDB;
";

    private const string SqliteSuppliersDdl = @"
CREATE TABLE IF NOT EXISTS Suppliers (
    Id TEXT NOT NULL PRIMARY KEY,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    ContactEmail TEXT NULL,
    ContactPhone TEXT NULL,
    Notes TEXT NULL,
    LeadTimeDays INTEGER NOT NULL DEFAULT 7,
    MinOrderValueCents INTEGER NOT NULL DEFAULT 0,
    Currency TEXT NOT NULL DEFAULT 'EUR',
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Suppliers_Code ON Suppliers(Code);
";

    private const string MySqlSuppliersDdl = @"
CREATE TABLE IF NOT EXISTS Suppliers (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    Code VARCHAR(32) NOT NULL,
    Name VARCHAR(256) NOT NULL,
    ContactEmail VARCHAR(256) NULL,
    ContactPhone VARCHAR(64) NULL,
    Notes VARCHAR(1000) NULL,
    LeadTimeDays INT NOT NULL DEFAULT 7,
    MinOrderValueCents INT NOT NULL DEFAULT 0,
    Currency CHAR(3) NOT NULL DEFAULT 'EUR',
    IsActive TINYINT(1) NOT NULL DEFAULT 1,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_Suppliers_Code (Code)
) ENGINE=InnoDB;
";

    private const string SqlitePurchaseOrdersDdl = @"
CREATE TABLE IF NOT EXISTS PurchaseOrders (
    Id TEXT NOT NULL PRIMARY KEY,
    PoNumber TEXT NOT NULL,
    SupplierId TEXT NOT NULL,
    Currency TEXT NOT NULL DEFAULT 'EUR',
    Notes TEXT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    SentAt TEXT NULL,
    ExpectedDate TEXT NULL,
    ReceivedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_PurchaseOrders_PoNumber ON PurchaseOrders(PoNumber);
CREATE INDEX IF NOT EXISTS IX_PurchaseOrders_Status_CreatedAt ON PurchaseOrders(Status, CreatedAt);
CREATE INDEX IF NOT EXISTS IX_PurchaseOrders_SupplierId ON PurchaseOrders(SupplierId);
";

    private const string MySqlPurchaseOrdersDdl = @"
CREATE TABLE IF NOT EXISTS PurchaseOrders (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    PoNumber VARCHAR(64) NOT NULL,
    SupplierId CHAR(36) NOT NULL,
    Currency CHAR(3) NOT NULL DEFAULT 'EUR',
    Notes VARCHAR(1000) NULL,
    Status INT NOT NULL DEFAULT 0,
    SentAt DATETIME(6) NULL,
    ExpectedDate DATETIME(6) NULL,
    ReceivedAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_PurchaseOrders_PoNumber (PoNumber),
    INDEX IX_PurchaseOrders_Status_CreatedAt (Status, CreatedAt),
    INDEX IX_PurchaseOrders_SupplierId (SupplierId)
) ENGINE=InnoDB;
";

    private const string SqlitePurchaseOrderLinesDdl = @"
CREATE TABLE IF NOT EXISTS PurchaseOrderLines (
    Id TEXT NOT NULL PRIMARY KEY,
    PurchaseOrderId TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    ArticleSku TEXT NOT NULL,
    OrderedQty INTEGER NOT NULL,
    ReceivedQty INTEGER NOT NULL DEFAULT 0,
    UnitPriceCents INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_PurchaseOrderLines_PurchaseOrderId ON PurchaseOrderLines(PurchaseOrderId);
";

    private const string MySqlPurchaseOrderLinesDdl = @"
CREATE TABLE IF NOT EXISTS PurchaseOrderLines (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    PurchaseOrderId CHAR(36) NOT NULL,
    ArticleId CHAR(36) NOT NULL,
    ArticleSku VARCHAR(64) NOT NULL,
    OrderedQty INT NOT NULL,
    ReceivedQty INT NOT NULL DEFAULT 0,
    UnitPriceCents INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_PurchaseOrderLines_PurchaseOrderId (PurchaseOrderId)
) ENGINE=InnoDB;
";

    private const string SqliteReturnShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS ReturnShipments (
    Id TEXT NOT NULL PRIMARY KEY,
    RmaNumber TEXT NOT NULL,
    OrderId TEXT NULL,
    CustomerReference TEXT NULL,
    Notes TEXT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    ProcessedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_ReturnShipments_RmaNumber ON ReturnShipments(RmaNumber);
CREATE INDEX IF NOT EXISTS IX_ReturnShipments_Status_CreatedAt ON ReturnShipments(Status, CreatedAt);
";

    private const string MySqlReturnShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS ReturnShipments (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    RmaNumber VARCHAR(64) NOT NULL,
    OrderId CHAR(36) NULL,
    CustomerReference VARCHAR(128) NULL,
    Notes VARCHAR(1000) NULL,
    Status INT NOT NULL DEFAULT 0,
    ProcessedAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_ReturnShipments_RmaNumber (RmaNumber),
    INDEX IX_ReturnShipments_Status_CreatedAt (Status, CreatedAt)
) ENGINE=InnoDB;
";

    private const string SqliteReturnLinesDdl = @"
CREATE TABLE IF NOT EXISTS ReturnLines (
    Id TEXT NOT NULL PRIMARY KEY,
    ReturnShipmentId TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    ArticleSku TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    LotNumber TEXT NULL,
    QcResult INTEGER NOT NULL DEFAULT 0,
    TargetBinId TEXT NULL,
    QcNotes TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_ReturnLines_ReturnShipmentId ON ReturnLines(ReturnShipmentId);
";

    private const string MySqlReturnLinesDdl = @"
CREATE TABLE IF NOT EXISTS ReturnLines (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    ReturnShipmentId CHAR(36) NOT NULL,
    ArticleId CHAR(36) NOT NULL,
    ArticleSku VARCHAR(64) NOT NULL,
    Quantity INT NOT NULL,
    LotNumber VARCHAR(64) NULL,
    QcResult INT NOT NULL DEFAULT 0,
    TargetBinId CHAR(36) NULL,
    QcNotes VARCHAR(500) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_ReturnLines_ReturnShipmentId (ReturnShipmentId)
) ENGINE=InnoDB;
";

    private const string SqliteCustomersDdl = @"
CREATE TABLE IF NOT EXISTS Customers (
    Id TEXT NOT NULL PRIMARY KEY,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    Email TEXT NULL,
    Phone TEXT NULL,
    Notes TEXT NULL,
    Currency TEXT NOT NULL DEFAULT 'EUR',
    DefaultDiscountPercent INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Customers_Code ON Customers(Code);
";

    private const string MySqlCustomersDdl = @"
CREATE TABLE IF NOT EXISTS Customers (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    Code VARCHAR(32) NOT NULL,
    Name VARCHAR(256) NOT NULL,
    Email VARCHAR(256) NULL,
    Phone VARCHAR(64) NULL,
    Notes VARCHAR(1000) NULL,
    Currency CHAR(3) NOT NULL DEFAULT 'EUR',
    DefaultDiscountPercent INT NOT NULL DEFAULT 0,
    IsActive TINYINT(1) NOT NULL DEFAULT 1,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_Customers_Code (Code)
) ENGINE=InnoDB;
";

    private const string SqliteCustomerAddressesDdl = @"
CREATE TABLE IF NOT EXISTS CustomerAddresses (
    Id TEXT NOT NULL PRIMARY KEY,
    CustomerId TEXT NOT NULL,
    Kind INTEGER NOT NULL DEFAULT 0,
    Label TEXT NOT NULL,
    Street TEXT NOT NULL,
    Street2 TEXT NULL,
    Zip TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL DEFAULT 'DE',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_CustomerAddresses_CustomerId ON CustomerAddresses(CustomerId);
";

    private const string MySqlCustomerAddressesDdl = @"
CREATE TABLE IF NOT EXISTS CustomerAddresses (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    CustomerId CHAR(36) NOT NULL,
    Kind INT NOT NULL DEFAULT 0,
    Label VARCHAR(64) NOT NULL,
    Street VARCHAR(256) NOT NULL,
    Street2 VARCHAR(256) NULL,
    Zip VARCHAR(16) NOT NULL,
    City VARCHAR(128) NOT NULL,
    Country CHAR(3) NOT NULL DEFAULT 'DE',
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_CustomerAddresses_CustomerId (CustomerId)
) ENGINE=InnoDB;
";

    private const string SqliteBundleComponentsDdl = @"
CREATE TABLE IF NOT EXISTS BundleComponents (
    Id TEXT NOT NULL PRIMARY KEY,
    BundleArticleId TEXT NOT NULL,
    ComponentArticleId TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_BundleComponents_BundleArticleId ON BundleComponents(BundleArticleId);
";

    private const string MySqlBundleComponentsDdl = @"
CREATE TABLE IF NOT EXISTS BundleComponents (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    BundleArticleId CHAR(36) NOT NULL,
    ComponentArticleId CHAR(36) NOT NULL,
    Quantity INT NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_BundleComponents_BundleArticleId (BundleArticleId)
) ENGINE=InnoDB;
";

    private const string SqliteShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS Shipments (
    Id TEXT NOT NULL PRIMARY KEY,
    ShipmentNumber TEXT NOT NULL,
    OrderId TEXT NOT NULL,
    PickListId TEXT NULL,
    CarrierCode TEXT NOT NULL,
    TrackingNumber TEXT NULL,
    TrackingUrl TEXT NULL,
    WeightGrams INTEGER NOT NULL DEFAULT 0,
    LengthMm INTEGER NOT NULL DEFAULT 0,
    WidthMm INTEGER NOT NULL DEFAULT 0,
    HeightMm INTEGER NOT NULL DEFAULT 0,
    CostCents INTEGER NOT NULL DEFAULT 0,
    Notes TEXT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    LabeledAt TEXT NULL,
    ShippedAt TEXT NULL,
    DeliveredAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Shipments_ShipmentNumber ON Shipments(ShipmentNumber);
CREATE INDEX IF NOT EXISTS IX_Shipments_OrderId ON Shipments(OrderId);
CREATE INDEX IF NOT EXISTS IX_Shipments_Status_CreatedAt ON Shipments(Status, CreatedAt);
";

    private const string MySqlShipmentsDdl = @"
CREATE TABLE IF NOT EXISTS Shipments (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    ShipmentNumber VARCHAR(64) NOT NULL,
    OrderId CHAR(36) NOT NULL,
    PickListId CHAR(36) NULL,
    CarrierCode VARCHAR(16) NOT NULL,
    TrackingNumber VARCHAR(128) NULL,
    TrackingUrl VARCHAR(500) NULL,
    WeightGrams INT NOT NULL DEFAULT 0,
    LengthMm INT NOT NULL DEFAULT 0,
    WidthMm INT NOT NULL DEFAULT 0,
    HeightMm INT NOT NULL DEFAULT 0,
    CostCents INT NOT NULL DEFAULT 0,
    Notes VARCHAR(1000) NULL,
    Status INT NOT NULL DEFAULT 0,
    LabeledAt DATETIME(6) NULL,
    ShippedAt DATETIME(6) NULL,
    DeliveredAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_Shipments_ShipmentNumber (ShipmentNumber),
    INDEX IX_Shipments_OrderId (OrderId),
    INDEX IX_Shipments_Status_CreatedAt (Status, CreatedAt)
) ENGINE=InnoDB;
";

    private const string SqliteStockMovementsDdl = @"
CREATE TABLE IF NOT EXISTS StockMovements (
    Id TEXT NOT NULL PRIMARY KEY,
    At TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    BinId TEXT NOT NULL,
    QuantityDelta INTEGER NOT NULL,
    UnitCostCents INTEGER NOT NULL DEFAULT 0,
    Reason INTEGER NOT NULL,
    ReferenceType TEXT NULL,
    ReferenceId TEXT NULL,
    LotNumber TEXT NULL,
    ExpiryDate TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_StockMovements_Article_At ON StockMovements(ArticleId, At);
CREATE INDEX IF NOT EXISTS IX_StockMovements_LotNumber ON StockMovements(LotNumber);
CREATE INDEX IF NOT EXISTS IX_StockMovements_Reference ON StockMovements(ReferenceType, ReferenceId);
";

    private const string MySqlStockMovementsDdl = @"
CREATE TABLE IF NOT EXISTS StockMovements (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    At DATETIME(6) NOT NULL,
    ArticleId CHAR(36) NOT NULL,
    BinId CHAR(36) NOT NULL,
    QuantityDelta INT NOT NULL,
    UnitCostCents INT NOT NULL DEFAULT 0,
    Reason INT NOT NULL,
    ReferenceType VARCHAR(64) NULL,
    ReferenceId CHAR(36) NULL,
    LotNumber VARCHAR(64) NULL,
    ExpiryDate DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_StockMovements_Article_At (ArticleId, At),
    INDEX IX_StockMovements_LotNumber (LotNumber),
    INDEX IX_StockMovements_Reference (ReferenceType, ReferenceId)
) ENGINE=InnoDB;
";

    private const string SqliteUsersDdl = @"
CREATE TABLE IF NOT EXISTS Users (
    Id TEXT NOT NULL PRIMARY KEY,
    Username TEXT NOT NULL,
    Email TEXT NULL,
    DisplayName TEXT NULL,
    PasswordHash TEXT NOT NULL,
    Roles INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    LastLoginAt TEXT NULL,
    FailedLoginAttempts INTEGER NOT NULL DEFAULT 0,
    LockedUntil TEXT NULL,
    MustChangePassword INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users(Username);
";

    private const string MySqlUsersDdl = @"
CREATE TABLE IF NOT EXISTS Users (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    Username VARCHAR(64) NOT NULL,
    Email VARCHAR(256) NULL,
    DisplayName VARCHAR(128) NULL,
    PasswordHash VARCHAR(256) NOT NULL,
    Roles INT NOT NULL DEFAULT 0,
    IsActive TINYINT(1) NOT NULL DEFAULT 1,
    LastLoginAt DATETIME(6) NULL,
    FailedLoginAttempts INT NOT NULL DEFAULT 0,
    LockedUntil DATETIME(6) NULL,
    MustChangePassword TINYINT(1) NOT NULL DEFAULT 0,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_Users_Username (Username)
) ENGINE=InnoDB;
";

    private const string SqlitePickWavesDdl = @"
CREATE TABLE IF NOT EXISTS PickWaves (
    Id TEXT NOT NULL PRIMARY KEY,
    WaveNumber TEXT NOT NULL,
    Description TEXT NULL,
    CutoffAt TEXT NULL,
    Status INTEGER NOT NULL,
    ReleasedAt TEXT NULL,
    CompletedAt TEXT NULL,
    OrderIdsCsv TEXT NULL,
    PickListIdsCsv TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_PickWaves_WaveNumber ON PickWaves(WaveNumber);
CREATE INDEX IF NOT EXISTS IX_PickWaves_Status_CreatedAt ON PickWaves(Status, CreatedAt);
";

    private const string MySqlPickWavesDdl = @"
CREATE TABLE IF NOT EXISTS PickWaves (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    WaveNumber VARCHAR(64) NOT NULL,
    Description VARCHAR(500) NULL,
    CutoffAt DATETIME(6) NULL,
    Status INT NOT NULL,
    ReleasedAt DATETIME(6) NULL,
    CompletedAt DATETIME(6) NULL,
    OrderIdsCsv LONGTEXT NULL,
    PickListIdsCsv LONGTEXT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    UNIQUE INDEX IX_PickWaves_WaveNumber (WaveNumber),
    INDEX IX_PickWaves_Status_CreatedAt (Status, CreatedAt)
) ENGINE=InnoDB;
";

    private const string SqliteReplenishmentTasksDdl = @"
CREATE TABLE IF NOT EXISTS ReplenishmentTasks (
    Id TEXT NOT NULL PRIMARY KEY,
    ArticleId TEXT NOT NULL,
    ArticleSku TEXT NOT NULL,
    SourceBinId TEXT NOT NULL,
    SourceBinCode TEXT NOT NULL,
    TargetBinId TEXT NOT NULL,
    TargetBinCode TEXT NOT NULL,
    SuggestedQty INTEGER NOT NULL,
    CompletedQty INTEGER NULL,
    Status INTEGER NOT NULL,
    CompletedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_ReplenishmentTasks_Status_CreatedAt ON ReplenishmentTasks(Status, CreatedAt);
CREATE INDEX IF NOT EXISTS IX_ReplenishmentTasks_Article_Target_Status ON ReplenishmentTasks(ArticleId, TargetBinId, Status);
";

    private const string MySqlReplenishmentTasksDdl = @"
CREATE TABLE IF NOT EXISTS ReplenishmentTasks (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    ArticleId CHAR(36) NOT NULL,
    ArticleSku VARCHAR(64) NOT NULL,
    SourceBinId CHAR(36) NOT NULL,
    SourceBinCode VARCHAR(64) NOT NULL,
    TargetBinId CHAR(36) NOT NULL,
    TargetBinCode VARCHAR(64) NOT NULL,
    SuggestedQty INT NOT NULL,
    CompletedQty INT NULL,
    Status INT NOT NULL,
    CompletedAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_ReplenishmentTasks_Status_CreatedAt (Status, CreatedAt),
    INDEX IX_ReplenishmentTasks_Article_Target_Status (ArticleId, TargetBinId, Status)
) ENGINE=InnoDB;
";

    private const string SqliteInventoryCountsDdl = @"
CREATE TABLE IF NOT EXISTS InventoryCounts (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Status INTEGER NOT NULL,
    ReconciledAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_InventoryCounts_Status_CreatedAt ON InventoryCounts(Status, CreatedAt);
";

    private const string SqliteInventoryLinesDdl = @"
CREATE TABLE IF NOT EXISTS InventoryLines (
    Id TEXT NOT NULL PRIMARY KEY,
    InventoryCountId TEXT NOT NULL,
    BinId TEXT NOT NULL,
    BinCode TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    ArticleSku TEXT NOT NULL,
    ExpectedQty INTEGER NOT NULL,
    CountedQty INTEGER NULL,
    Reason TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ConcurrencyToken TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_InventoryLines_InventoryCountId ON InventoryLines(InventoryCountId);
";

    private const string MySqlInventoryCountsDdl = @"
CREATE TABLE IF NOT EXISTS InventoryCounts (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    Name VARCHAR(128) NOT NULL,
    Status INT NOT NULL,
    ReconciledAt DATETIME(6) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_InventoryCounts_Status_CreatedAt (Status, CreatedAt)
) ENGINE=InnoDB;
";

    private const string MySqlInventoryLinesDdl = @"
CREATE TABLE IF NOT EXISTS InventoryLines (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    InventoryCountId CHAR(36) NOT NULL,
    BinId CHAR(36) NOT NULL,
    BinCode VARCHAR(64) NOT NULL,
    ArticleId CHAR(36) NOT NULL,
    ArticleSku VARCHAR(64) NOT NULL,
    ExpectedQty INT NOT NULL,
    CountedQty INT NULL,
    Reason VARCHAR(500) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    ConcurrencyToken CHAR(36) NULL,
    INDEX IX_InventoryLines_InventoryCountId (InventoryCountId)
) ENGINE=InnoDB;
";

    private const string SqliteAuditEntriesDdl = @"
CREATE TABLE IF NOT EXISTS AuditEntries (
    Id TEXT NOT NULL PRIMARY KEY,
    At TEXT NOT NULL,
    User TEXT NULL,
    EntityType TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    Operation TEXT NOT NULL,
    ChangesJson TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_AuditEntries_EntityType_EntityId_At ON AuditEntries(EntityType, EntityId, At);
CREATE INDEX IF NOT EXISTS IX_AuditEntries_At ON AuditEntries(At);
";

    private const string MySqlAuditEntriesDdl = @"
CREATE TABLE IF NOT EXISTS AuditEntries (
    Id CHAR(36) NOT NULL PRIMARY KEY,
    At DATETIME(6) NOT NULL,
    User VARCHAR(128) NULL,
    EntityType VARCHAR(64) NOT NULL,
    EntityId VARCHAR(64) NOT NULL,
    Operation VARCHAR(16) NOT NULL,
    ChangesJson LONGTEXT NULL,
    INDEX IX_AuditEntries_EntityType_EntityId_At (EntityType, EntityId, At),
    INDEX IX_AuditEntries_At (At)
) ENGINE=InnoDB;
";

    private const string MySqlWallsPointsBackfill = @"
UPDATE Walls
SET PointsJson = CONCAT('[{""XMm"":', StartXMm, ',""YMm"":', StartYMm, ',""ZMm"":', StartZMm,
                       '},{""XMm"":', EndXMm,   ',""YMm"":', EndYMm,   ',""ZMm"":', EndZMm,   '}]')
WHERE (PointsJson IS NULL OR PointsJson = '');
";
}
