using Lager.Application.Warehouse;
using Lager.Contracts.Warehouse;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Lagerstruktur (Lager, Zonen, Gänge, Regale, Lagerplätze), Wände und Pickpunkte. Lesen darf jeder Angemeldete, Ändern nur
/// die Rolle Manager. Fachliche Fehler (<c>duplicate_code</c>, <c>warehouse_not_empty</c>, <c>in_use</c>) sind 409 mit Code
/// (siehe ExceptionProblemMapper).
/// </summary>
/// <remarks>
/// Nur das Lager selbst hat einen Abruf per Id (<c>GET /api/warehouse/{id}</c>). Zonen, Gänge, Regale, Lagerplätze, Wände und Pickpunkte
/// stehen im Layout bzw. in ihren Listen: <c>Location</c> nach dem Anlegen zeigt auf die Liste, die den neuen Eintrag enthält.
/// </remarks>
[ApiController]
[Route("api/warehouse")]
[Authorize]
public class WarehouseController : ControllerBase
{
    private readonly WarehouseService _service;

    public WarehouseController(WarehouseService service) => _service = service;

    /// <summary>Liefert das ganze Lager-Layout: alle Lager mit Zonen, Gängen, Regalen und Lagerplätzen.</summary>
    [HttpGet("layout")]
    [ProducesResponseType<IReadOnlyList<WarehouseDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<WarehouseDto>> Layout(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert ein Lager samt Zonen, Gängen, Regalen und Lagerplätzen.</summary>
    /// <param name="id">Id des Lagers.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<WarehouseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WarehouseDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt ein Lager an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/warehouse/{id}</c>. Ein doppelter Code ist 409 (<c>duplicate_code</c>).</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<WarehouseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WarehouseDto>> CreateWarehouse([FromBody] CreateWarehouseRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateWarehouseAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Ändert Code und Name eines Lagers.</summary>
    /// <param name="id">Id des Lagers.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<WarehouseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WarehouseDto>> UpdateWarehouse(Guid id, [FromBody] UpdateWarehouseRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateWarehouseAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht das Lager samt leerer Unterstruktur.</summary>
    /// <remarks>409 <c>warehouse_not_empty</c>, wenn darunter Bestand oder offene Aufgaben liegen.</remarks>
    /// <param name="id">Id des Lagers.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteWarehouse(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteWarehouseAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Legt eine Zone in einem Lager an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf das Layout (<c>GET /api/warehouse/layout</c>). Ein unbekanntes Lager ist 404.</remarks>
    [HttpPost("zones")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ZoneDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ZoneDto>> CreateZone([FromBody] CreateZoneRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateZoneAsync(request, ct);
        return CreatedAtAction(nameof(Layout), new { }, dto);
    }

    /// <summary>Ändert Code, Name und Ursprung einer Zone.</summary>
    /// <param name="id">Id der Zone.</param>
    [HttpPut("zones/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ZoneDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ZoneDto>> UpdateZone(Guid id, [FromBody] UpdateZoneRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateZoneAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht eine Zone samt leerer Unterstruktur.</summary>
    /// <remarks>409 <c>warehouse_not_empty</c>, wenn darunter Bestand oder offene Aufgaben liegen.</remarks>
    /// <param name="id">Id der Zone.</param>
    [HttpDelete("zones/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteZone(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteZoneAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Legt einen Gang in einer Zone an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf das Layout (<c>GET /api/warehouse/layout</c>).</remarks>
    [HttpPost("aisles")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<AisleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AisleDto>> CreateAisle([FromBody] CreateAisleRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateAisleAsync(request, ct);
        return CreatedAtAction(nameof(Layout), new { }, dto);
    }

    /// <summary>Ändert Code, Verlauf und Ausrichtung eines Gangs.</summary>
    /// <param name="id">Id des Gangs.</param>
    [HttpPut("aisles/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<AisleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AisleDto>> UpdateAisle(Guid id, [FromBody] UpdateAisleRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateAisleAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht einen Gang samt leerer Unterstruktur.</summary>
    /// <remarks>409 <c>warehouse_not_empty</c>, wenn darunter Bestand oder offene Aufgaben liegen.</remarks>
    /// <param name="id">Id des Gangs.</param>
    [HttpDelete("aisles/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAisle(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteAisleAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Listet alle Lagerplätze.</summary>
    [HttpGet("storage-locations")]
    [ProducesResponseType<IReadOnlyList<StorageLocationDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<StorageLocationDto>> ListLocations(CancellationToken ct) => _service.ListStorageLocationsAsync(ct);

    /// <summary>Legt einen Lagerplatz in einem Regal an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf die Liste der Lagerplätze. Ein doppelter Code ist 409 (<c>duplicate_code</c>, der Code ist lagerübergreifend eindeutig).</remarks>
    [HttpPost("storage-locations")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StorageLocationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageLocationDto>> CreateLocation([FromBody] CreateStorageLocationRequest request, CancellationToken ct)
    {
        var dto = await _service.AddStorageLocationAsync(request, ct);
        return CreatedAtAction(nameof(ListLocations), new { }, dto);
    }

    /// <summary>Ändert Code und Maße eines Lagerplatzes.</summary>
    /// <param name="id">Id des Lagerplatzes.</param>
    [HttpPut("storage-locations/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StorageLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageLocationDto>> UpdateLocation(Guid id, [FromBody] UpdateStorageLocationRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateStorageLocationAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Verschiebt einen Lagerplatz (Position im Lager).</summary>
    /// <param name="id">Id des Lagerplatzes.</param>
    [HttpPut("storage-locations/{id:guid}/position")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StorageLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageLocationDto>> MoveLocation(Guid id, [FromBody] UpdatePositionRequest request, CancellationToken ct)
    {
        var dto = await _service.MoveStorageLocationAsync(id, request.Position, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Verschiebt ein Regal (Position im Lager).</summary>
    /// <param name="id">Id des Regals.</param>
    [HttpPut("shelves/{id:guid}/position")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ShelfDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShelfDto>> MoveShelf(Guid id, [FromBody] UpdatePositionRequest request, CancellationToken ct)
    {
        var dto = await _service.MoveShelfAsync(id, request.Position, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Ändert Code und Maße eines Regals.</summary>
    /// <param name="id">Id des Regals.</param>
    [HttpPut("shelves/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ShelfDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShelfDto>> UpdateShelf(Guid id, [FromBody] UpdateShelfRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateShelfAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht das Regal samt leeren Lagerplätzen.</summary>
    /// <remarks>409 <c>warehouse_not_empty</c>, wenn darin Bestand oder offene Aufgaben liegen.</remarks>
    /// <param name="id">Id des Regals.</param>
    [HttpDelete("shelves/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteShelf(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteShelfAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Legt ein Regal samt Anfangs-Lagerplätzen in einem Gang an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf das Layout (<c>GET /api/warehouse/layout</c>). Ein unbekannter Gang oder ein doppelter Regal-Code ist 409.</remarks>
    [HttpPost("shelves")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ShelfDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShelfDto>> CreateShelf([FromBody] CreateShelfRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateShelfAsync(request, ct);
        return CreatedAtAction(nameof(Layout), new { }, dto);
    }

    /// <summary>Fügt einem Regal einen Lagerplatz hinzu.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf die Liste der Lagerplätze. Ein doppelter Code ist 409 (<c>duplicate_code</c>).</remarks>
    /// <param name="id">Id des Regals.</param>
    [HttpPost("shelves/{id:guid}/bins")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StorageLocationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageLocationDto>> AddBin(Guid id, [FromBody] AddBinToShelfRequest request, CancellationToken ct)
    {
        var dto = await _service.AddBinToShelfAsync(id, request, ct);
        return dto is null ? NotFound() : CreatedAtAction(nameof(ListLocations), new { }, dto);
    }

    /// <summary>Löscht einen Lagerplatz.</summary>
    /// <remarks>409, wenn noch Bestand oder offene Aufgaben darauf liegen.</remarks>
    /// <param name="id">Id des Lagerplatzes.</param>
    [HttpDelete("storage-locations/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteBin(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteBinAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Setzt die Art eines Lagerplatzes (<c>Standard</c>, <c>HotPick</c> oder <c>Reserve</c>) und seine Nachschub-Schwelle.</summary>
    /// <param name="id">Id des Lagerplatzes.</param>
    [HttpPut("storage-locations/{id:guid}/bin-type")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StorageLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageLocationDto>> SetBinType(Guid id, [FromBody] SetBinTypeRequest req, CancellationToken ct)
    {
        var dto = await _service.SetBinTypeAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Listet die Wände (Hindernisse für die Wegberechnung).</summary>
    [HttpGet("walls")]
    [ProducesResponseType<IReadOnlyList<WallDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<WallDto>> ListWalls(CancellationToken ct) => _service.ListWallsAsync(ct);

    /// <summary>Legt eine Wand an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf die Liste der Wände.</remarks>
    [HttpPost("walls")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<WallDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WallDto>> CreateWall([FromBody] CreateWallRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateWallAsync(request, ct);
        return CreatedAtAction(nameof(ListWalls), new { }, dto);
    }

    /// <summary>Ersetzt die Eckpunkte einer Wand.</summary>
    /// <param name="id">Id der Wand.</param>
    [HttpPut("walls/{id:guid}/points")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<WallDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WallDto>> UpdateWallPoints(Guid id, [FromBody] UpdateWallPointsRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateWallPointsAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht eine Wand.</summary>
    /// <param name="id">Id der Wand.</param>
    [HttpDelete("walls/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteWall(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteWallAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Listet die Pickpunkte (Start- und Endpunkte der Pickrouten).</summary>
    [HttpGet("pick-points")]
    [ProducesResponseType<IReadOnlyList<PickPointDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PickPointDto>> ListPickPoints(CancellationToken ct) => _service.ListPickPointsAsync(ct);

    /// <summary>Legt einen Pickpunkt an.</summary>
    /// <remarks>Antwort 201; <c>Location</c> zeigt auf die Liste der Pickpunkte.</remarks>
    [HttpPost("pick-points")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickPointDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickPointDto>> CreatePickPoint([FromBody] CreatePickPointRequest request, CancellationToken ct)
    {
        var dto = await _service.CreatePickPointAsync(request, ct);
        return CreatedAtAction(nameof(ListPickPoints), new { }, dto);
    }

    /// <summary>Ändert Bezeichnung und Art eines Pickpunkts.</summary>
    /// <param name="id">Id des Pickpunkts.</param>
    [HttpPut("pick-points/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickPointDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickPointDto>> UpdatePickPoint(Guid id, [FromBody] UpdatePickPointRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdatePickPointAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Verschiebt einen Pickpunkt (Position im Lager).</summary>
    /// <param name="id">Id des Pickpunkts.</param>
    [HttpPut("pick-points/{id:guid}/position")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickPointDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickPointDto>> MovePickPoint(Guid id, [FromBody] UpdatePositionRequest request, CancellationToken ct)
    {
        var dto = await _service.MovePickPointAsync(id, request.Position, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht einen Pickpunkt.</summary>
    /// <param name="id">Id des Pickpunkts.</param>
    [HttpDelete("pick-points/{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeletePickPoint(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeletePickPointAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}
