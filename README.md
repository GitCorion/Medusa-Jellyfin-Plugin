# Medusa — Plugin para Jellyfin

<p align="center"><img src="assets/medusa-wordmark.png" width="320" alt="Medusa"></p>

Plugin de Jellyfin para [TOD](https://github.com/GitCorion/TOD) (*Torrent on
Demand*). Traduce los ítems `.strm` a claves de TOD y expone en la API de
Jellyfin el estado de descarga, la preparación bajo demanda, los *leases* de
reproducción y la negociación de versiones compatibles. No descarga torrents
por su cuenta: delega el estado, la preparación y el streaming en TOD.

## El ecosistema Medusa

| Repositorio | Función |
|---|---|
| [Medusa](https://github.com/GitCorion/Medusa) | Cliente nativo para Android TV y tablet. Reproduce a través de Jellyfin. |
| **Medusa-Jellyfin-Plugin** (este repositorio) | API puente en Jellyfin (`/Medusa/Capabilities`, `/Status`, `/Prepare` y `/Lease`) y arranque rápido de `.strm`. |
| [TOD](https://github.com/GitCorion/TOD) | Biblioteca bajo demanda: releases, cola de qBittorrent, `.strm` y streaming por rangos. |

## Requisitos

- Jellyfin **12.4+** (runtime `.NET 10`)
- Servicio TOD accesible desde el servidor de Jellyfin (habitualmente `http://127.0.0.1:8773`)
- Endpoints `/status`, `/prepare`, `/lease` y `/media-info` activos en TOD

## Instalación

Añade la URL del repositorio en Jellyfin:

```text
Panel → Plugins → Repositorios → +
https://raw.githubusercontent.com/GitCorion/Medusa-Jellyfin-Plugin/main/manifest.json
```

Instala **Medusa** desde el catálogo de plugins y reinicia Jellyfin. Las
actualizaciones posteriores se gestionan con la tarea programada **Actualizar
plugins**.

Para instalación manual, detén Jellyfin, extrae la DLL del `.zip` de la release
en `<datos de Jellyfin>/plugins/Medusa_<versión>/` y vuelve a arrancar Jellyfin.

## Configuración

En **Panel → Plugins → Medusa** configura:

- **TOD proxy URL**: dirección local de TOD (por defecto `http://127.0.0.1:8773`).
- **TMDB API key**: clave API de TMDB (v3) usada por el servidor para el proxy
  `/Medusa/Tmdb/*` y por Medusa. Se guarda solo en la configuración del plugin;
  `Capabilities` únicamente publica `tmdb_configured` y nunca la clave.
- **Timeout / Prepare timeout**: tiempos máximos para consultas de estado y
  arranque en frío.
- **Native mode (experimental)**: desactivado por defecto. Permite que clientes
  de Jellyfin distintos de Medusa inicien la preparación de un `.strm` en frío
  desde `PlaybackInfo` y esperen opcionalmente hasta 30 s (`Native prebuffer`)
  antes de devolver las fuentes multimedia.

## API

Todos los endpoints se sirven bajo `/Medusa/*` en Jellyfin (con `/Catalogo/*`
mantenido como alias de compatibilidad).

### Capabilities

```http
GET /Medusa/Capabilities
```

```json
{
  "api_version": 1,
  "plugin_version": "1.2.0.0",
  "name": "Medusa",
  "features": [
    "status",
    "prepare",
    "lease",
    "media-info",
    "fast-start",
    "versions",
    "version-negotiation",
    "tmdb-proxy",
    "trakt-sync"
  ],
  "native_mode": false,
  "proxy_configured": true,
  "tmdb_configured": true
}
```

Endpoint anónimo para detectar las capacidades del plugin antes de reproducir.
`Status`, `Prepare` y `Lease` requieren autenticación estándar de Jellyfin.

### Status

```http
GET /Medusa/Status/{itemId}
GET /Medusa/Status/{itemId}?release=109&maxWidth=1920&supports4k=false&supportsDv=false
```

Lee el `.strm` del ítem, extrae la clave `key` y el `release` de TOD y devuelve
el estado en vivo junto con la negociación de versiones compatibles:

```json
{
  "is_catalog": true,
  "item_id": "35a9cbbf-c537-e8d9-2381-adddfcc9ab12",
  "key": "movie:tmdb:1447420",
  "release_id": 109,
  "status": {
    "state": "DOWNLOADING",
    "progress": 0.28,
    "speed_bps": 15133798,
    "eta_seconds": 172,
    "seeds": 1,
    "stream_ready": true
  },
  "recommended_media_source_id": "...",
  "recommended_release_id": 109,
  "versions": []
}
```

Para ítems que no pertenecen a TOD:

```json
{ "is_catalog": false, "item_id": "..." }
```

### Prepare

```http
POST /Medusa/Prepare/{itemId}
```

Inicia la preparación bajo demanda en TOD sin devolver vídeo. El cliente debe
consultar `Status` periódicamente hasta que `stream_ready` sea `true` e iniciar
entonces la reproducción normal en Jellyfin.

```json
{
  "is_catalog": true,
  "item_id": "...",
  "key": "movie:tmdb:1447420",
  "release_id": 109,
  "prepared": {
    "prepared": true,
    "status": { "...": "..." }
  }
}
```

### Lease

```http
POST /Medusa/Lease/{itemId}
DELETE /Medusa/Lease/{itemId}
```

Mantiene o libera un *lease* de reproducción activo en TOD para el ítem
indicado.

### Sync (endpoints aditivos)

Todos requieren autenticación de Jellyfin y actúan solo sobre el usuario
autenticado (claim `Jellyfin-UserId`). No alteran el flujo de reproducción.

```http
GET /Medusa/Config
GET /Medusa/Trakt/Credentials
PUT /Medusa/Trakt/Credentials
DELETE /Medusa/Trakt/Credentials?baseRevision=1
GET /Medusa/Tmdb/trending/movie/week?page=1&language=es
```

- `GET /Medusa/Config` → `{ "tmdb_configured": true, "trakt_synced": true,
  "trakt_username": "...", "trakt_revision": 1 }`. Nunca devuelve secretos ni
  la clave TMDB.
- `GET /Medusa/Trakt/Credentials` → DTO snake_case guardado (refrescando el
  access token si caduca en menos de una hora), o `{ "configured": false }`.
- `PUT /Medusa/Trakt/Credentials` → cuerpo snake_case con `access_token`,
  `refresh_token`, `expires_in`, `created_at`, `client_id`, `client_secret`,
  `username` y `base_revision`. Devuelve el DTO guardado con su `revision`; si
  `base_revision` no coincide con la revisión actual responde `409` con el
  DTO vigente. `access_token` y `client_id` son obligatorios.
- `DELETE /Medusa/Trakt/Credentials` → `{ "deleted": true }` (acepta
  `baseRevision` opcional).
- `GET /Medusa/Tmdb/{**path}` → proxy de solo lectura a
  `https://api.themoviedb.org/3/`; solo admite rutas `trending/`, `movie/`,
  `tv/` y `discover/`, y reenvía únicamente `page`, `language`, `with_genres`,
  `without_genres`, `with_keywords`, `with_watch_providers`, `watch_region`,
  `with_watch_monetization_types`, `sort_by` y `vote_count.gte`. La clave se
  inyecta en el servidor; si no está configurada responde `503` con
  `error: "tmdb_not_configured"`.

Las credenciales Trakt se guardan en
`<datos de Jellyfin>/plugins/configurations/medusa-data.json` con control de
revisión y escritura atómica.

## Flujo del cliente

1. `POST /Items/{itemId}/PlaybackInfo`.
2. Comprobar `MediaSources[].Path`.
3. Si termina en `.strm`, llamar a `GET /Medusa/Status/{itemId}`.
4. Si `is_catalog` es `true` y `stream_ready` es `false`:
   - llamar a `POST /Medusa/Prepare/{itemId}`;
   - mostrar el estado de buffering y consultar `Status` cada 1–2 segundos.
5. Cuando `stream_ready` sea `true`, reproducir usando el flujo normal de
   Jellyfin para conservar transcodificación, subtítulos y progreso visto.
6. Si `is_catalog` es `false`, reproducir normalmente.

## Compilación

```bash
dotnet restore src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj
dotnet build src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj -c Release
dotnet test tests/Jellyfin.Plugin.Catalogo.Tests/Jellyfin.Plugin.Catalogo.Tests.csproj -c Release
```

## Seguridad

- Mantener TOD escuchando en `127.0.0.1` o en una red privada LAN/WireGuard.
- `/prepare` y `/lease` en TOD están restringidos a loopback por defecto.
- `Status`, `Prepare` y `Lease` requieren autenticación en Jellyfin.
- Los endpoints de sync y el proxy TMDB también requieren autenticación; el
  proxy ignora cualquier `api_key` que envíe el cliente.
- No exponer `/play?...&download=1` de TOD directamente a Internet.

## Reversión

Si la versión `1.2.0.0` falla, consulta [`docs/ROLLBACK.md`](docs/ROLLBACK.md):
mitigación sin desinstalar, sustitución por el DLL `1.1.4.0` y `git revert`.
No hay migraciones de base de datos.

## Licencia

GPL-3.0. Consulta [LICENSE](LICENSE).
