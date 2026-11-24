# Contrato con Medusa

Documento de referencia para cambiar el plugin sin romper la reproducción de
Medusa. No describe lo que el plugin hace hoy en abstracto: describe lo que
Medusa consume de verdad.

- Plugin: `Jellyfin.Plugin.Catalogo` 1.1.4.0 (`89b7122`).
- Medusa: `main` (`e1ad950`) y build `2.4.2-beta.6`.
- Jellyfin: 12.4.

## 1. Flujo real de Medusa

Medusa no pide `PlaybackInfo` en frío. El orden es:

1. `getItemDetail()` sobre el ítem de Jellyfin.
2. Detecta `.strm` con `JellyfinPlaybackSourceResolver.isStrm()`.
3. Elige la primera `MediaSource` (o la preferida) y saca `release` del
   `Path` del `.strm`.
4. `GET /Medusa/Capabilities`.
5. `GET /Medusa/Status/{itemId}?release=...`.
6. Si `stream_ready` es `false`, `POST /Medusa/Prepare/{itemId}?release=...`
   una sola vez.
7. Poll de `Status` cada 1,5 s hasta `stream_ready`.
8. Solo entonces pide `PlaybackInfo` y construye los candidatos de
   reproducción.
9. Durante la reproducción vuelve a consultar `Status` para vigilar
   `stream_generation`; si cambia, reinicia la reproducción.

Referencias:

- `app/src/main/java/tv/medusa/app/ui/screens/player/PlayerViewModel.kt`
- `app/src/main/java/tv/medusa/app/data/remote/jellyfin/MedusaModels.kt`
- `app/src/main/java/tv/medusa/app/data/remote/jellyfin/JellyfinPlaybackSourceResolver.kt`
- `docs/PLAYBACK.md`

## 2. Endpoints que Medusa consume

### `GET /Medusa/Capabilities`

Anónimo. Medusa lo considera soportado solo si:

```json
{
  "api_version": 1,
  "features": ["status", "prepare", "..."]
}
```

`MedusaCapabilitiesDto.isSupported` exige `api_version == 1` y que `features`
contenga `status` y `prepare`. **Añadir features es seguro; quitarlas,
renombrarlas o cambiar `api_version` rompe la detección.**

### `GET /Medusa/Status/{itemId}?release=`

Autenticado. Devuelve:

```json
{
  "is_catalog": true,
  "item_id": "...",
  "key": "movie:tmdb:...",
  "release_id": 123,
  "status": { "...": "..." }
}
```

Medusa usa, entre otros:

- `is_catalog`
- `status.stream_ready`
- `status.stream_generation`
- `status.stream_switched_at`
- `status.stream_switch_reason`
- `status.release_id`
- `status.state`
- `status.progress`
- `status.buffer_bytes`
- `status.buffer_required`
- `status.downloaded_bytes`
- `status.size_bytes`
- `status.speed_bps`
- `status.eta_seconds`
- `status.seeds`
- `status.peers`

El plugin debe seguir pasando el `status` de TOD sin reescribir nombres ni
semántica.

Comportamientos especiales:

- `401`: Medusa reintenta autenticación.
- `503`: Medusa muestra `message` y distingue `UNAVAILABLE` si contiene
  «No hay ninguna fuente».
- `404` o error de red: Medusa sigue en modo *fail-open* como un ítem normal.
- `is_catalog: false`: Medusa reproduce como cualquier ítem de Jellyfin.

### `POST /Medusa/Prepare/{itemId}?release=`

Autenticado. Medusa lo llama una sola vez tras ver `stream_ready=false`.
Espera éxito y luego sigue haciendo poll de `Status`. Un `503` con `message`
se muestra en el overlay.

### `POST/DELETE /Medusa/Lease/{itemId}`

Lo usa el ecosistema del plugin para mantener el lease de TOD. Medusa no
depende de este endpoint para su flujo principal, pero no se debe cambiar su
semántica sin revisar `PlaybackLeaseService`.

### Alias legacy `/Catalogo/*`

Se mantiene para la app estable 2.4.1. No retirar mientras haya clientes
estables.

## 3. Invariantes que no se deben romper

1. **`api_version = 1`** y las features `status` y `prepare` siguen presentes.
2. **Forma y nombres** de `MedusaStatusDto`, `MedusaPrepareResponseDto` y
   `MedusaErrorDto`.
3. **`is_catalog`** sigue significando «este ítem es del catálogo TOD».
4. **`MediaSources`**: Medusa elige la primera fuente y saca `release` del
   `Path`. No reordenar, añadir variantes delante ni cambiar `Path`/`Id` sin
   una razón y una prueba explícita.
5. **`Path` del `.strm`** sigue terminando en `.strm` y conservando el query
   `key` y `release`.
6. **`Container`** puede pasar de `strm` a `mkv` en el fast-start; Medusa
   sigue detectando `.strm` por el `Path`. No eliminar el `Path`.
7. **`stream_ready`** sigue viniendo de TOD; no inventar un `true` antes de
   que TOD pueda servir bytes.
8. **`stream_generation`** no se debe filtrar ni normalizar: Medusa reinicia
   cuando cambia.
9. **`HideTextSubtitles`** e **`IsComplete`** son parches deliberados para que
   ffmpeg no se quede leyendo el torrent a medias. No tocarlos sin reproducir
   el caso de un `.strm` frío con subtítulos de texto.
