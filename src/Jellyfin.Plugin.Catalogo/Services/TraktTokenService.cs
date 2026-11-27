using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Refresco best-effort del token Trakt. Si Trakt no responde, se conservan las
/// credenciales guardadas: el sync es aditivo y nunca debe romper la
/// reproducción.
/// </summary>
public sealed class TraktTokenService
{
    private const string TokenUrl = "https://api.trakt.tv/oauth/token";

    private readonly HttpClient _http;
    private readonly MedusaDataStore _store;
    private readonly ILogger<TraktTokenService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktTokenService"/> class.
    /// </summary>
    public TraktTokenService(
        HttpClient http,
        MedusaDataStore store,
        ILogger<TraktTokenService> logger)
    {
        _http = http;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve credenciales vigentes, refrescando el access token si está a
    /// menos de una hora de caducar (o ya caducó).
    /// </summary>
    public async Task<TraktCredentials> EnsureFreshAsync(
        string userId,
        TraktCredentials creds,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(creds.RefreshToken)
            || string.IsNullOrWhiteSpace(creds.ClientId)
            || creds.ExpiresIn <= 0
            || creds.CreatedAt <= 0
            || now < creds.CreatedAt + creds.ExpiresIn - 3600)
        {
            return creds;
        }

        try
        {
            var payload = new
            {
                client_id = creds.ClientId,
                client_secret = creds.ClientSecret,
                refresh_token = creds.RefreshToken,
                grant_type = "refresh_token"
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            request.Headers.TryAddWithoutValidation("trakt-api-key", creds.ClientId);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Trakt token refresh failed with HTTP {Status}",
                    (int)response.StatusCode);
                return creds.Clone();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var updated = creds.Clone();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return creds.Clone();
            }

            ReadString(root, "access_token", value => updated.AccessToken = value);
            ReadString(root, "refresh_token", value => updated.RefreshToken = value);
            ReadString(root, "username", value => updated.Username = value);
            if (root.TryGetProperty("expires_in", out var expires)
                && expires.ValueKind == JsonValueKind.Number)
            {
                updated.ExpiresIn = expires.GetInt64();
            }
            updated.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var (stored, conflict) = await _store.UpsertAsync(
                userId, updated, updated.Revision);
            if (conflict)
            {
                _logger.LogWarning(
                    "Trakt token refresh lost a revision race for user {UserId}",
                    userId);
            }

            return stored;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException)
        {
            _logger.LogWarning(ex, "Trakt token refresh failed; keeping current credentials");
            return creds.Clone();
        }
    }

    private static void ReadString(
        JsonElement root,
        string name,
        Action<string> assign)
    {
        if (root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            assign(value.GetString() ?? string.Empty);
        }
    }
}
