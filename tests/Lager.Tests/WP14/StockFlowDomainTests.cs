using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Domain.Purchasing;
using Lager.Domain.Returns;
using Lager.Domain.Stock;

namespace Lager.Tests.WP14;

/// <summary>Domain-Unit-Tests (ohne Host): die neuen Regeln an Lieferung, Inventurzeile, Retourenzeile, Bestellzeile und Movement.</summary>
public class StockFlowDomainTests
{
    private static readonly DateTime ExpiryA = new(2031, 3, 1);
    private static readonly DateTime ExpiryB = new(2031, 6, 1);

    [Fact]
    public void A_lot_in_one_bin_can_carry_only_one_expiry_inside_a_shipment()
    {
        var article = Guid.NewGuid();
        var bin = Guid.NewGuid();
        var s = new InboundShipment("WE-1", null, null);
        s.AddLine(article, bin, 1, "LOT-A", ExpiryA);

        // gleiche Charge, gleiches MHD (auch mit Uhrzeit am selben Tag) und andere Chargen/Plätze sind in Ordnung
        s.AddLine(article, bin, 1, "LOT-A", ExpiryA.AddHours(5));
        s.AddLine(article, bin, 1, "LOT-B", ExpiryB);
        s.AddLine(article, Guid.NewGuid(), 1, "LOT-A", ExpiryB);
        s.AddLine(article, bin, 1, null, null);
        s.AddLine(article, bin, 1, null, ExpiryB);   // ohne Charge ist das MHD frei: das sind zwei Bestandszeilen

        var ex = Assert.Throws<InvalidOperationException>(() => s.AddLine(article, bin, 1, "LOT-A", ExpiryB));
        Assert.Equal("lot_expiry_mismatch", ex.Data["code"]);
        Assert.Equal(6, s.Lines.Count);   // die abgelehnte Zeile wurde nicht angehängt
    }

    [Fact]
    public void Lot_numbers_are_normalized_and_limited_on_inbound_and_return_lines()
    {
        var s = new InboundShipment("WE-2", null, null);
        Assert.Null(s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, "   ", null).LotNumber);
        Assert.Equal("LOT-1", s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, "  LOT-1 ", null).LotNumber);
        Assert.Throws<ArgumentException>(() => s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, new string('x', 65), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, null, null, unitCostCents: -1));

        var r = new ReturnShipment("RMA-1", null, null, null);
        Assert.Null(r.AddLine(Guid.NewGuid(), "SKU", 1, "").LotNumber);
        Assert.Equal("LOT-2", r.AddLine(Guid.NewGuid(), "SKU", 1, " LOT-2 ").LotNumber);
        Assert.Throws<ArgumentException>(() => r.AddLine(Guid.NewGuid(), "SKU", 1, new string('x', 65)));
    }

    [Fact]
    public void A_purchase_order_line_can_only_be_referenced_by_a_shipment_that_belongs_to_an_order()
    {
        var free = new InboundShipment("WE-3", null, null);
        Assert.Throws<InvalidOperationException>(() => free.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, null, null, purchaseOrderLineId: Guid.NewGuid()));

        var order = Guid.NewGuid();
        var linked = new InboundShipment("WE-4", "PO-1", null, order);
        var line = linked.AddLine(Guid.NewGuid(), Guid.NewGuid(), 4, null, null, Guid.NewGuid(), 250);
        Assert.Equal(order, linked.PurchaseOrderId);
        Assert.Equal((250, true), (line.UnitCostCents, line.PurchaseOrderLineId is not null));
        Assert.Throws<ArgumentException>(() => new InboundShipment("WE-5", null, null, Guid.Empty));
    }

    [Fact]
    public void Inventory_lines_keep_the_lot_and_expiry_of_their_stock_row_and_the_diff_is_only_a_display_value()
    {
        var c = new InventoryCount("Stichtag");
        var line = c.AddSnapshotLine(Guid.NewGuid(), "A-01", Guid.NewGuid(), "SKU-1", 100, " LOT-9 ", ExpiryA);
        var plain = c.AddSnapshotLine(Guid.NewGuid(), "A-02", Guid.NewGuid(), "SKU-2", 5);

        Assert.Equal(("LOT-9", ExpiryA), (line.LotNumber, line.ExpiryDate));
        Assert.Equal((null, null), (plain.LotNumber, plain.ExpiryDate));
        c.SetCount(line.Id, 70, null);
        Assert.Equal(-30, line.Diff);
    }

    [Fact]
    public void A_QC_result_outside_the_enum_is_rejected_by_the_domain_too()
    {
        var r = new ReturnShipment("RMA-2", null, null, null);
        var line = r.AddLine(Guid.NewGuid(), "SKU", 1);

        var ex = Assert.Throws<ArgumentException>(() => r.SetLineQc(line.Id, (QcResult)99, null, null));
        Assert.Contains("99", ex.Message);
        Assert.Equal(QcResult.Pending, line.QcResult);
        r.SetLineQc(line.Id, QcResult.Defect, null, "zerbrochen");
        Assert.Equal(QcResult.Defect, line.QcResult);
    }

    [Fact]
    public void The_new_movement_reasons_have_stable_numbers_because_the_database_stores_them()
    {
        Assert.Equal((9, 10), ((int)StockMovementReason.ReturnB, (int)StockMovementReason.ReturnScrap));
        Assert.Equal(8, (int)StockMovementReason.BinMove);   // die bestehenden Werte bleiben unverändert
    }

    [Fact]
    public void A_reconciled_inventory_line_carries_the_counters_reason_followed_by_the_note_and_stays_within_the_column_width()
    {
        var c = new InventoryCount("Stichtag");
        var line = c.AddSnapshotLine(Guid.NewGuid(), "A-01", Guid.NewGuid(), "SKU-1", 10);
        var other = c.AddSnapshotLine(Guid.NewGuid(), "A-02", Guid.NewGuid(), "SKU-2", 10);
        c.SetCount(line.Id, 8, "Bruch");
        c.SetCount(other.Id, 10, null);

        // Hinweise nur beim Abgleich: vorher und für unbekannte Zeilen Fehler
        Assert.Throws<InvalidOperationException>(() => c.AnnotateLine(line.Id, "zu früh"));
        c.Reconcile();
        c.AnnotateLine(line.Id, "Bestand verändert");
        c.AnnotateLine(other.Id, "  nur der Hinweis ");
        c.AnnotateLine(other.Id, "  ");   // leer: nichts
        Assert.Throws<InvalidOperationException>(() => c.AnnotateLine(Guid.NewGuid(), "x"));

        Assert.Equal("Bruch | Bestand verändert", line.Reason);
        Assert.Equal("nur der Hinweis", other.Reason);
        c.AnnotateLine(line.Id, new string('x', 900));
        Assert.Equal(InventoryLine.MaxReasonLength, line.Reason!.Length);
    }

    [Fact]
    public void A_purchase_order_line_reports_its_open_quantity_and_the_order_is_open_only_while_it_expects_goods()
    {
        var po = new PurchaseOrder("PO-1", Guid.NewGuid(), "EUR");
        var line = po.AddLine(Guid.NewGuid(), "SKU", 10, 100);
        Assert.False(po.IsOpen);                 // Draft

        po.MarkSent();
        Assert.True(po.IsOpen);
        po.ReceiveLine(line.Id, 4);
        Assert.Equal((6, true), (line.OpenQuantity(), po.IsOpen));   // PartiallyReceived
        po.ReceiveLine(line.Id, 6);
        Assert.Equal((0, false), (line.OpenQuantity(), po.IsOpen));  // Received
    }
}
