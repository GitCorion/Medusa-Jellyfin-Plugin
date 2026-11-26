# Medusa — Jellyfin companion plugin

<p align="center"><img src="assets/medusa-wordmark.png" width="320" alt="Medusa"></p>

Jellyfin companion plugin for [TOD](https://github.com/GitCorion/TOD) (Torrent on
Demand). It translates `.strm` items into TOD keys and exposes playback status,
on-demand preparation, playback leases, and fast-start media metadata through
Jellyfin's API.

> **Compatibility:** Preserves the plugin GUID (`50c67d89-41de-4a95-84e2-4511775d4965`)
> and configuration. Medusa detects the plugin via `GET /Medusa/Capabilities`.

The plugin does **not** download torrents itself; it delegates status,
preparation, and streaming to the local TOD service.

## The Medusa stack

| Repository | Role |
|---|---|
| [Medusa](https://github.com/GitCorion/Medusa) | Native Android TV client. Plays through Jellyfin. |
| **Medusa-Jellyfin-Plugin** (this repository) | Jellyfin companion API. Exposes `/Medusa/Capabilities`, `/Status`, `/Prepare`, and `/Lease`, and enables fast `.strm` startup. |
| [TOD](https://github.com/GitCorion/TOD) | Library, releases, qBittorrent queue, `.strm` generation, and range streaming. |

Medusa communicates exclusively with Jellyfin; this plugin bridges that
conversation to TOD so clients can display buffering progress, trigger cold
starts, and follow source switches.

## Requirements

- Jellyfin **12.4+** (`.NET 10` runtime)
- TOD reachable from the Jellyfin server (typically `http://127.0.0.1:8773`)
- TOD endpoints `/status`, `/prepare`, `/lease`, and `/media-info` enabled

## Install

Add this repository URL in Jellyfin:

```text
Dashboard → Plugins → Repositories → +
https://raw.githubusercontent.com/GitCorion/Medusa-Jellyfin-Plugin/main/manifest.json
```

Install **Medusa** from the plugin catalog and restart Jellyfin. Future updates
are handled by Jellyfin's **Update Plugins** scheduled task.

For manual installation, stop Jellyfin, extract the DLL from the release zip
into `<Jellyfin data>/plugins/Medusa_<version>/`, and start Jellyfin again.

## Configure

Open **Dashboard → Plugins → Medusa** and set:

- **TOD proxy URL**: local TOD address (default `http://127.0.0.1:8773`).
- **Timeout / Prepare timeout**: request timeouts for status and cold-start
  preparation.
- **Native mode (experimental)**: disabled by default. Allows non-Medusa
  Jellyfin clients to trigger cold `.strm` preparation from `PlaybackInfo` and
  optionally wait up to 30 s (`Native prebuffer`) before returning media
  sources.

## API

All endpoints are served under `/Medusa/*` by Jellyfin (with `/Catalogo/*`
retained as a legacy alias).

### Capabilities

```http
GET /Medusa/Capabilities
```

```json
{
  "api_version": 1,
  "plugin_version": "1.1.4.0",
  "name": "Medusa",
  "features": [
    "status",
    "prepare",
    "lease",
    "media-info",
    "fast-start",
    "versions",
    "version-negotiation"
  ],
  "native_mode": false,
  "proxy_configured": true
}
```

Anonymous endpoint used for client capability detection before playback.
`Status`, `Prepare`, and `Lease` require standard Jellyfin authentication.

### Status

```http
GET /Medusa/Status/{itemId}
GET /Medusa/Status/{itemId}?release=109&maxWidth=1920&supports4k=false&supportsDv=false
```

Reads the item's `.strm`, extracts the TOD `key`/`release`, and returns the
live TOD status along with compatible version negotiation:

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

For non-TOD items:

```json
{ "is_catalog": false, "item_id": "..." }
```

### Prepare

```http
POST /Medusa/Prepare/{itemId}
```

Starts on-demand preparation in TOD without streaming video. Clients should poll
`Status` until `stream_ready` is `true`, then start normal Jellyfin playback.

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

Maintains or releases an active playback lease in TOD for the given item.

## Client flow

1. `POST /Items/{itemId}/PlaybackInfo`.
2. Inspect `MediaSources[].Path`.
3. If it ends with `.strm`, call `GET /Medusa/Status/{itemId}`.
4. If `is_catalog` is `true` and `stream_ready` is `false`:
   - call `POST /Medusa/Prepare/{itemId}`;
   - display a buffering overlay and poll `Status` every 1–2 seconds.
5. Once `stream_ready` is `true`, start normal Jellyfin playback so transcoding,
   subtitles, and watch progress remain handled by Jellyfin.
6. If `is_catalog` is `false`, play normally.

## Build

```bash
dotnet restore src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj
dotnet build src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj -c Release
dotnet test tests/Jellyfin.Plugin.Catalogo.Tests/Jellyfin.Plugin.Catalogo.Tests.csproj -c Release
```

## Security

- Keep TOD bound to `127.0.0.1` or a private LAN/WireGuard interface.
- `/prepare` and `/lease` on TOD are restricted to loopback by default.
- `Status`, `Prepare`, and `Lease` require Jellyfin authentication.
- Do not expose TOD's `/play?...&download=1` directly to the Internet.

## License

GPL-3.0. See [LICENSE](LICENSE).
