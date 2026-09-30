namespace Lager.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    /// <summary>
    /// Zeitstempel sind immer UTC. Die Domain setzt <c>DateTime.UtcNow</c>; die Persistenz liest sie als
    /// <c>DateTimeKind.Utc</c> zurück (Value-Converter im DbContext), damit die JSON-Ausgabe ein "Z" trägt.
    /// </summary>
    public DateTime CreatedAt { get; protected set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; protected set; } = DateTime.UtcNow;

    /// <summary>
    /// Rotated on every mutation via <see cref="Touch"/>. EF compares it on
    /// UPDATE/DELETE; a mismatch means somebody else changed the row first
    /// and the operation fails with DbUpdateConcurrencyException → HTTP 409.
    /// SQLite has no native rowversion, so we use a Guid bumped by the domain.
    /// EF erzwingt die Prüfung nur an den Aggregaten, die in ConcurrencyTokenConfiguration als Token
    /// konfiguriert sind (Bestand und Belege mit Statuswechsel); an den übrigen ist es nur eine Spalte.
    /// </summary>
    public Guid ConcurrencyToken { get; protected set; } = Guid.NewGuid();

    public void Touch()
    {
        UpdatedAt = DateTime.UtcNow;
        ConcurrencyToken = Guid.NewGuid();
    }
}
