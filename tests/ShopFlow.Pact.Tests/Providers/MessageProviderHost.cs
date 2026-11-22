using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ShopFlow.Pact.Tests.Providers;

internal sealed record ProvidedMessage(object Content, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// A minimal message provider for the Pact verifier: for every request with an interaction description it returns a
/// message built by the real provider code.
/// </summary>
internal sealed class MessageProviderHost : IAsyncDisposable
{
    private const string MetadataHeader = "Pact-Message-Metadata";

    private readonly WebApplication _app;

    private MessageProviderHost(WebApplication app, Uri uri)
    {
        _app = app;
        Uri = uri;
    }

    public Uri Uri { get; }

    public static async Task<MessageProviderHost> StartAsync(IReadOnlyDictionary<string, Func<ProvidedMessage>> scenarios, JsonSerializerOptions contentOptions)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapPost("/pact-messages", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<MessageRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
            if (request?.Description is not { } description || !scenarios.TryGetValue(description, out var scenario))
            {
                return Results.NotFound($"No message scenario named '{request?.Description}'.");
            }

            var message = scenario();
            context.Response.Headers[MetadataHeader] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(message.Metadata));
            return Results.Text(JsonSerializer.Serialize(message.Content, message.Content.GetType(), contentOptions), "application/json");
        });

        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        return new MessageProviderHost(app, new Uri(address, "/pact-messages"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record MessageRequest(string? Description);
}