10. **`GetPlaybackMediaSources`** no debe bloquearse varios segundos en el
    camino de Medusa. Medusa ya hace su propio overlay y poll.

## 4. Matriz de riesgo

| Cambio propuesto | Toca | Riesgo para Medusa | Mitigación |
|---|---|---|---|
| Preparar en `PlaybackInfo` | `GetPlaybackMediaSources` | Medio: en el camino normal Medusa ya está `stream_ready`; en *fail-open* puede llegar frío y duplicar `Prepare`. | Flag apagado por defecto; detectar Medusa y no cambiar su camino. |
| Esperar buffer antes de responder `PlaybackInfo` | `GetPlaybackMediaSources` | Bajo/medio: Medusa solo lo pide tras `stream_ready`; en *fail-open* podría colgarse. | Solo modo nativo; timeout corto; nunca si el cliente es Medusa. |
| Fast-start con `allowMediaProbe=false` | `GetPlaybackMediaSources` | Medio: llamadas extra a `/status` y `/media-info`; puede alterar el orden de fuentes. | Flag; tests de conteo y orden; no cambiar `MediaSources`. |
| Refrescar streams o duración | `SaveStreams` | Alto: puede cambiar índices de audio/subtítulos en caliente. | Solo con el ítem inactivo; solo añadir, nunca reordenar; flag. |
| Mapear más campos de `media-info` | TOD + `CatalogoMediaStreams` | Medio: puede cambiar direct play/transcode y el comportamiento de audio. | Campos aditivos; probar en Medusa/Android TV; flag. |
| Proveedor de metadatos o NFO | nuevo `IRemoteMetadataProvider` / `strm.py` | Medio: rescans y reidentificación; Medusa guarda IDs de Jellyfin. | Opt-in; no renombrar; no cambiar IDs. |
| Token para `/prepare` y `/lease` en Docker | TOD `http.py` | Bajo para Medusa en host; seguridad si se abre a la LAN. | Token/allowlist opt-in; loopback por defecto. |
| Cambiar `Capabilities` | API | Alto si se quitan features o cambia `api_version`. | Solo añadir; mantener `api_version=1`. |
| Cambiar `HideTextSubtitles` | reproducción | Alto: puede reintroducir el bloqueo de ffmpeg. | No tocar en modo compatibilidad. |

## 5. Plan de pruebas mínimo

Los tests de contrato viven en
`tests/Jellyfin.Plugin.Catalogo.Tests/MedusaContractTests.cs` y
`CatalogoProxyClientTests.cs`. No cambian comportamiento: congelan la API que
consume Medusa.

### Plugin, sin Medusa

- `Capabilities` mantiene `api_version=1` y las features `status` y `prepare`.
- `Status`, `Prepare` y `Lease` conservan forma y códigos.
- `GetPlaybackMediaSources` con un `.strm` de catálogo:
  - frío, sin streams guardados;
  - caliente, con streams guardados;
  - completo e incompleto;
  - conserva `Path`, `Id` y orden de `MediaSources`;
  - no llama a `Prepare` cuando ya está `stream_ready`.
- `HideTextSubtitles` y `IsComplete` mantienen el comportamiento actual.

### Medusa 2.4.2-beta.6

- Película fría: overlay, `Prepare`, poll y arranque.
- Episodio frío de un pack.
- Ítem ya `stream_ready`: sin overlay.
- Pausa, reanudación y seek.
- Cambio de audio y subtítulos.
- Varias versiones: `release` correcto.
- `stream_generation` cambia durante la reproducción: reinicio controlado.
- `503` sin fuente y `404` *fail-open*.
- Reproducción simultánea con Jellyfin iOS.

### Criterio de aceptación

Con el modo nativo apagado, el comportamiento observable debe ser idéntico a
`1.1.4.0`. Con el modo nativo encendido, Medusa debe seguir pasando toda la
matriz anterior.

## 6. Diseño propuesto para el modo nativo

- Añadir `NativeMode` a `PluginConfiguration`, **apagado por defecto**.
- Detectar Medusa por `Authorization`/`X-Emby-Authorization`
  (`Client="Medusa"`) o `X-Emby-Client`. Si hay duda, aplicar el camino de
  compatibilidad.
- Todo comportamiento nuevo va detrás de `NativeMode` y de la detección:
  - preparación server-side en `PlaybackInfo`;
  - prebuffer opcional y acotado;
  - refresco de streams solo con el ítem inactivo.
- No cambiar `Capabilities`, `MediaSources`, `Path` ni `HideTextSubtitles` en
  el camino de compatibilidad.

Estado actual:

- `NativeMode` ya existe en la configuración y está apagado por defecto.
- `Capabilities` publica `native_mode` como campo aditivo; Medusa lo ignora.
- La preparación server-side solo se activa con el flag y cuando el cliente
  no es Medusa. Sin contexto HTTP se aplica compatibilidad.
- `MediaSources`, `Path`, `stream_ready` y `HideTextSubtitles` siguen igual.

## 7. Rollback

- `NativeMode` apagado devuelve el camino actual.
- Sin migraciones de base de datos.
- Se puede volver a `1.1.4.0` instalando el DLL anterior.
- El manifiesto y el GUID del plugin no cambian.
