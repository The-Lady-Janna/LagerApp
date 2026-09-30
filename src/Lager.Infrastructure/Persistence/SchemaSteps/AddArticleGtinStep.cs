using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// GTIN/EAN am Artikel (WP20). Neue Datenbanken bekommen Spalte und Index aus dem EF-Modell (ArticleConfiguration);
/// dieser Schritt zieht Bestandsdatenbanken nach:
///
///  - <c>Articles.Gtin</c> (SQLite TEXT, MySQL VARCHAR(14), jeweils NULL): nur Ziffern, höchstens 14 Stellen.
///    Bestehende Artikel haben keine GTIN (NULL), es gibt nichts zu übernehmen.
///  - Eindeutiger Index <c>IX_Articles_Gtin</c> nur für nicht-NULL-Werte: bei SQLite als partieller Index
///    (<c>WHERE Gtin IS NOT NULL</c>), bei MySQL als gewöhnlicher Unique-Index (MySQL erlaubt beliebig viele NULLs in
///    einem Unique-Index, SQLite ebenso; der Filter macht bei SQLite die Absicht sichtbar).
///
/// Weil die Spalte neu ist, kann der Unique-Index an keinen Altdaten scheitern - der Schritt ist deshalb kritisch (Standard).
/// Idempotent: läuft er auf einer Datenbank, die Spalte und Index schon hat (frisch aus dem Modell), ändert er nichts.
/// Nur rohes SQL mit den Helfern aus <see cref="SchemaSql"/> (der Schritt läuft auf Datenbanken, denen spätere Spalten
/// noch fehlen).
/// </summary>
public sealed class AddArticleGtinStep : ISchemaUpgradeStep
{
    public const string IndexName = "IX_Articles_Gtin";

    public int Order => 2000;
    public string Name => "2000_AddArticleGtin";

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        // Existiert die Tabelle nicht, legt EnsureCreated sie samt Spalte und Index aus dem Modell an.
        if (!await SchemaSql.TableExistsAsync(db, "Articles")) return;

        var hadColumn = await SchemaSql.ColumnExistsAsync(db, "Articles", "Gtin");
        await SchemaSql.EnsureColumnAsync(db, "Articles", "Gtin", "TEXT NULL", "VARCHAR(14) NULL");
        if (!hadColumn)
            logger.LogInformation("{Step}: Spalte Articles.Gtin angelegt.", Name);

        if (SchemaSql.IsMySql(db))
        {
            await SchemaSql.EnsureIndexAsync(db, "Articles", IndexName, unique: true, "Gtin");
            return;
        }

        var table = SchemaSql.Q(db, "Articles");
        var column = SchemaSql.Q(db, "Gtin");
        await SchemaSql.ExecuteAsync(db,
            $"CREATE UNIQUE INDEX IF NOT EXISTS {SchemaSql.Q(db, IndexName)} ON {table} ({column}) WHERE {column} IS NOT NULL;");
    }
}
