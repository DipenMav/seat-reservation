using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;
using SeatReservation.Api.Features.Reservations.Cancel;
using SeatReservation.Api.Features.Reservations.GetReservation;
using SeatReservation.Api.Features.Reservations.Reserve;
using SeatReservation.Api.Features.Shows.CreateShow;
using SeatReservation.Api.Features.Shows.GetShow;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Health;
using SeatReservation.Api.Infrastructure.Observability;
using SeatReservation.Api.Infrastructure.OpenApi;
using SeatReservation.Api.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Platforms like Render/Railway/Fly inject PORT.
if (builder.Configuration["PORT"] is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---- Logging: one flat JSON object per line ----
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.FormatterName = FlatJsonConsoleFormatter.Name;
    o.MaxQueueLength = 20_000;
});
builder.Logging.AddConsoleFormatter<FlatJsonConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

// ---- Persistence ----
var connectionString = DatabaseConfig.Resolve(builder.Configuration);
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(OpsDataSource.Create(connectionString));
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(dataSource)
    .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton<DatabaseState>();
builder.Services.AddHostedService<DatabaseMigrator>();

// ---- Features ----
builder.Services.AddScoped<ReserveHandler>();
builder.Services.AddScoped<CancelHandler>();
builder.Services.AddSingleton<SeatStateCollector>();

// ---- Auth: identity comes only from the bearer token ----
builder.Services
    .AddAuthentication(BearerTokenHandler.SchemeName)
    .AddScheme<BearerTokenOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, o =>
        o.AdminToken = builder.Configuration["ADMIN_TOKEN"] is { Length: > 0 } t ? t : "admin");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Admin, p => p.RequireRole(BearerTokenHandler.AdminRole));

// ---- JSON: snake_case, integers must be integers (no "25000" strings, no 250.5 paise) ----
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddApiDocs();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

ReservationMetrics.Initialize();
app.Services.GetRequiredService<SeatStateCollector>().Register(Metrics.DefaultRegistry);

app.UseMiddleware<RequestPipelineMiddleware>();
app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());
app.UseAuthentication();
app.UseAuthorization();

// Swagger UI at /swagger, OpenAPI document at /openapi/v1.json.
app.UseApiDocs();

app.MapGet("/", () => Results.Ok(new
{
    service = "seat-reservation",
    docs = "/swagger",
    endpoints = new[]
    {
        "POST /shows (admin)", "GET /shows/{id}", "POST /shows/{id}/reserve",
        "POST /reservations/{id}/cancel", "GET /reservations/{id}",
        "GET /health/live", "GET /health/ready", "GET /metrics",
    },
})).ExcludeFromDescription();

// Liveness: the process is up. Deliberately does not touch the database.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthAsync,
});

// Readiness: migrated and Postgres reachable right now; 503 otherwise (fail closed).
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = WriteHealthAsync,
});

app.MapMetrics("/metrics");

// The assignment's paths at the root, and the same routes under /api (docs/API.md).
// The /api copies are left out of the OpenAPI document so Swagger lists each operation once.
foreach (var routes in new IEndpointRouteBuilder[] { app, app.MapGroup("/api").ExcludeFromDescription() })
{
    CreateShowEndpoint.Map(routes);
    GetShowEndpoint.Map(routes);
    ReserveEndpoint.Map(routes);
    CancelEndpoint.Map(routes);
    GetReservationEndpoint.Map(routes);
}

app.Run();

static Task WriteHealthAsync(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json";
    return ctx.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(e => e.Key, e => new
        {
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
        }),
    });
}

public partial class Program;
