# Medusa — Plugin para Jellyfin

<p align="center"><img src="assets/medusa-wordmark.png" width="320" alt="Medusa"></p>

Plugin de Jellyfin para [TOD](https://github.com/GitCorion/TOD) (*Torrent on
Demand*). Traduce los ítems `.strm` a claves de TOD y expone en la API de
Jellyfin el estado de descarga, la preparación bajo demanda, los *leases* de
reproducción y los metadatos de arranque rápido.

> **Compatibilidad:** Conserva el GUID (`50c67d89-41de-4a95-84e2-4511775d4965`)
> y la configuración. La app Medusa detecta el plugin mediante
> `GET /Medusa/Capabilities`.

El plugin **no** descarga torrents por su cuenta: delega el estado, la
preparación y el streaming en el servicio local de TOD.

## El ecosistema Medusa

| Repositorio | Función |
|---|---|
| [Medusa](https://github.com/GitCorion/Medusa) | Cliente nativo para Android TV. Reproduce a través de Jellyfin. |
| **Medusa-Jellyfin-Plugin** (este repositorio) | API puente en Jellyfin. Expone `/Medusa/Capabilities`, `/Status`, `/Prepare` y `/Lease`, y acelera el arranque de los `.strm`. |
| [TOD](https://github.com/GitCorion/TOD) | Biblioteca, releases, cola de qBittorrent, generación de `.strm` y streaming por rangos. |

Medusa habla únicamente con Jellyfin; este plugin traduce esa comunicación
hacia TOD para mostrar el progreso del buffer, iniciar descargas bajo demanda y
seguir los cambios de fuente.

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
- No exponer `/play?...&download=1` de TOD directamente a Internet.

## Licencia

GPL-3.0. Consulta [LICENSE](LICENSE).
