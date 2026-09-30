using System.Text.Json;

namespace Lager.Application.ImportExport;

/// <summary>Der eine Sammel-Eintrag eines Imports, so wie ihn der AuditingInterceptor in den Audit-Trail schreibt.</summary>
public sealed record AuditBatchEntry(string EntityType, string EntityId, string Operation, string ChangesJson);

/// <summary>
/// Sammelmodus des Audit-Trails für Importe. Ohne Sammelmodus schreibt der AuditingInterceptor je geänderter Entität eine
/// Audit-Zeile: ein Import von 1000 Artikeln ergäbe 1000 Zeilen, die den Trail fluten. Während ein Import die Änderungen
/// schreibt (<see cref="Begin"/> ... Dispose) ist der Sammelmodus aktiv: der Interceptor schreibt dann KEINE Einzelzeilen,
/// sondern zählt die Änderungen (<see cref="Count"/>) und hängt beim nächsten SaveChanges EINEN Sammel-Eintrag an,
/// sobald der Import ihn mit <see cref="Complete"/> freigegeben hat (<see cref="TakePending"/>).
/// Ohne aktiven Sammelmodus ändert sich am Auditing nichts. Scoped: ein Scope (Request) hat genau einen Sammelmodus.
/// </summary>
public interface IAuditBatch
{
    /// <summary>Läuft gerade ein Import im Sammelmodus?</summary>
    bool IsActive { get; }

    /// <summary>Schaltet den Sammelmodus ein; Dispose schaltet ihn wieder aus. Verschachtelt nicht (<see cref="InvalidOperationException"/>).</summary>
    IDisposable Begin(string entityType, string entityId, string operation);

    /// <summary>Der Interceptor meldet eine unterdrückte Änderung (Entitätstyp, "Added"/"Modified"/"Deleted").</summary>
    void Count(string entityType, string operation);

    /// <summary>
    /// Gibt den Sammel-Eintrag frei: <paramref name="summary"/> ist der Text ("CSV-Import: 1200 Artikel, Nutzer X"),
    /// <paramref name="details"/> kommen mit in den Eintrag. Geschrieben wird er mit dem nächsten SaveChanges.
    /// </summary>
    void Complete(string summary, IReadOnlyDictionary<string, object?>? details = null);

    /// <summary>Der freigegebene Sammel-Eintrag samt den Zählungen bis jetzt (einmalig; danach null).</summary>
    AuditBatchEntry? TakePending();
}

/// <inheritdoc cref="IAuditBatch"/>
public sealed class AuditBatch : IAuditBatch
{
    private readonly Dictionary<string, Dictionary<string, int>> _counts = new(StringComparer.Ordinal);
    private string? _entityType;
    private string? _entityId;
    private string? _operation;
    private string? _summary;
    private IReadOnlyDictionary<string, object?>? _details;
    private bool _pending;

    public bool IsActive { get; private set; }

    public IDisposable Begin(string entityType, string entityId, string operation)
    {
        if (IsActive) throw new InvalidOperationException("Ein Import-Sammelmodus läuft bereits.");
        IsActive = true;
        _counts.Clear();
        _pending = false;
        _summary = null;
        _details = null;
        _entityType = entityType;
        _entityId = entityId;
        _operation = operation;
        return new Scope(this);
    }

    public void Count(string entityType, string operation)
    {
        if (!_counts.TryGetValue(entityType, out var operations))
            _counts[entityType] = operations = new Dictionary<string, int>(StringComparer.Ordinal);
        operations[operation] = operations.GetValueOrDefault(operation) + 1;
    }

    public void Complete(string summary, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (!IsActive) throw new InvalidOperationException("Es läuft kein Import-Sammelmodus.");
        _summary = summary;
        _details = details;
        _pending = true;
    }

    public AuditBatchEntry? TakePending()
    {
        if (!IsActive || !_pending) return null;
        _pending = false;

        var json = new Dictionary<string, object?> { ["summary"] = _summary };
        if (_details is not null)
            foreach (var (key, value) in _details) json[key] = value;
        json["entities"] = _counts.ToDictionary(kv => kv.Key, kv => (object?)new Dictionary<string, int>(kv.Value));
        return new AuditBatchEntry(_entityType!, _entityId!, _operation!, JsonSerializer.Serialize(json));
    }

    private void End()
    {
        IsActive = false;
        _pending = false;
    }

    private sealed class Scope : IDisposable
    {
        private AuditBatch? _owner;
        public Scope(AuditBatch owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.End();
            _owner = null;
        }
    }
}
