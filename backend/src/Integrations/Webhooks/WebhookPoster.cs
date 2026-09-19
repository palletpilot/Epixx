namespace Lagerkraft.Integrations.Webhooks;

public sealed class WebhookPoster
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<(bool Ok, string? Error)> PostAsync(string url, string payload, string signature, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, signature);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                return (true, null);
            }

            return (false, $"http_{(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.GetType().Name);
        }
    }
}
