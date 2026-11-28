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
    public CatalogoProxyException(
        string message,
        HttpStatusCode? statusCode = null,
        string? body = null)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>
    /// Gets the HTTP status code, if any.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Gets the proxy response body, if any.
    /// </summary>
    public string? Body { get; }
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

    private static int PrepareTimeoutSeconds =>
        Math.Max(5, Plugin.Instance?.Configuration.PrepareTimeoutSeconds ?? 40);

    /// <summary>
    /// Gets the status for a catalog key.
    /// </summary>
    public Task<JsonElement> GetStatusAsync(
        string key,
        int? releaseId,
        CancellationToken cancellationToken)
        => GetStatusAsync(key, releaseId, null, null, null, cancellationToken);

    public Task<JsonElement> GetStatusAsync(
        string key,
        int? releaseId,
        int? maxWidth,
        bool? supports4k,
        bool? supportsDv,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/status?key={Uri.EscapeDataString(key)}";
        if (releaseId is not null)
        {
            url += $"&release={releaseId.Value}";
        }
        url = AppendCapabilities(url, maxWidth, supports4k, supportsDv);

        return SendAsync(HttpMethod.Get, url, cancellationToken);
    }

    /// <summary>
    /// Gets the status for a catalog key, falling back to the unpinned
    /// active/recommended status when a pinned release is unavailable or
    /// switched away.
    /// </summary>
    public async Task<(JsonElement Status, int? EffectiveReleaseId, bool UsedFallback)> GetEffectiveStatusAsync(
        string key,
        int? releaseId,
        int? maxWidth,
        bool? supports4k,
        bool? supportsDv,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(
                key, releaseId, maxWidth, supports4k, supportsDv, cancellationToken)
            .ConfigureAwait(false);
        var effectiveRelease = ReadInt32(status, "release_id") ?? releaseId;
        if (releaseId is null || !ShouldFallbackFromPinnedStatus(status))
        {
            return (status, effectiveRelease, false);
        }

        try
        {
            var fallback = await GetStatusAsync(
                    key, null, maxWidth, supports4k, supportsDv, cancellationToken)
                .ConfigureAwait(false);
            if (IsPlayableOrActiveStatus(fallback))
            {
                var fallbackRelease = ReadInt32(fallback, "release_id")
                    ?? ReadInt32(fallback, "recommended_release_id");
                return (fallback, fallbackRelease, true);
            }
        }
        catch (CatalogoProxyException)
        {
            // Keep the original pinned status when unpinned lookup fails.
        }

        return (status, effectiveRelease, false);
    }

    /// <summary>
    /// Returns whether a status response for a pinned release indicates that
    /// the release is dead, failed, or was switched away by TOD.
    /// </summary>
    public static bool ShouldFallbackFromPinnedStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (status.TryGetProperty("stream_ready", out var readyProp)
            && readyProp.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        var state = ReadString(status, "state");
        if (string.Equals(state, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var source = ReadString(status, "source");
        if (string.Equals(source, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var playbackState = ReadString(status, "playback_state");
        if (string.Equals(playbackState, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var reason = ReadString(status, "reason");
        if (string.Equals(reason, "stream_bad_swarm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "release_not_found", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "tracker_blocked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (status.TryGetProperty("release_id", out var relProp)
            && relProp.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var switchReason = ReadString(status, "stream_switch_reason");
        if (!string.IsNullOrWhiteSpace(switchReason))
        {
            var progress = ReadDouble(status, "progress") ?? 0.0;
            var speedBps = ReadInt64(status, "speed_bps") ?? 0;
            if (progress <= 0.0001 && speedBps <= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlayableOrActiveStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (status.TryGetProperty("stream_ready", out var readyProp)
            && readyProp.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var state = ReadString(status, "state");
        var source = ReadString(status, "source");
        return !string.Equals(state, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, "unavailable", StringComparison.OrdinalIgnoreCase);
    }

    internal static int? ReadInt32(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private static long? ReadInt64(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Gets the playback metadata (codecs, resolution, audio) for a catalog key.
    /// </summary>
    public Task<JsonElement> GetMediaInfoAsync(
        string key,
        int? releaseId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/media-info?key={Uri.EscapeDataString(key)}";
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
        int? runtimeSeconds,
        CancellationToken cancellationToken)
        => PrepareAsync(key, releaseId, runtimeSeconds, null, null, null, cancellationToken);

    public Task<JsonElement> PrepareAsync(
        string key,
        int? releaseId,
        int? runtimeSeconds,
        int? maxWidth,
        bool? supports4k,
        bool? supportsDv,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/prepare?key={Uri.EscapeDataString(key)}";
        if (releaseId is not null)
        {
            url += $"&release={releaseId.Value}";
        }
        if (runtimeSeconds is not null)
        {
            url += $"&runtime={runtimeSeconds.Value}";
        }
        url = AppendCapabilities(url, maxWidth, supports4k, supportsDv);

        return SendAsync(
            HttpMethod.Post, url, cancellationToken, PrepareTimeoutSeconds);
    }

    private static string AppendCapabilities(
        string url,
        int? maxWidth,
        bool? supports4k,
        bool? supportsDv)
    {
        if (maxWidth is > 0)
        {
            url += $"&max_width={maxWidth.Value}";
        }
        if (supports4k is not null)
        {
            url += $"&supports_4k={(supports4k.Value ? 1 : 0)}";
        }
        if (supportsDv is not null)
        {
            url += $"&supports_dv={(supportsDv.Value ? 1 : 0)}";
        }
        return url;
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
        CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(timeoutSeconds ?? TimeoutSeconds));

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
                    response.StatusCode,
                    body);
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
