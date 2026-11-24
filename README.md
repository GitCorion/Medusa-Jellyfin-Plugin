# Medusa — Jellyfin companion plugin

<p align="center"><img src="assets/medusa-wordmark.png" width="320" alt="Medusa"></p>

Companion plugin for the [TOD](https://github.com/GitCorion/TOD) on-demand
torrent proxy, used by the Medusa player. It exposes the proxy state through Jellyfin so a Jellyfin client
(for example a mobile APK) can show download progress, speed, ETA and when a
title is ready to play.

> **Compatibilidad.** El plugin conserva el GUID y la configuración. La app
> Medusa lo detecta por `GET /Medusa/Capabilities`.

The plugin does **not** download torrents itself. It asks the local TOD proxy
for the status and, optionally, to start a download.

## The Medusa stack

This plugin is the bridge between three complementary repositories:

| Repository | Role |
|---|---|
| [Medusa](https://github.com/GitCorion/Medusa) | Native Android TV client. It plays through Jellyfin. |
| **Medusa-Jellyfin-Plugin** (this repository) | Jellyfin companion API. Exposes `/Medusa/Status`, `/Prepare` and `/Lease`, and maps a Jellyfin `.strm` item to a TOD key. |
| [TOD](https://github.com/GitCorion/TOD) | Catalog, sources, downloads, `.strm` generation and range streaming. |

Medusa never talks to TOD directly. It talks to Jellyfin; this plugin translates
that conversation, so the client can show progress, prepare a cold source and
follow source switches without knowing how the catalog works.


## Requirements

- Jellyfin **12.4+**
- `.NET 10` runtime (included with Jellyfin 12.4)
- TOD proxy reachable from the Jellyfin server, normally
  `http://127.0.0.1:8773`
- The proxy must expose `/status` and `/prepare`

The proxy should stay on loopback. This plugin is the bridge between Jellyfin
clients and the proxy; the proxy does not need to be public.

## Install

Add this repository URL in Jellyfin:

```
Dashboard → Plugins → Repositories → +
https://raw.githubusercontent.com/GitCorion/Medusa-Jellyfin-Plugin/main/manifest.json
```

Then install **Medusa** from the plugin catalog and
restart Jellyfin.

Jellyfin checks this repository with the **Update Plugins** scheduled task
(Dashboard → Scheduled Tasks → Update Plugins). Keep the repository URL added
and new versions will be installed automatically.

For manual installation, stop Jellyfin first, copy the DLL from the zip into:

```
<Jellyfin data>/plugins/Medusa_<version>/
```

and start Jellyfin again. Do not copy the DLL over a running server: it can
produce `BadImageFormatException`.

## Configure

Go to:

```
Dashboard → Plugins → Medusa
```

Set the proxy URL, normally:

```
http://127.0.0.1:8773
```

### Native mode (experimental)

Off by default. When enabled, Jellyfin clients that are not Medusa can start a
cold catalog prepare from `PlaybackInfo` and, optionally, wait up to 30
seconds (`Native prebuffer`) before returning the media sources. Medusa keeps
its own preparation flow and is detected from its Jellyfin client headers.
Keep it off until you have tested your other Jellyfin clients.

## API

All endpoints are served by Jellyfin, not by the proxy.

### Capabilities

```http
GET /Medusa/Capabilities
```

```json
{
  "api_version": 1,
  "plugin_version": "1.0.0.0",
  "features": ["status", "prepare"],
  "proxy_configured": true
}
```

This endpoint is anonymous so a client can detect the plugin before playback.
`Status` and `Prepare` require normal Jellyfin authentication.

### Status

```http
GET /Medusa/Status/{itemId}
GET /Medusa/Status/{itemId}?release=109
```

The plugin reads the item's `.strm`, extracts the TOD `key`/`release` and
returns the proxy status:

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
  }
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

Starts the on-demand download without returning video. The client should then
poll `Status` until `stream_ready` is true and play through Jellyfin normally.

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

## Client flow

1. `POST /Items/{itemId}/PlaybackInfo`.
2. Read `MediaSources[].Path`.
3. If it ends in `.strm`, call `GET /Medusa/Status/{itemId}`.
4. If `is_catalog` is true and `stream_ready` is false:
   - call `POST /Medusa/Prepare/{itemId}`;
   - show a progress/buffer overlay;
   - poll `Status` every 1–2 seconds.
5. When `stream_ready` is true, play using Jellyfin's normal playback.
6. If `is_catalog` is false, play normally.

Use Jellyfin playback (not the proxy `play_url`) so Jellyfin keeps transcoding,
subtitles and watched/progress state.

## Build

```bash
dotnet restore src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj
dotnet build src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj -c Release
dotnet test tests/Jellyfin.Plugin.Catalogo.Tests/Jellyfin.Plugin.Catalogo.Tests.csproj -c Release
```

The build produces the plugin DLL.

## Security

- The proxy should listen on `127.0.0.1` (or a private VPN/LAN address).
- `/prepare` in the proxy is restricted to loopback by default.
- Plugin `Status` and `Prepare` require Jellyfin authentication.
- Do not expose the proxy `/play?...&download=1` directly to the internet.

## License

GPL-3.0. See `LICENSE`.
