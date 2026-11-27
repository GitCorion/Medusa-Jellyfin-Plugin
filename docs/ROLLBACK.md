# Reversión de la versión 1.2.0.0

Esta versión añade el proxy de TMDB y la sincronización de credenciales de
Trakt. **Es aditiva**: no hay migraciones de base de datos, no cambia el flujo
de reproducción (`.strm`, `Prepare`, `Lease`, negociación de versiones) y
`Capabilities` conserva `api_version = 1` y las features `status`/`prepare`.
Un cliente antiguo ignora los campos nuevos.

## 1. Mitigación rápida (sin desinstalar)

1. **Proxy TMDB**: vacía el campo *TMDB API key* en Panel → Plugins → Medusa y
   guarda. `/Medusa/Tmdb/*` responderá `503 tmdb_not_configured`. La app 1.2
   detecta el fallo de configuración y vuelve al acceso directo con la clave
   local. Si la TV no tiene clave propia, rellénala en Ajustes.
2. **Sync de Trakt**: borra
   `<datos de Jellyfin>/plugins/configurations/medusa-data.json` con Jellyfin
   parado. Cada dispositivo vuelve a usar su token local; el vínculo por QR
   sigue funcionando como antes.

No es necesario borrar ninguna tabla ni tocar la biblioteca.

## 2. Reversión completa del plugin

1. Detén Jellyfin.
2. Sustituye el DLL de `1.2.0.0` por el de `1.1.4.0` (release publicada o
   `artifacts/`) en `<datos de Jellyfin>/plugins/Medusa_<versión>/`.
3. Arranca Jellyfin. El campo `TmdbApiKey` queda en el XML de configuración
   pero el plugin antiguo lo ignora: no hace falta editarlo.
4. Opcional: elimina `medusa-data.json` (solo contiene credenciales aditivas).

La app con estos cambios es *fail-open*: funciona contra un plugin `1.1.4.0`
sin proxy ni sync, por lo que no hace falta revertir la app a la vez.

## 3. Reversión en git

```bash
git revert <sha-del-commit>
dotnet test tests/Jellyfin.Plugin.Catalogo.Tests/Jellyfin.Plugin.Catalogo.Tests.csproj -c Release
```

No hay dumps ni scripts de migración: el único estado nuevo es el fichero JSON
indicado arriba.

## 4. Verificación tras revertir

```bash
dotnet build src/Jellyfin.Plugin.Catalogo/Jellyfin.Plugin.Catalogo.csproj -c Release
dotnet test tests/Jellyfin.Plugin.Catalogo.Tests/Jellyfin.Plugin.Catalogo.Tests.csproj -c Release
```

Comprobar en Jellyfin que `GET /Medusa/Capabilities` ya no incluye
`tmdb-proxy`/`trakt-sync`, y que la reproducción de un `.strm` sigue igual.
