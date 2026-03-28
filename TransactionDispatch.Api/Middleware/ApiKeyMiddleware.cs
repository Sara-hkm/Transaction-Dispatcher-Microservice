using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Api.Middleware;

/// <summary>
/// Enforces API key authentication by inspecting the <c>X-Api-Key</c> request header.
/// Exempt paths (e.g. /health) bypass the check so infrastructure probes still work.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<SecurityOptions> securityOptions)
{
    private const string ApiKeyHeader = "X-Api-Key";
    private static readonly string[] ExemptPaths = ["/health"];

    public async Task InvokeAsync(HttpContext context)
    {
        var expectedKey = securityOptions.Value.ApiKey;

        // If no key is configured, enforcement is disabled (useful in development).
        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            bool isExempt = ExemptPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isExempt)
            {
                if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
                    !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Unauthorized. A valid X-Api-Key header is required.\"}");
                    return;
                }
            }
        }

        await next(context);
    }
}
