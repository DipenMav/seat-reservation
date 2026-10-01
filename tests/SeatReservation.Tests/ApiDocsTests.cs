using System.Net;
using System.Text.Json.Nodes;

namespace SeatReservation.Tests;

[Collection(ApiCollection.Name)]
public sealed class ApiDocsTests(ApiFixture fx)
{
    private readonly HttpClient _client = fx.Factory.CreateClient();

    [Fact]
    public async Task OpenApiDocument_DescribesEndpointsOnce_WithSnakeCaseAndBearerAuth()
    {
        var doc = JsonNode.Parse(await _client.GetStringAsync("/openapi/v1.json"))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();

        Assert.Contains("/shows", paths);
        Assert.Contains("/shows/{showId}", paths);
        Assert.Contains("/shows/{showId}/reserve", paths);
        Assert.Contains("/reservations/{reservationId}/cancel", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/"));

        Assert.Equal("bearer", doc["components"]!["securitySchemes"]!["Bearer"]!["scheme"]!.GetValue<string>());

        var reservation = doc["components"]!["schemas"]!["ReservationDto"]!["properties"]!.AsObject();
        Assert.Contains("reservation_id", reservation.Select(p => p.Key));
        Assert.Contains("amount_paise", reservation.Select(p => p.Key));

        var reserveParams = doc["paths"]!["/shows/{showId}/reserve"]!["post"]!["parameters"]!.AsArray();
        Assert.Contains(reserveParams, p => p!["name"]!.GetValue<string>() == "Idempotency-Key");
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        var res = await _client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("swagger-ui", await res.Content.ReadAsStringAsync());
    }
}
