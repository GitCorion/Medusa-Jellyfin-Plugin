using System.Text.Json;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Proxy de solo lectura hacia la API v3 de TMDB. La clave del servidor se
/// inyecta aquí y nunca se devuelve a los clientes.
/// </summary>
public sealed class TmdbProxyClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";

    // Solo rutas de lectura de catálogo; nunca se reenvía una ruta arbitraria.
    private static readonly string[] AllowedPathPrefixes =
    {
        "trending/",
        "movie/",
        "tv/",
        "discover/"
    };

    // Parámetros permitidos por el contrato del proxy. Cualquier otro se ignora
    // (en especial api_key, que el cliente podría intentar colar).
    private static readonly string[] AllowedQueryKeys =
    {
        "page",
        "language",
        "with_genres",
        "without_genres",
        "with_keywords",
        "with_watch_providers",
        "watch_region",
        "with_watch_monetization_types",
        "sort_by",
        "vote_count.gte"
    };

    private readonly HttpClient _http;
    private readonly Func<string?>? _apiKeyProvider;
    private readonly Func<int>? _timeoutProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbProxyClient"/> class.
    /// Los proveedores opcionales permiten probar el cliente sin depender del
    /// singleton <see cref="Plugin"/>.
    /// </summary>
    public TmdbProxyClient(
        HttpClient http,
        Func<string?>? apiKeyProvider = null,
        Func<int>? timeoutProvider = null)
    {
        _http = http;
        _apiKeyProvider = apiKeyProvider;
        _timeoutProvider = timeoutProvider;
    }

    /// <summary>
    /// Realiza un GET contra TMDB y devuelve el estado y el cuerpo sin tocar.
    /// </summary>
    public async Task<(int Status, string Body)> GetAsync(
        string path,
        IReadOnlyDictionary<string, string?> query,
        CancellationToken cancellationToken)
    {
        if (!IsPathAllowed(path))
        {
            return (400, ErrorJson("invalid_path", "Ruta TMDB no permitida."));
        }

        var apiKey = _apiKeyProvider?.Invoke()
            ?? Plugin.Instance?.Configuration.TmdbApiKey
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (503, ErrorJson("tmdb_not_configured", "Falta la clave API de TMDB."));
        }

        var url = BaseUrl + path.TrimStart('/') + BuildQuery(query, apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seconds = _timeoutProvider?.Invoke()
            ?? Plugin.Instance?.Configuration.TimeoutSeconds
            ?? 15;
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, seconds)));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (502, ErrorJson("tmdb_unavailable", ex.Message));
        }
    }

    private static bool IsPathAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var candidate = path.TrimStart('/');
        if (candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('?')
            || candidate.Contains('#'))
        {
            return false;
        }

        return AllowedPathPrefixes.Any(prefix =>
            candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildQuery(
        IReadOnlyDictionary<string, string?> query,
        string apiKey)
    {
        var parts = new List<string> { "api_key=" + Uri.EscapeDataString(apiKey.Trim()) };
        foreach (var key in AllowedQueryKeys)
        {
            // OrdinalIgnoreCase: elCliente puede enviar "Page" o "with_genres".
            var match = query.FirstOrDefault(pair =>
                string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null || string.IsNullOrWhiteSpace(match.Value))
            {
                continue;
            }

            parts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(match.Value!));
        }

        return "?" + string.Join('&', parts);
    }

    private static string ErrorJson(string error, string message)
        => JsonSerializer.Serialize(new { error, message });
}
