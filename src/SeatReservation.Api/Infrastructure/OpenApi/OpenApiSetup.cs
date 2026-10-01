using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SeatReservation.Api.Infrastructure.OpenApi;

/// <summary>
/// OpenAPI document at /openapi/v1.json and Swagger UI at /swagger (enabled in every environment so
/// reviewers can poke the live deployment). Click "Authorize" and enter a token such as user-123 or
/// the ADMIN_TOKEN — the UI adds the "Bearer " prefix.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/openapi/v1.json";

    public static IServiceCollection AddApiDocs(this IServiceCollection services) =>
        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo
            {
                Title = "Seat Reservation API",
                Version = "v1",
                Description = """
                    Sells assigned seats under heavy contention: no seat is ever sold twice, the per-user limit holds
                    under concurrency, and a retried request (same idempotency key) never books twice.

                    **Auth:** the bearer token *is* the identity. Click **Authorize** and enter `user-123` to act as
                    user `user-123`, or the admin token (default `admin`) to create shows. Any `user_id` in a request
                    body is ignored.

                    **Money** is always integer paise (`25000` = ₹250.00).

                    **Errors** are `{"error":{"code","message","correlation_id"}}`. Contention outcomes are 409s,
                    never 5xx.
                    """,
            };

            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Any user id (e.g. `user-123`) acts as that user; the ADMIN_TOKEN acts as admin.",
            };
            doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] }];
            return Task.CompletedTask;
        }));

    public static WebApplication UseApiDocs(this WebApplication app)
    {
        app.MapOpenApi(DocumentPath).ExcludeFromDescription();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint(DocumentPath, "Seat Reservation API v1");
            o.RoutePrefix = "swagger";
            o.DocumentTitle = "Seat Reservation API";
            o.EnablePersistAuthorization();
            o.DisplayRequestDuration();
        });
        return app;
    }

    /// <summary>Attach a JSON request-body example to an endpoint's operation.</summary>
    public static TBuilder WithRequestExample<TBuilder>(this TBuilder builder, string json) where TBuilder : IEndpointConventionBuilder =>
        builder.AddOpenApiOperationTransformer((op, _, _) =>
        {
            if (op.RequestBody?.Content?.TryGetValue("application/json", out var media) == true)
                media.Example = JsonNode.Parse(json);
            return Task.CompletedTask;
        });
}
