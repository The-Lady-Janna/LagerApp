using System.Data.Common;
using Lager.Application.Abstractions;
using Lager.Infrastructure.Auth;
using Lager.Infrastructure.Backup;
using Lager.Infrastructure.Integrations;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.Repositories;
using Lager.Infrastructure.Shipping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Provider, Verbindungsstring und (SQLite) der aufgelöste Dateipfad einmal als Singleton: DbContext, Backup und
        // Restore sehen dieselbe Datei. Ein relativer SQLite-Pfad gilt einheitlich relativ zum ContentRoot.
        services.AddSingleton(sp => DatabaseSettings.Create(config, sp.GetRequiredService<IHostEnvironment>().ContentRootPath));

        // AuditingInterceptor must be scoped now — it depends on ICurrentUser which
        // is per-request. The DbContext factory resolves it from the scope.
        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<LagerDbContext>((sp, options) =>
        {
            var settings = sp.GetRequiredService<DatabaseSettings>();
            if (settings.IsMySql)
            {
                // Die Serverversion steht einmal fest (Database:MySqlServerVersion oder einmalige Erkennung),
                // nicht pro Scope: sonst baute jeder Request eine Extra-Verbindung nur dafür auf.
                options.UseMySql(settings.ConnectionString, settings.GetMySqlServerVersion());
            }
            else
            {
                options.UseSqlite(settings.ConnectionString);
                options.AddInterceptors(SqlitePragmaInterceptor.Instance);
            }
            options.AddInterceptors(sp.GetRequiredService<AuditingInterceptor>());
        });

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IWarehouseRepository, WarehouseRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IPickListRepository, PickListRepository>();
        services.AddScoped<IPickCartConfigRepository, PickCartConfigRepository>();
        services.AddScoped<IInboundRepository, InboundRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IPickWaveRepository, PickWaveRepository>();
        services.AddScoped<IReplenishmentRepository, ReplenishmentRepository>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IReturnRepository, ReturnRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IStockMovementRepository, StockMovementRepository>();

        // Backup/Restore: Einstellungen (Abschnitt Backup, alle mit Standard im Code), der Dienst für Liste/Erstellen/Aufbewahrung/Restore
        // und der zeitgesteuerte Hintergrundjob (ohne Backup:Schedule tut er nichts; bei MySQL ruht er).
        services.Configure<BackupOptions>(config.GetSection(BackupOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BackupService>();
        services.AddHostedService<BackupHostedService>();

        // Carrier registry + stub-adapters
        services.AddSingleton<ICarrierAdapter, ManualCarrierAdapter>();
        services.AddSingleton<ICarrierAdapter, DhlStubAdapter>();
        services.AddSingleton<ICarrierAdapter, UpsStubAdapter>();
        services.AddSingleton<ICarrierRegistry, CarrierRegistry>();

        // External order sources (heute keine konfiguriert)
        services.AddSingleton<IExternalOrderSourceRegistry, ExternalOrderSourceRegistry>();

        // Waage: NullScaleReader bis echte Hardware konfiguriert ist
        services.AddSingleton<IScaleReader, NullScaleReader>();
        services.AddScoped<Lager.Application.Reports.IReportQueryGateway, ReportQueryGateway>();

        // Auth services
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        // JwtSettings binding happens in the host (Program.cs) where the
        // Microsoft.Extensions.Configuration.Binder package is implicitly
        // pulled in by the AspNetCore meta-package.

        return services;
    }
}

/// <summary>
/// Datenbank-Einstellungen aus der Konfiguration (<c>Database:Provider</c>, <c>Database:ConnectionString</c>,
/// optional <c>Database:MySqlServerVersion</c>), einmal aufgelöst.
/// </summary>
public sealed class DatabaseSettings
{
    private readonly Lazy<ServerVersion>? _mySqlVersion;

    private DatabaseSettings(bool isMySql, string connectionString, string? sqliteFilePath, Lazy<ServerVersion>? mySqlVersion)
    {
        IsMySql = isMySql;
        ConnectionString = connectionString;
        SqliteFilePath = sqliteFilePath;
        _mySqlVersion = mySqlVersion;
    }

    public bool IsMySql { get; }
    public bool IsSqlite => !IsMySql;

    /// <summary>Verbindungsstring, bei SQLite mit aufgelöstem (absolutem) Dateipfad.</summary>
    public string ConnectionString { get; }

    /// <summary>Absoluter Pfad der SQLite-Datei; null bei MySQL und bei reinen In-Memory-Datenbanken.</summary>
    public string? SqliteFilePath { get; }

    /// <summary>
    /// Die MySQL-Serverversion: aus <c>Database:MySqlServerVersion</c> (z. B. "8.0.36" oder "10.11.0-mariadb"), sonst
    /// beim ersten Zugriff einmal vom Server erfragt. Nur bei MySQL aufrufen.
    /// </summary>
    public ServerVersion GetMySqlServerVersion() =>
        _mySqlVersion?.Value ?? throw new InvalidOperationException("Die Datenbank ist keine MySQL-Datenbank.");

    public static DatabaseSettings Create(IConfiguration config, string contentRootPath)
    {
        var provider = config["Database:Provider"] ?? "Sqlite";
        var connectionString = config["Database:ConnectionString"]
            ?? throw new InvalidOperationException("Missing Database:ConnectionString in configuration");

        switch (provider.ToLowerInvariant())
        {
            case "mysql":
                var configured = config["Database:MySqlServerVersion"];
                var version = string.IsNullOrWhiteSpace(configured)
                    ? new Lazy<ServerVersion>(() => ServerVersion.AutoDetect(connectionString), LazyThreadSafetyMode.PublicationOnly)
                    : new Lazy<ServerVersion>(() => ServerVersion.Parse(configured.Trim()), LazyThreadSafetyMode.PublicationOnly);
                return new DatabaseSettings(true, connectionString, null, version);

            case "sqlite":
                var builder = new SqliteConnectionStringBuilder(connectionString);
                var dataSource = builder.DataSource;
                var isFile = !string.IsNullOrWhiteSpace(dataSource)
                             && builder.Mode != SqliteOpenMode.Memory
                             && !string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                             && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
                if (!isFile)
                    return new DatabaseSettings(false, connectionString, null, null);

                // Absolute Pfade bleiben unverändert; relative werden gegen den ContentRoot aufgelöst (nicht gegen das
                // Arbeitsverzeichnis des Prozesses, das bei Dienst/Docker abweichen kann).
                if (Path.IsPathRooted(dataSource))
                    return new DatabaseSettings(false, connectionString, dataSource, null);

                var absolute = Path.GetFullPath(dataSource, contentRootPath);
                builder.DataSource = absolute;
                return new DatabaseSettings(false, builder.ToString(), absolute, null);

            default:
                throw new InvalidOperationException($"Unsupported Database:Provider '{provider}'. Use 'MySql' or 'Sqlite'.");
        }
    }
}

/// <summary>
/// SQLite-Betrieb: Auf jeder geöffneten Verbindung (auch der von SchemaUpgrader und Backup) gelten
/// <c>journal_mode=WAL</c> (Leser blockieren Schreiber nicht), <c>busy_timeout=5000</c> (ein gesperrter Schreibzugriff
/// wartet bis zu 5 s statt sofort zu scheitern) und <c>synchronous=NORMAL</c> (unter WAL sicher und schneller als FULL).
/// journal_mode ist in der Datei persistent, die anderen beiden gelten je Verbindung.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    private const string Pragmas = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
