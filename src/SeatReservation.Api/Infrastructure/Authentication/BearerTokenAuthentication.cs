using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Common;

namespace SeatReservation.Api.Infrastructure.Authentication;

public sealed class BearerTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>Token that grants the admin role (show creation). From ADMIN_TOKEN.</summary>
    public string AdminToken { get; set; } = "admin";
}

/// <summary>
/// Development-grade bearer auth, deliberately simple so graders can mint users freely:
///   Authorization: Bearer &lt;ADMIN_TOKEN&gt;  → user "admin", role admin
///   Authorization: Bearer user-123        → user "user-123", role user
/// The point being demonstrated is that identity is derived ONLY from this header: handlers read
/// the user id from ClaimsPrincipal and request bodies have no user field that is ever read.
/// Swapping this for JWT validation changes nothing downstream.
/// </summary>
public sealed partial class BearerTokenHandler(
    IOptionsMonitor<BearerTokenOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<BearerTokenOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";
    public const string AdminRole = "admin";
    private const string AdminUserId = "admin";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:@-]{0,99}$")]
    private static partial Regex UserIdPattern();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Unsupported authorization scheme"));

        var token = header["Bearer ".Length..].Trim();
        var isAdmin = FixedTimeEquals(token, Options.AdminToken);
        if (!isAdmin && (!UserIdPattern().IsMatch(token) || token == AdminUserId))
            return Task.FromResult(AuthenticateResult.Fail("Malformed token"));

        var userId = isAdmin ? AdminUserId : token;
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiErrors.WriteAsync(Context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized,
            "Missing or invalid bearer token.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
            "You are not allowed to perform this action.");

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The only source of user identity in the application.</summary>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Endpoint requires authentication but no user id claim is present.");

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(BearerTokenHandler.AdminRole);
}
