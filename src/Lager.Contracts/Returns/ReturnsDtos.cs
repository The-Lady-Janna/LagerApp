namespace Lager.Contracts.Returns;

public record ReturnLineDto(
    Guid Id,
    Guid ArticleId,
    string ArticleSku,
    int Quantity,
    string? LotNumber,
    string QcResult,
    Guid? TargetBinId,
    string? QcNotes);

public record ReturnShipmentDto(
    Guid Id,
    string RmaNumber,
    Guid? OrderId,
    string? CustomerReference,
    string? Notes,
    string Status,
    DateTime CreatedAt,
    DateTime? ProcessedAt,
    IReadOnlyList<ReturnLineDto> Lines);

/// <summary>
/// Retoure anlegen. Mit <paramref name="OrderId"/> wird gegen die Bestellung geprüft: nur Artikel der Bestellung,
/// höchstens die gelieferte Menge abzüglich früherer Retouren.
/// </summary>
public record CreateReturnShipmentRequest(
    Guid? OrderId,
    string? CustomerReference,
    string? Notes,
    IReadOnlyList<CreateReturnLineRequest> Lines);

public record CreateReturnLineRequest(Guid ArticleId, int Quantity, string? LotNumber = null);

public record AddReturnLineRequest(Guid ArticleId, int Quantity, string? LotNumber = null);

/// <summary>QC-Ergebnis einer Zeile; <paramref name="Result"/> nur als Name: Pending, Sellable, BGrade, Defect oder Destroy (keine Zahlen).</summary>
public record SetQcRequest(string Result, Guid? TargetBinId, string? Notes);
