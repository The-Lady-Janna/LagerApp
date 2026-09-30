using Lager.Application.Abstractions;
using Lager.Application.Orders;
using Lager.Contracts.Orders;
using Lager.Domain.Articles;
using Lager.Domain.Orders;

namespace Lager.Application.ImportExport;

/// <summary>Eine Bestellung, die angelegt werden soll (alle Positionen der Datei mit dieser Bestellnummer).</summary>
internal sealed record OrderChange(int Line, string Key, CreateOrderRequest Request) : PlannedChange(Line, Key);

/// <summary>
/// Bestellimport: neue Bestellungen anlegen, eine Zeile je Position (Bestellnummer, SKU, Menge); die Positionen mit derselben
/// Bestellnummer gehören zu EINER Bestellung, ihre Kopfangaben (Kundenreferenz, Priorität, Fälligkeit, externe Referenz) stehen
/// in einer der Zeilen (oder in allen, dann gleich).
///  - Angelegt wird über <see cref="OrderService.CreateAsync"/>, mit denselben Regeln wie über die API. Eine Bestellung wird ganz
///    oder gar nicht angelegt: hat eine ihrer Zeilen einen Fehler, entsteht sie nicht.
///  - Eine schon vorhandene Bestellnummer ist ein Fehler (<c>duplicate_order_number</c>) - außer die Zeile nennt dieselbe externe
///    Referenz wie die vorhandene Bestellung: dann ist es eine Wiederholung und zählt als "unverändert" (wie bei der externen
///    Bestell-API). Bestehende Bestellungen werden nie geändert.
///  - Positionen nennen den Artikel per SKU; unbekannte und außerhalb ihres Saison-Fensters liegende Artikel sind Fehler.
///  - Kunde und Lieferadresse kennt die Datei nicht.
/// </summary>
internal sealed class OrderImporter : IImportHandler
{
    private const int MaxOrderNumber = 64;
    private const int MaxCustomerReference = 128;

    private readonly OrderService _orders;
    private readonly IArticleRepository _articles;
    private readonly IOrderRepository _repository;

    public OrderImporter(OrderService orders, IArticleRepository articles, IOrderRepository repository)
    {
        _orders = orders;
        _articles = articles;
        _repository = repository;
    }

    /// <summary>Eine gelesene Positionszeile mit den Kopfangaben, die sie nennt (leer = nicht genannt).</summary>
    private sealed record Position(int Line, Article Article, int Quantity, string? Customer, int? Priority, DateTime? DueDate, string? External);

    public async Task<ImportPlan> PlanAsync(CsvTable table, CancellationToken ct)
    {
        var numberCol = ImportHeaders.Require(table, "OrderNumber");
        var skuCol = ImportHeaders.Require(table, "Sku");
        var quantityCol = ImportHeaders.Require(table, "Quantity");
        var customerCol = table.IndexOf("CustomerReference");
        var priorityCol = table.IndexOf("Priority");
        var dueCol = table.IndexOf("DueDate");
        var externalCol = table.IndexOf("ExternalReference");

        var articles = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in await _articles.ListAsync(ct)) articles.TryAdd(article.Sku, article);

        var errors = new List<ImportRowError>();
        var problems = new ProblemCollector();

