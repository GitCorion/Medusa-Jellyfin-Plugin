# Medusa — Jellyfin companion plugin

<p align="center"><img src="assets/medusa-wordmark.png" width="320" alt="Medusa"></p>

Companion plugin for the [TOD](https://github.com/GitCorion/TOD) on-demand
torrent proxy, used by the Medusa player. It exposes the proxy state through Jellyfin so a Jellyfin client
(for example a mobile APK) can show download progress, speed, ETA and when a
title is ready to play.

> **Compatibilidad con el nombre.** El plugin conserva el GUID y las rutas
> `/Catalogo/...` que usa el cliente Medusa. Desde 1.1.0.0 aparece en Jellyfin
> como **Medusa**; el cliente lo detecta por `/Catalogo/Capabilities`, así que
> el cambio de nombre no rompe la app. El archivo de configuración, el
> namespace y el assembly siguen siendo `Jellyfin.Plugin.Catalogo`.

The plugin does **not** download torrents itself. It asks a local Catalogo proxy
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
- Catalogo proxy reachable from the Jellyfin server, normally
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

Then install **Catalogo** from the plugin catalog and restart Jellyfin.

For manual installation, copy `Jellyfin.Plugin.Catalogo.dll` into:

```
<Jellyfin data>/plugins/Catalogo_1.0.0.0/
```

## Configure

Go to:

```
Dashboard → Plugins → Catalogo
```

Set the proxy URL, normally:

```
http://127.0.0.1:8773
```

## API

All endpoints are served by Jellyfin, not by the proxy.

### Capabilities

```http
GET /Catalogo/Capabilities
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
GET /Catalogo/Status/{itemId}
GET /Catalogo/Status/{itemId}?release=109
```

The plugin reads the item's `.strm`, extracts the Catalogo `key`/`release` and
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

For non-Catalogo items:

```json
{ "is_catalog": false, "item_id": "..." }
```

### Prepare

```http
POST /Catalogo/Prepare/{itemId}
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
3. If it ends in `.strm`, call `GET /Catalogo/Status/{itemId}`.
4. If `is_catalog` is true and `stream_ready` is false:
   - call `POST /Catalogo/Prepare/{itemId}`;
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

The build produces `Jellyfin.Plugin.Catalogo.dll`.

## Security

- The proxy should listen on `127.0.0.1` (or a private VPN/LAN address).
- `/prepare` in the proxy is restricted to loopback by default.
- Plugin `Status` and `Prepare` require Jellyfin authentication.
- Do not expose the proxy `/play?...&download=1` directly to the internet.

## License

GPL-3.0. See `LICENSE`.
