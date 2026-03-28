using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TransactionDispatch.Api.Middleware;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Tests;

public sealed class ApiKeyMiddlewareTests
{
    private static ApiKeyMiddleware CreateMiddleware(string apiKey, RequestDelegate next) =>
        new(next, Options.Create(new SecurityOptions { ApiKey = apiKey }));

    private static DefaultHttpContext CreateContext(string path, string? apiKeyHeaderValue = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (apiKeyHeaderValue is not null)
            context.Request.Headers["X-Api-Key"] = apiKeyHeaderValue;
        return context;
    }

    [Fact]
    public async Task NoApiKeyConfigured_AnyRequest_PassesThrough()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(string.Empty, _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/dispatch-transactions"));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task CorrectApiKey_PassesThrough()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware("secret-key", _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/dispatch-transactions", "secret-key"));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task MissingApiKeyHeader_Returns401()
    {
        var middleware = CreateMiddleware("secret-key", _ => Task.CompletedTask);
        var context = CreateContext("/dispatch-transactions");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WrongApiKey_Returns401()
    {
        var middleware = CreateMiddleware("secret-key", _ => Task.CompletedTask);
        var context = CreateContext("/dispatch-transactions", "wrong-key");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WrongApiKey_ResponseBodyContainsErrorMessage()
    {
        var middleware = CreateMiddleware("secret-key", _ => Task.CompletedTask);
        var context = CreateContext("/dispatch-transactions", "wrong-key");

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Unauthorized", body);
    }

    [Fact]
    public async Task ExemptPath_Health_PassesThroughWithoutApiKey()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware("secret-key", _ => { nextCalled = true; return Task.CompletedTask; });

        // /health is exempt — no header needed.
        await middleware.InvokeAsync(CreateContext("/health"));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ExemptPath_HealthSubPath_PassesThroughWithoutApiKey()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware("secret-key", _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/health/live"));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ApiKeyIsCaseSensitive_WrongCase_Returns401()
    {
        var middleware = CreateMiddleware("Secret-Key", _ => Task.CompletedTask);
        var context = CreateContext("/dispatch-transactions", "secret-key");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhitespaceApiKeyConfigured_TreatedAsDisabled_PassesThrough()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware("   ", _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/dispatch-transactions"));

        Assert.True(nextCalled);
    }
}
