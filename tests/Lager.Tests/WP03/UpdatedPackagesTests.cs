using System.Text;
using Lager.Api.Documents;
using Lager.Contracts.PickLists;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace Lager.Tests.WP03;

/// <summary>
/// Regressionsschutz für die Paket-Updates (QuestPDF, Swashbuckle): Diese Bibliotheken werden von den
/// übrigen Tests nicht berührt, ein Update würde sonst erst im Betrieb auffallen.
/// </summary>
public class UpdatedPackagesTests
{
    [Fact]
    public void Shipping_label_renderer_produces_a_pdf()
    {
        var orderId = Guid.NewGuid();
        var items = new[]
        {
            new PickItemDto(Guid.NewGuid(), 1, orderId, "ORD-0001", Guid.NewGuid(), "SKU-1", "Schraube M4", Guid.NewGuid(), "A-01-01", 5, true, 5, DateTime.UtcNow),
            new PickItemDto(Guid.NewGuid(), 2, orderId, "ORD-0001", Guid.NewGuid(), "SKU-2", "Mutter M4", Guid.NewGuid(), "A-01-02", 3, false, null, null),
        };
        var pickList = new PickListDto(Guid.NewGuid(), "PL-0001", "Open", null, 0, DateTime.UtcNow, items, Array.Empty<PositionDto>());

        var pdf = ShippingLabelRenderer.Render(pickList);

        Assert.True(pdf.Length > 1000, "PDF ist unerwartet klein: " + pdf.Length + " Bytes");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Swagger_document_can_be_generated_for_all_endpoints()
    {
        // Swagger wird nur in Development registriert (WP01) - Development ohne Demo-Seed hochfahren.
        using var baseFactory = new LagerApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Seed"] = "false" }));
        });

        var swagger = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        Assert.Equal("Lager API", swagger.Info.Title);
        Assert.Contains("/api/auth/login", swagger.Paths.Keys);
        Assert.Contains("Bearer", swagger.Components.SecuritySchemes.Keys);
    }
}
