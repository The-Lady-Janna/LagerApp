using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Ein Schritt der Schema-Evolution. Der <see cref="SchemaUpgrader"/> findet alle Implementierungen in diesem
/// Namespace (<c>Lager.Infrastructure.Persistence.SchemaSteps</c>) per Reflection, sortiert sie nach
/// <see cref="Order"/> und führt jeden Schritt genau einmal pro Datenbank aus. Angewendete Schritte stehen mit
/// Namen in der Tabelle <c>__LagerSchemaVersion</c>.
///
/// Neue Tabellen, Spalten oder Indizes kommen also NUR als neue Datei in diesem Ordner dazu -
/// <c>SchemaUpgrader.cs</c> wird dafür nicht mehr angefasst.
///
/// Regeln für neue Schritte:
///  - Eine öffentliche oder interne, nicht abstrakte Klasse mit parameterlosem Konstruktor.
///  - <see cref="Order"/> ist eindeutig und größer als 0 (0 belegt die Baseline). Vergebene Bereiche: WP09 = 10-99;
///    spätere Pakete nehmen ihre Paketnummer mal 100 als Start (WP13 ab 1300, WP20 ab 2000). Doppelte Orders
///    oder Namen lassen den Start mit einer klaren Meldung scheitern.
///  - <see cref="Name"/> ist eindeutig und ändert sich nie (er ist der Schlüssel in <c>__LagerSchemaVersion</c>),
///    z. B. "1300_AddReservedQuantity".
///  - Idempotent und provider-bewusst (SQLite und MySQL): Auf einer frisch aus dem Modell angelegten Datenbank
///    (EnsureCreated) existiert alles schon, der Schritt darf dort nichts ändern oder kaputtmachen. Nur
///    Existenzprüfungen und die Helfer aus <see cref="SchemaSql"/> verwenden, keine Werte in SQL interpolieren.
///  - Nur rohes SQL, keine Entity-Typen: Ein Schritt läuft auf Datenbanken, denen spätere Spalten noch fehlen.
/// </summary>
public interface ISchemaUpgradeStep
{
    /// <summary>Reihenfolge (aufsteigend); eindeutig, größer als 0.</summary>
    int Order { get; }

    /// <summary>Eindeutiger, unveränderlicher Name (Schlüssel in <c>__LagerSchemaVersion</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Kritische Schritte (Standard) brechen den Start ab, wenn sie scheitern - die App läuft nicht auf einem
    /// halb aktualisierten Schema. Nicht kritische Schritte (Optimierungen wie ein Unique-Index, der an
    /// unerwarteten Legacy-Daten scheitern kann) werden geloggt, nicht als angewendet vermerkt und beim
    /// nächsten Start erneut versucht.
    /// </summary>
    bool IsCritical => true;

    /// <summary>
    /// Führt den Schritt aus. Bei SQLite läuft er zusammen mit dem Versionsvermerk in einer Transaktion
    /// (Fehler = alles zurückgerollt); bei MySQL sind DDL-Anweisungen nicht transaktional, dort muss der Schritt
    /// nach einem Abbruch wiederholbar sein.
    /// </summary>
    Task ApplyAsync(LagerDbContext db, ILogger logger);
}
