using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Credenciales Trakt sincronizadas por un usuario de Jellyfin. Los secretos
/// nunca salen de este almacén local salvo en el endpoint autenticado del
/// propio usuario.
/// </summary>
public sealed class TraktCredentials
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public long ExpiresIn { get; set; }

    public long CreatedAt { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optimistic-concurrency revision. El store lo incrementa
    /// en cada upsert; 0 significa «sin credenciales guardadas».
    /// </summary>
    public int Revision { get; set; }

    public string UpdatedAtUtc { get; set; } = string.Empty;

    /// <summary>
    /// Crea una copia independiente para no exponer referencias mutables.
    /// </summary>
    public TraktCredentials Clone() => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        ExpiresIn = ExpiresIn,
        CreatedAt = CreatedAt,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Username = Username,
        Revision = Revision,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

/// <summary>
/// Almacén JSON por usuario para los datos aditivos de Medusa (por ahora,
/// credenciales Trakt). Acceso serializado y escritura atómica.
/// </summary>
public sealed class MedusaDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataFilePath;
    private readonly ILogger<MedusaDataStore>? _logger;
    private Dictionary<string, TraktCredentials>? _traktByUser;

    /// <summary>
    /// Initializes a new instance of the <see cref="MedusaDataStore"/> class.
    /// </summary>
    /// <param name="dataFilePath">Ruta opcional para pruebas.</param>
    /// <param name="logger">Logger opcional.</param>
    public MedusaDataStore(
        string? dataFilePath = null,
        ILogger<MedusaDataStore>? logger = null)
    {
        _dataFilePath = !string.IsNullOrWhiteSpace(dataFilePath)
            ? dataFilePath
            : Path.Combine(
                Plugin.Instance!.PluginConfigurationsDirectory,
                "medusa-data.json");
        _logger = logger;
    }

    /// <summary>
    /// Obtiene las credenciales guardadas para un usuario, o null.
    /// </summary>
    public async Task<TraktCredentials?> GetAsync(string userId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = await LoadAsync().ConfigureAwait(false);
            return data.TryGetValue(userId, out var credentials)
                ? credentials.Clone()
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Inserta o actualiza las credenciales de un usuario. Si se indica una
    /// revisión base y no coincide con la actual, no se escribe nada.
    /// </summary>
    public async Task<(TraktCredentials Credentials, bool Conflict)> UpsertAsync(
        string userId,
        TraktCredentials input,
        int? baseRevision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = await LoadAsync().ConfigureAwait(false);
            var currentRevision = data.TryGetValue(userId, out var current)
                ? current.Revision
                : 0;
            if (baseRevision.HasValue && baseRevision.Value != currentRevision)
            {
                return (current?.Clone() ?? new TraktCredentials(), true);
            }

            var stored = input.Clone();
            stored.Revision = currentRevision + 1;
            stored.UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            data[userId] = stored;
            await SaveAsync(data).ConfigureAwait(false);
            return (stored.Clone(), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Borra las credenciales de un usuario, con control de revisión opcional.
    /// </summary>
    public async Task<bool> DeleteAsync(string userId, int? baseRevision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = await LoadAsync().ConfigureAwait(false);
            if (!data.TryGetValue(userId, out var current)
                || (baseRevision.HasValue && baseRevision.Value != current.Revision))
            {
                return false;
            }

            data.Remove(userId);
            await SaveAsync(data).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Carga diferida. Un fichero ausente o corrupto se trata como vacío: es
    /// datos aditivos, no deben impedir la reproducción.
    /// </summary>
    private async Task<Dictionary<string, TraktCredentials>> LoadAsync()
    {
        if (_traktByUser is not null)
        {
            return _traktByUser;
        }

        var empty = new Dictionary<string, TraktCredentials>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = await File.ReadAllTextAsync(_dataFilePath).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions);
                if (document?.TraktByUser is not null)
                {
                    foreach (var (userId, credentials) in document.TraktByUser)
                    {
                        empty[userId] = credentials ?? new TraktCredentials();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Medusa data file could not be read; starting empty: {Path}", _dataFilePath);
        }

        _traktByUser = empty;
        return empty;
    }

    private async Task SaveAsync(Dictionary<string, TraktCredentials> data)
    {
        var directory = Path.GetDirectoryName(_dataFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _dataFilePath + ".tmp";
        var json = JsonSerializer.Serialize(
            new StoreDocument { TraktByUser = data }, JsonOptions);
        await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
        File.Move(temporary, _dataFilePath, overwrite: true);
    }

    private sealed class StoreDocument
    {
        public Dictionary<string, TraktCredentials>? TraktByUser { get; set; }
    }
}
