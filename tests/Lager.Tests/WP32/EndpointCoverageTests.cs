using Lager.Api.Controllers;
using Lager.Tests.WP02;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Tests.WP32;

/// <summary>
/// Die Rollenmatrix (WP02) deckt jede [Http*]-Action jeder Controller-Klasse ab - auch die neuen Routen, die in eigenen
/// Klassen mit dem Routenpräfix eines anderen Controllers liegen (Storno einer Bestellung, Wareneingang aus der Bestellung).
/// Hier ohne Host: die Zählung selbst und der Nachweis, dass eine Action ohne Tabellenzeile auffällt.
/// </summary>
public class EndpointCoverageTests
{
    /// <summary>Eine Controller-Klasse, wie sie jemand später ergänzen könnte: neues Präfix, mehrere Actions, keine Tabellenzeile.</summary>
    [ApiController]
    [Route("api/wp32-probe")]
    private sealed class UnlistedProbeController : ControllerBase
    {
        [HttpGet]
        public IActionResult List() => Ok();

        [HttpPost("{id:guid}/archive")]
        public IActionResult Archive(Guid id) => Ok();

        [HttpPut("{id:guid}")]
        [HttpPatch("{id:guid}")]
        public IActionResult Change(Guid id) => Ok();

        [NonAction]
        public IActionResult Helper() => Ok();

        public IActionResult NoHttpAttribute() => Ok();
    }

    [Fact]
    public void A_new_http_action_without_a_table_row_is_reported()
    {
        var actions = EndpointMatrix.ScanActions(new[] { typeof(UnlistedProbeController) });

        // jede [Http*]-Attribut-Kombination zählt (auch zwei Attribute an einer Action), NonAction und Methoden ohne Attribut nicht
        Assert.Equal(
            new[] { "GET api/wp32-probe", "PATCH api/wp32-probe/{id:guid}", "POST api/wp32-probe/{id:guid}/archive", "PUT api/wp32-probe/{id:guid}" },
            actions.Select(a => a.Key).Order(StringComparer.Ordinal).ToArray());

        // ... und keine davon hat eine Zeile oder eine ausdrückliche Ausnahme: der Matrix-Test würde rot
        Assert.Equal(actions.Count, EndpointMatrix.Uncovered(actions.Select(a => a.Key)).Count);
    }

    [Fact]
    public void Every_http_action_of_the_api_assembly_has_a_table_row_or_an_explicit_exemption()
    {
        var keys = EndpointMatrix.ScanActions(typeof(OrdersController).Assembly.GetTypes())
            .Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncovered = EndpointMatrix.Uncovered(keys);
        Assert.True(uncovered.Count == 0, "Actions ohne Tabellenzeile: " + string.Join(", ", uncovered));

        // und umgekehrt: keine Zeile ohne echte Action (Tippfehler, gelöschte Endpunkte)
        var stale = EndpointMatrix.Rules.Select(r => r.Key).Where(k => !keys.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Tabellenzeilen ohne Action: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData(typeof(OrderCancellationController), "POST", "api/orders/{id:guid}/cancel", Tier.Manager)]
    [InlineData(typeof(PurchaseOrderInboundController), "POST", "api/purchase-orders/{id:guid}/create-inbound", Tier.Receiver)]
    public void Actions_in_a_second_controller_class_with_a_shared_route_prefix_are_in_the_table(Type controller, string method, string template, Tier tier)
    {
        var scanned = EndpointMatrix.ScanActions(new[] { controller });

        Assert.Equal($"{method} {template}", Assert.Single(scanned).Key);
        Assert.Equal(tier, EndpointMatrix.Find(method, template).Tier);
        Assert.True(EndpointMatrix.Find(method, template).Probe, "Die Rollen werden per HTTP durchprobiert");
    }
}
