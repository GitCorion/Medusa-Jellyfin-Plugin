using System.Net;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Error talking to the Catalogo proxy.
/// </summary>
public class CatalogoProxyException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoProxyException"/> class.
    /// </summary>
    public CatalogoProxyException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the HTTP status code, if any.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Small HTTP client for the local Catalogo proxy.
/// </summary>
public class CatalogoProxyClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoProxyClient"/> class.
    /// </summary>
    public CatalogoProxyClient(HttpClient http)
    {
        _http = http;
    }

    private static string BaseUrl =>
        (Plugin.Instance?.Configuration.ProxyUrl ?? "http://127.0.0.1:8773")
        .TrimEnd('/');

    private static int TimeoutSeconds =>
        Math.Max(2, Plugin.Instance?.Configuration.TimeoutSeconds ?? 15);

    /// <summary>
    /// Gets the status for a catalog key.
    /// </summary>
    public Task<JsonElement> GetStatusAsync(
        string key,
        int? releaseId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/status?key={Uri.EscapeDataString(key)}";
        if (releaseId is not null)
        {
            url += $"&release={releaseId.Value}";
        }

        return SendAsync(HttpMethod.Get, url, cancellationToken);
    }

    /// <summary>
    /// Starts a download without streaming it.
    /// </summary>
    public Task<JsonElement> PrepareAsync(
        string key,
        int? releaseId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/prepare?key={Uri.EscapeDataString(key)}";
        if (releaseId is not null)
        {
            url += $"&release={releaseId.Value}";
        }

        return SendAsync(HttpMethod.Post, url, cancellationToken);
    }

    /// <summary>
    /// Renews the playback lease for a catalog key.
    /// </summary>
    public Task<JsonElement> LeaseAsync(
        string key,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lease?key={Uri.EscapeDataString(key)}"
            + $"&session={Uri.EscapeDataString(sessionId)}";
        return SendAsync(HttpMethod.Post, url, cancellationToken);
    }

    /// <summary>
    /// Releases the playback lease when playback stops.
    /// </summary>
    public async Task ReleaseLeaseAsync(
        string key,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lease?key={Uri.EscapeDataString(key)}"
            + $"&session={Uri.EscapeDataString(sessionId)}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        // Un 404/200 da igual: el lease caduca solo si no se renueva.
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd("application/json");
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent(
                string.Empty, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogoProxyException("Catalogo proxy timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogoProxyException(
                $"Catalogo proxy unavailable: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new CatalogoProxyException(
                    $"Catalogo proxy HTTP {(int)response.StatusCode}: {body}",
                    response.StatusCode);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new CatalogoProxyException(
                    $"Catalogo proxy invalid JSON: {ex.Message}");
            }
        }
    }
}
