using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TransactionDispatch.Infrastructure.Extensions;
using TransactionDispatch.Api.Extensions;
using TransactionDispatch.Api.HealthChecks;
using TransactionDispatch.Api.Middleware;
using TransactionDispatch.Application.Options;

var builder = WebApplication.CreateBuilder(args);

var securityOptions = builder.Configuration
    .GetSection(SecurityOptions.SectionName)
    .Get<SecurityOptions>() ?? new SecurityOptions();

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", failureStatus: HealthStatus.Unhealthy)
    .AddCheck<KafkaHealthCheck>("kafka", failureStatus: HealthStatus.Unhealthy);
builder.Services.AddTransactionDispatch(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS — origins are driven by Security:AllowedOrigins in config.
if (securityOptions.AllowedOrigins.Count > 0)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins([.. securityOptions.AllowedOrigins])
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
    });
}

// Rate limiting — applied globally to all controller endpoints via MapControllers().
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("global", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (securityOptions.AllowedOrigins.Count > 0)
    app.UseCors();

app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();

await app.InitialiseInfrastructureAsync();

app.UseAuthorization();
app.MapControllers().RequireRateLimiting("global");
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (httpContext, report) =>
    {
        bool isLocal = httpContext.Connection.RemoteIpAddress is { } ip &&
                       (ip.Equals(System.Net.IPAddress.Loopback) ||
                        ip.Equals(System.Net.IPAddress.IPv6Loopback) ||
                        ip.ToString() == "::1");
        bool isDevelopment = httpContext.RequestServices
            .GetRequiredService<IHostEnvironment>().IsDevelopment();

        httpContext.Response.ContentType = "application/json";
        var response = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                // Only surface exception details to local callers or in development.
                error = (isLocal || isDevelopment) ? e.Value.Exception?.Message : null
            })
        };
        await httpContext.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
});

app.Run();

