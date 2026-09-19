using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lagerkraft.Integrations.Tests;

public sealed class WebhookSink : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Url { get; }
    public ConcurrentBag<ReceivedCall> Calls { get; } = [];
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    private WebhookSink(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static async Task<WebhookSink> StartAsync()
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var sink = new WebhookSink(app, $"http://127.0.0.1:{port}/hook");
        app.MapPost("/hook", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            sink.Calls.Add(new ReceivedCall(
                body,
                ctx.Request.Headers["X-Lagerkraft-Signature"].ToString()));
            return Results.StatusCode(sink.StatusCode);
        });
        await app.StartAsync();
        return sink;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed record ReceivedCall(string Body, string Signature);