        // Positionen je Bestellnummer, in der Reihenfolge des ersten Auftretens.
        var groups = new Dictionary<string, List<CsvRow>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var row in table.Rows)
        {
            var number = CsvTable.Cell(row, numberCol);
            if (number.Length == 0)
            {
                errors.Add(new ImportRowError(row.Line, null, "order_number_missing", "Die Bestellnummer fehlt."));
                continue;
            }
            if (!groups.TryGetValue(number, out var rows))
            {
                groups[number] = rows = new List<CsvRow>();
                order.Add(number);
            }
            rows.Add(row);
        }

        var changes = new List<PlannedChange>();
        var now = DateTime.UtcNow;
        int created = 0, unchanged = 0;

        foreach (var number in order)
        {
            var rows = groups[number];
            var first = rows[0];

            if (number.Length > MaxOrderNumber)
            {
                errors.Add(new ImportRowError(first.Line, RowReader.Shorten(number), "order_number_too_long", $"Die Bestellnummer darf höchstens {MaxOrderNumber} Zeichen lang sein."));
                continue;
            }

            var positions = new List<Position>();
            var failed = false;
            foreach (var row in rows)
            {
                problems.Clear();
                if (table.OverflowProblem(row) is { } overflow)
                {
                    errors.Add(new ImportRowError(row.Line, number, "too_many_columns", overflow));
                    failed = true;
                    continue;
                }

                var reader = new RowReader(row, problems);
                var sku = reader.Text(skuCol);
                Article? article = null;
                if (sku.Length == 0) problems.Add("sku_missing", "Die SKU fehlt.");
                else if (!articles.TryGetValue(sku, out article)) problems.Add("unknown_sku", $"Unbekannte SKU '{RowReader.Shorten(sku)}'.");
                else if (!article.IsCurrentlyActive(now)) problems.Add("article_not_orderable", $"'{article.Sku}' ist außerhalb seines Saison-Fensters nicht bestellbar.");

                var quantity = 0;
                if (reader.Text(quantityCol).Length == 0) problems.Add("quantity_missing", "Die Menge fehlt.");
                else
                {
                    quantity = reader.Int(quantityCol, "Quantity", 0);
                    if (!problems.Any && (quantity < 1 || quantity > OrderService.MaxQuantity))
                        problems.Add("invalid_quantity", $"Die Menge muss zwischen 1 und {OrderService.MaxQuantity} liegen ({quantity}).");
                }

                var customer = customerCol >= 0 && reader.Text(customerCol).Length > 0 ? reader.Text(customerCol) : null;
                if (customer is { Length: > MaxCustomerReference }) problems.Add("customer_reference_too_long", $"Die Kundenreferenz darf höchstens {MaxCustomerReference} Zeichen lang sein.");

                int? priority = null;
                if (priorityCol >= 0 && reader.Text(priorityCol).Length > 0)
                {
                    priority = reader.Int(priorityCol, "Priority", 0);
                    if (priority is < 0 or > Order.MaxPriority) problems.Add("invalid_priority", $"Die Priorität muss zwischen 0 und {Order.MaxPriority} liegen.");
                }

                var due = dueCol >= 0 ? reader.Date(dueCol, "DueDate", null) : null;
                var external = externalCol >= 0 && reader.Text(externalCol).Length > 0 ? reader.Text(externalCol) : null;
                if (external is { Length: > Order.MaxExternalReferenceLength })
                    problems.Add("external_reference_too_long", $"Die externe Referenz darf höchstens {Order.MaxExternalReferenceLength} Zeichen lang sein.");

                if (problems.Any)
                {
                    errors.Add(problems.ToError(row.Line, number));
                    failed = true;
                    continue;
                }
                positions.Add(new Position(row.Line, article!, quantity, customer, priority, due, external));
            }
            if (failed) continue;

            // Kopfangaben: eine Nennung genügt, mehrere müssen übereinstimmen.
            if (HeaderMismatch(positions, number, errors)) continue;
            var customerRef = positions.Select(p => p.Customer).FirstOrDefault(v => v is not null);
            var priorityValue = positions.Select(p => p.Priority).FirstOrDefault(v => v is not null) ?? 0;
            var dueDate = positions.Select(p => p.DueDate).FirstOrDefault(v => v is not null);
            var externalRef = positions.Select(p => p.External).FirstOrDefault(v => v is not null);

            var existing = await _repository.GetByNumberAsync(number, ct);
            if (existing is not null)
            {
                if (externalRef is not null && string.Equals(existing.ExternalReference, externalRef, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }
                errors.Add(new ImportRowError(first.Line, number, "duplicate_order_number", $"Die Bestellung '{number}' existiert bereits."));
                continue;
            }

            if (positions.Count > OrderService.MaxLines)
            {
                errors.Add(new ImportRowError(first.Line, number, "too_many_lines", $"Eine Bestellung darf höchstens {OrderService.MaxLines} Positionen haben."));
                continue;
            }

            var request = new CreateOrderRequest(
                number, customerRef, positions.Select(p => new CreateOrderLineRequest(p.Article.Id, p.Quantity)).ToList(),
                Priority: priorityValue, DueDate: dueDate, ExternalReference: externalRef);
            changes.Add(new OrderChange(first.Line, number, request));
            created++;
        }

        return new ImportPlan(table.Rows.Count, changes, created, 0, unchanged, errors.OrderBy(e => e.Line).ToList(),
            ImportHeaders.Warnings(table, CsvColumns.Orders));
    }

    /// <summary>Nennen zwei Zeilen derselben Bestellung verschiedene Kopfangaben, ist die spätere ein Fehler (welche gilt sonst?).</summary>
    private static bool HeaderMismatch(IReadOnlyList<Position> positions, string number, List<ImportRowError> errors)
    {
        var found = false;

        void Check<T>(string label, Func<Position, T?> select) where T : struct
        {
            Position? reference = null;
            foreach (var position in positions)
            {
                if (select(position) is not { } value) continue;
                if (reference is null) { reference = position; continue; }
                if (!EqualityComparer<T>.Default.Equals(select(reference)!.Value, value))
                {
                    errors.Add(new ImportRowError(position.Line, number, "order_header_mismatch",
                        $"{label} weicht von Zeile {reference.Line} ab: alle Positionen einer Bestellung müssen dieselbe Angabe nennen."));
                    found = true;
                }
            }
        }

        void CheckText(string label, Func<Position, string?> select)
        {
            Position? reference = null;
            foreach (var position in positions)
            {
                var value = select(position);
                if (value is null) continue;
                if (reference is null) { reference = position; continue; }
                if (!string.Equals(select(reference), value, StringComparison.Ordinal))
                {
                    errors.Add(new ImportRowError(position.Line, number, "order_header_mismatch",
                        $"{label} weicht von Zeile {reference.Line} ab: alle Positionen einer Bestellung müssen dieselbe Angabe nennen."));
                    found = true;
                }
            }
        }

        CheckText("Die Kundenreferenz", p => p.Customer);
        Check<int>("Die Priorität", p => p.Priority);
        Check<DateTime>("Die Fälligkeit", p => p.DueDate);
        CheckText("Die externe Referenz", p => p.External);
        return found;
    }

    public async Task ApplyAsync(IReadOnlyList<PlannedChange> changes, ApplyContext context, CancellationToken ct)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var change = (OrderChange)changes[i];
            if (i == changes.Count - 1) context.BeforeLastChange();

            await _orders.CreateAsync(change.Request, OrderSource.Manual, ct);

            if ((i + 1) % 100 == 0) context.Transaction.ReleaseTrackedEntities();
        }
    }
}
