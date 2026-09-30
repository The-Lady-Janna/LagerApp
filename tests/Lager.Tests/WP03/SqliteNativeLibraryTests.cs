using Microsoft.Data.Sqlite;

namespace Lager.Tests.WP03;

/// <summary>
/// Schutz gegen CVE-2025-6965 (GHSA-2m69-gcr7-jv3q): SQLitePCLRaw.lib.e_sqlite3 bis 2.1.11 bündelt eine
/// SQLite-Version &lt; 3.50.2 (Speicherkorruption). Das Advisory nennt keinen Patch-Stand, deshalb prüft
/// dieser Test nicht die Paketversion, sondern die native Bibliothek, die tatsächlich geladen wird.
/// </summary>
public class SqliteNativeLibraryTests
{
    private static readonly Version MinimumSafeVersion = new(3, 50, 2);

    [Fact]
    public void Bundled_sqlite_is_not_affected_by_CVE_2025_6965()
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";

        var raw = (string)command.ExecuteScalar()!;
        var version = Version.Parse(raw);

        Assert.True(version >= MinimumSafeVersion,
            $"Die geladene SQLite-Version {version} ist älter als {MinimumSafeVersion} (CVE-2025-6965). " +
            "SQLitePCLRaw.bundle_e_sqlite3 in Lager.Infrastructure.csproj auf eine gepatchte Version anheben.");
    }
}
