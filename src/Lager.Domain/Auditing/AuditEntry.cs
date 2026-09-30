namespace Lager.Domain.Auditing;

/// <summary>
/// One audit record per Insert/Update/Delete on a tracked entity.
/// Written automatically by AuditingInterceptor during SaveChanges.
/// </summary>
public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>Optional — filled when authentication is added later.</summary>
    public string? User { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    /// <summary>"Added", "Modified" or "Deleted".</summary>
    public string Operation { get; set; } = string.Empty;
    /// <summary>
    /// JSON: { "PropertyName": { "old": ..., "new": ... } } for Modified;
    /// { "PropertyName": value } for Added; null for Deleted.
    /// </summary>
    public string? ChangesJson { get; set; }
}
