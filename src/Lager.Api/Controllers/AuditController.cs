using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lager.Api.Controllers;

/// <summary>Audit-Trail (wer hat wann welchen Datensatz geändert). Nur Rolle Manager; der CSV-Export steht unter <c>/api/export/audit.csv</c>.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = "Manager")]
public class AuditController : ControllerBase
{
    private readonly LagerDbContext _db;

    public AuditController(LagerDbContext db) => _db = db;

    /// <summary>Ein Eintrag des Audit-Trails.</summary>
    /// <param name="Id">Id des Eintrags.</param>
    /// <param name="At">Zeitpunkt der Änderung (UTC).</param>
    /// <param name="User">Benutzername, der die Änderung ausgelöst hat; null bei Systemvorgängen.</param>
    /// <param name="EntityType">Typ des geänderten Datensatzes, z. B. <c>Article</c>.</param>
    /// <param name="EntityId">Id des geänderten Datensatzes.</param>
    /// <param name="Operation">Art der Änderung: <c>Added</c>, <c>Modified</c> oder <c>Deleted</c>.</param>
    /// <param name="ChangesJson">Geänderte Felder als JSON-Text; null, wenn keine Felder erfasst sind.</param>
    public record AuditEntryDto(Guid Id, DateTime At, string? User, string EntityType, string EntityId, string Operation, string? ChangesJson);

    /// <summary>Listet Audit-Einträge, neueste zuerst.</summary>
    /// <remarks>Die Filter sind optional und kombinierbar.</remarks>
    /// <param name="entity">Nur Einträge dieses Datensatztyps (siehe <c>entity-types</c>).</param>
    /// <param name="id">Nur Einträge dieses Datensatzes.</param>
    /// <param name="take">Höchstens so viele Einträge (1 bis 1000, Standard 200).</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AuditEntryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IReadOnlyList<AuditEntryDto>> List(
        [FromQuery] string? entity,
        [FromQuery] string? id,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var q = _db.AuditEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(entity)) q = q.Where(a => a.EntityType == entity);
        if (!string.IsNullOrEmpty(id)) q = q.Where(a => a.EntityId == id);

        var items = await q.OrderByDescending(a => a.At).Take(take).ToListAsync(ct);
        return items.Select(a => new AuditEntryDto(a.Id, a.At, a.User, a.EntityType, a.EntityId, a.Operation, a.ChangesJson)).ToList();
    }

    /// <summary>Listet die Datensatztypen, zu denen es Audit-Einträge gibt (Auswahl für den Filter der Oberfläche).</summary>
    [HttpGet("entity-types")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<string>> EntityTypes(CancellationToken ct = default) =>
        await _db.AuditEntries.AsNoTracking()
            .Select(a => a.EntityType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
}
