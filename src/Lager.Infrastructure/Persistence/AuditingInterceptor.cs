using System.Collections.Concurrent;
using System.Text.Json;
using Lager.Application.Abstractions;
using Lager.Application.ImportExport;
using Lager.Domain.Articles;
using Lager.Domain.Auditing;
using Lager.Domain.Auth;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Infrastructure.Persistence;

/// <summary>
/// Records before/after diffs for business-relevant entities during SaveChanges.
/// Skips audit-related entries themselves to prevent feedback loops, and skips
/// joins/derived data (PickItem, OrderLine etc.) — those bubble up via their
/// parent's Modified state.
///
/// Owned Types (Abmessungen, Positionen ...) stehen im Diff des Besitzers mit Präfix
/// ("Dimensions.LengthMm"); Deletes protokollieren den zuletzt gespeicherten Zustand.
/// Benutzer werden auditiert, der PasswordHash nur maskiert ("***") - nie sein Wert.
///
/// Sammelmodus (IAuditBatch): Während ein CSV-Import schreibt, entstehen statt je einer Zeile pro Entität
/// nur Zählungen und am Ende EIN Sammel-Eintrag (Entitätstyp "CsvImport"); ohne aktiven Sammelmodus
/// bleibt alles wie oben beschrieben.
///
/// The current user (from ICurrentUser) is stamped into each AuditEntry.User:
/// "anonymous" für Requests ohne Anmeldung, "system" nur für Arbeit außerhalb eines
/// Requests (Seeder, Startup, Hintergrund).
/// </summary>
public class AuditingInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<Type> AuditedTypes = new()
    {
        typeof(Article),
        typeof(StockItem),
        typeof(Order),
        typeof(PickList),
        typeof(Wall),
        typeof(StorageLocation),
        typeof(WarehouseEntity),
        typeof(PickPoint),
        typeof(Shelf),
        typeof(PickCartConfig),
        typeof(User),
    };

    /// <summary>Rauschen: Zeitstempel, Concurrency-Token.</summary>
    private static readonly HashSet<string> NoiseProperties = new() { "UpdatedAt", "CreatedAt", "ConcurrencyToken" };

    /// <summary>Werte dieser Eigenschaften landen nie im Audit-Trail; protokolliert wird nur, DASS sie sich geändert haben.</summary>
    private static readonly HashSet<string> MaskedProperties = new() { "PasswordHash" };

    /// <summary>
    /// Reine Login-Buchhaltung des Benutzers (jeder Login ändert sie). Gehört in das Sicherheits-Log,
    /// nicht in den Audit-Trail - sonst erzeugt jede Anmeldung einen Eintrag.
    /// </summary>
    private static readonly HashSet<string> LoginBookkeeping = new() { "LastLoginAt", "FailedLoginAttempts", "LockedUntil" };

    private const string Masked = "***";

    private static readonly ConcurrentDictionary<IEntityType, string[]> OwnedNavigationNames = new();

    private readonly ICurrentUser? _currentUser;
    private readonly IAuditBatch? _batch;

    public AuditingInterceptor(ICurrentUser? currentUser = null, IAuditBatch? batch = null)
    {
        _currentUser = currentUser;
        _batch = batch;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteAudit(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        WriteAudit(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void WriteAudit(DbContext? ctx)
    {
        if (ctx is null) return;

        // Snapshot entries first — the loop below adds new ones (AuditEntry rows)
        // to the change tracker, and modifying it during iteration would throw.
        var tracked = ctx.ChangeTracker.Entries().ToList();
        var audited = tracked.Where(e => AuditedTypes.Contains(e.Entity.GetType())).ToList();

        // Sammelmodus eines Imports: Einzelzeilen entfallen, gezählt wird; den freigegebenen Sammel-Eintrag schreibt dieses SaveChanges.
        var batch = _batch is { IsActive: true } ? _batch : null;
        if (audited.Count == 0 && batch is null) return;

        // Wird ein Owned Type durch eine neue Instanz ersetzt (article.Dimensions = new ...), markiert EF die
        // alte als Deleted und die neue als Added. Die alten Werte braucht der Vorher/Nachher-Diff.
        var replacedOwned = new Dictionary<(IEntityType, string), EntityEntry>();
        foreach (var e in tracked.Where(e => e.State == EntityState.Deleted && e.Metadata.IsOwned()))
            replacedOwned.TryAdd((e.Metadata, KeyOf(e)), e);

        // Capture user identity ONCE per SaveChanges — cheap on lookup, and
        // avoids repeated null-coalescing on every audit row.
        var user = ResolveActor();

        var audits = new List<AuditEntry>();
        foreach (var entry in audited)
        {
            var owned = OwnedEntries(entry);
            var ownerChanged = IsChanged(entry.State);
            if (!ownerChanged && !owned.Any(o => IsChanged(o.Entry.State))) continue;

            var changes = BuildChanges(entry, owned, replacedOwned);

            // Nur ein Owned Type wurde ersetzt, aber inhaltlich nichts geändert: kein Eintrag.
            if (!ownerChanged && changes is null) continue;
            // Ein Benutzer, dessen einzige Änderung die Login-Buchhaltung ist, erzeugt keinen Eintrag.
            if (entry.Entity is User && entry.State == EntityState.Modified && changes is null) continue;

            var entityType = entry.Entity.GetType().Name;
            var operation = (ownerChanged ? entry.State : EntityState.Modified).ToString();
            if (batch is not null)
            {
                batch.Count(entityType, operation);
                continue;
            }

            audits.Add(new AuditEntry
            {
                EntityType = entityType,
                EntityId = GetPrimaryKey(entry),
                Operation = operation,
                ChangesJson = changes,
                User = user,
            });
        }

        if (batch?.TakePending() is { } pending)
        {
            audits.Add(new AuditEntry
            {
                EntityType = pending.EntityType,
                EntityId = pending.EntityId,
                Operation = pending.Operation,
                ChangesJson = pending.ChangesJson,
                User = user,
            });
        }

        if (audits.Count > 0) ctx.Set<AuditEntry>().AddRange(audits);
    }

    private static string KeyOf(EntityEntry entry) =>
        string.Join("|", entry.Metadata.FindPrimaryKey()?.Properties.Select(p => entry.Property(p.Name).CurrentValue) ?? Array.Empty<object?>());

    private string ResolveActor()
    {
        if (_currentUser is null || _currentUser.IsSystemContext) return "system";
        return _currentUser.IsAuthenticated ? _currentUser.Username ?? "anonymous" : "anonymous";
    }

    private static bool IsChanged(EntityState s) => s is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static string GetPrimaryKey(EntityEntry entry)
    {
        var keyProp = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return keyProp?.CurrentValue?.ToString() ?? "?";
    }

    /// <summary>Die (geladenen) Owned-Type-Instanzen des Eintrags samt Navigationsname, z. B. ("Dimensions", entry).</summary>
    private static List<(string Name, EntityEntry Entry)> OwnedEntries(EntityEntry entry)
    {
        var names = OwnedNavigationNames.GetOrAdd(entry.Metadata, t => t.GetNavigations()
            .Where(n => n.ForeignKey.IsOwnership && !n.IsOnDependent && !n.IsCollection)
            .Select(n => n.Name)
            .ToArray());

        var result = new List<(string, EntityEntry)>(names.Length);
        foreach (var name in names)
        {
            var target = entry.Reference(name).TargetEntry;
            if (target is not null) result.Add((name, target));
        }
        return result;
    }

    private static string? BuildChanges(
        EntityEntry entry, List<(string Name, EntityEntry Entry)> owned, Dictionary<(IEntityType, string), EntityEntry> replacedOwned)
    {
        var dict = new Dictionary<string, object?>();
        var isUser = entry.Entity is User;

        AddProperties(dict, entry, prefix: "", entry.State, isUser);
        foreach (var (name, ownedEntry) in owned)
        {
            var prefix = name + ".";
            // Bei Added/Deleted des Besitzers gilt derselbe Zustand für die Owned Types.
            if (entry.State is EntityState.Added or EntityState.Deleted)
            {
                AddProperties(dict, ownedEntry, prefix, entry.State, isUser: false);
            }
            else if (ownedEntry.State == EntityState.Added && replacedOwned.TryGetValue((ownedEntry.Metadata, KeyOf(ownedEntry)), out var previous))
            {
                // Ersetzte Instanz: Vorher/Nachher je Eigenschaft, nur echte Unterschiede.
                foreach (var prop in ownedEntry.Properties)
                {
                    if (IsOwnedKey(prop) || NoiseProperties.Contains(prop.Metadata.Name)) continue;
                    var oldValue = previous.Property(prop.Metadata.Name).OriginalValue;
                    if (!Equals(oldValue, prop.CurrentValue))
                        dict[prefix + prop.Metadata.Name] = new { old = oldValue, @new = prop.CurrentValue };
                }
            }
            else
            {
                AddProperties(dict, ownedEntry, prefix, ownedEntry.State, isUser: false);
            }
        }

        return dict.Count == 0 ? null : JsonSerializer.Serialize(dict);
    }

    /// <summary>Schlüssel/Fremdschlüssel eines Owned Types sind nur der Verweis auf den Besitzer, kein Inhalt.</summary>
    private static bool IsOwnedKey(PropertyEntry prop) =>
        prop.EntityEntry.Metadata.IsOwned() && (prop.Metadata.IsPrimaryKey() || prop.Metadata.IsForeignKey());

    private static void AddProperties(
        Dictionary<string, object?> dict, EntityEntry entry, string prefix, EntityState state, bool isUser)
    {
        foreach (var prop in entry.Properties)
        {
            // Skip noise: timestamps, concurrency tokens, JSON shadow columns.
            var name = prop.Metadata.Name;
            if (NoiseProperties.Contains(name)) continue;
            if (IsOwnedKey(prop)) continue;
            if (isUser && LoginBookkeeping.Contains(name)) continue;

            var masked = MaskedProperties.Contains(name);
            var key = prefix + name;

            switch (state)
            {
                case EntityState.Added:
                    dict[key] = masked ? Masked : prop.CurrentValue;
                    break;
                case EntityState.Deleted:
                    // Letzter gespeicherter Zustand, damit sich ein Löschen nachvollziehen lässt.
                    dict[key] = masked ? Masked : prop.OriginalValue;
                    break;
                case EntityState.Modified when prop.IsModified:
                    dict[key] = masked
                        ? new { old = Masked, @new = Masked }
                        : new { old = prop.OriginalValue, @new = prop.CurrentValue };
                    break;
            }
        }
    }
}
