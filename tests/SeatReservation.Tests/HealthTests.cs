using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SeatReservation.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task DatabaseUnreachable_LivenessUp_ReadinessFailsClosed_WritesAre503()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Nothing listens on port 1: every connection attempt fails fast.
            b.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
            b.UseSetting("DB_CONNECT_TIMEOUT", "1");
            b.UseSetting("Logging:LogLevel:Default", "Critical");
        });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        var res = await client.ReserveAsync(Guid.NewGuid().ToString(), "u", ["A1"]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.Status);
        Assert.Equal("SERVICE_UNAVAILABLE", res.ErrorCode);
    }
}
