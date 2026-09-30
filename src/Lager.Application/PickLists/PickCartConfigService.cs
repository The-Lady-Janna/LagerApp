using Lager.Application.Abstractions;
using Lager.Contracts.PickLists;
using Lager.Domain.PickLists;

namespace Lager.Application.PickLists;

public class PickCartConfigService
{
    private readonly IPickCartConfigRepository _repo;
    private readonly IUnitOfWork _uow;

    public PickCartConfigService(IPickCartConfigRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task<IReadOnlyList<PickCartConfigDto>> ListAsync(CancellationToken ct = default)
    {
        var items = await _repo.ListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<PickCartConfigDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await _repo.GetAsync(id, ct);
        return c is null ? null : ToDto(c);
    }

    public async Task<PickCartConfigDto> CreateAsync(CreatePickCartConfigRequest request, CancellationToken ct = default)
    {
        var existing = await _repo.GetByNameAsync(request.Name, ct);
        if (existing is not null) throw new InvalidOperationException($"PickCartConfig with name '{request.Name}' already exists");
        var entity = new PickCartConfig(request.Name, request.LevelCount, request.LevelWidthMm, request.LevelDepthMm, request.LevelHeightMm, request.MaxWeightGrams);
        await _repo.AddAsync(entity, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<PickCartConfigDto?> UpdateAsync(Guid id, UpdatePickCartConfigRequest request, CancellationToken ct = default)
    {
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) return null;
        entity.Update(request.Name, request.LevelCount, request.LevelWidthMm, request.LevelDepthMm, request.LevelHeightMm, request.MaxWeightGrams);
        await _uow.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) return false;
        _repo.Remove(entity);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    public static PickCartConfigDto ToDto(PickCartConfig c) => new(
        c.Id, c.Name, c.LevelCount, c.LevelWidthMm, c.LevelDepthMm, c.LevelHeightMm,
        c.MaxWeightGrams, c.TotalVolumeMm3);
}
