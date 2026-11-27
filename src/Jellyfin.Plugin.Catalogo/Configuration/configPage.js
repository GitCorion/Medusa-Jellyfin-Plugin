const MedusaConfig = {
    pluginUniqueId: '50c67d89-41de-4a95-84e2-4511775d4965'
};

const i18n = {
    es: {
        subtitle: 'Jellyfin ↔ TOD · catálogo bajo demanda',
        proxyLabel: 'URL del proxy TOD',
        proxyDesc: 'URL local del proxy TOD usado por Medusa. Por defecto: http://127.0.0.1:8773.',
        timeoutLabel: 'Tiempo de espera (segundos)',
        timeoutDesc: 'Tiempo de espera HTTP para consultas de estado de TOD. Por defecto: 15 segundos (rango: 2–120).',
        prepareLabel: 'Tiempo de espera de preparación (segundos)',
        prepareDesc: 'Iniciar un torrent en frío puede tardar más que consultar el estado. Este límite solo aplica a Prepare. Por defecto: 40 segundos (rango: 5–120).',
        tmdbLabel: 'Clave API de TMDB',
        tmdbPlaceholder: 'Pega tu clave API v3 de TMDB',
        tmdbDesc: 'Clave API de TMDB (v3). El servidor actúa como proxy para todos tus dispositivos Medusa: la usan sin tener que configurarla ni descargar una copia de la clave.',
        nativeLabel: 'Modo nativo',
        nativeDesc: 'Prepara elementos del catálogo en frío desde clientes Jellyfin que no son Medusa. Medusa conserva su propio flujo. Experimental y desactivado por defecto.',
        prebufferLabel: 'Prebúfer nativo (segundos)',
        prebufferDesc: 'Tiempo que PlaybackInfo nativo espera a que el stream esté listo tras iniciar la preparación. Cero devuelve al instante. Medusa nunca espera. Por defecto: 0 segundos (rango: 0–30).',
        save: 'Guardar'
    },
    en: {
        subtitle: 'Jellyfin ↔ TOD · on-demand catalog',
        proxyLabel: 'TOD proxy URL',
        proxyDesc: 'Local URL of the TOD proxy used by Medusa. Default: http://127.0.0.1:8773.',
        timeoutLabel: 'Timeout (seconds)',
        timeoutDesc: 'HTTP timeout for TOD status requests. Default: 15 seconds (range: 2–120).',
        prepareLabel: 'Prepare timeout (seconds)',
        prepareDesc: 'Starting a cold torrent can take longer than a status request. This timeout only applies to Prepare. Default: 40 seconds (range: 5–120).',
        tmdbLabel: 'TMDB API key',
        tmdbPlaceholder: 'Paste your TMDB v3 API key',
        tmdbDesc: 'TMDB API key (v3). The server acts as a proxy for all your Medusa devices: they can use it without configuring or downloading a copy of the key.',
        nativeLabel: 'Native mode',
        nativeDesc: 'Prepare cold catalog items from Jellyfin clients that are not Medusa. Medusa keeps its own preparation flow. Experimental and disabled by default.',
        prebufferLabel: 'Native prebuffer (seconds)',
        prebufferDesc: 'How long a native PlaybackInfo waits for the stream after starting a prepare. Zero returns immediately. Medusa never waits. Default: 0 seconds (range: 0–30).',
        save: 'Save'
    }
};

function detectLocale() {
    let lang = '';
    try {
        if (window.Globalize && typeof window.Globalize.getCurrentLocale === 'function') {
            lang = window.Globalize.getCurrentLocale() || '';
        }
    } catch (e) {}
    if (!lang) {
        lang = document.documentElement.lang || navigator.language || 'en';
    }
    return lang.toLowerCase().startsWith('es') ? 'es' : 'en';
}

function applyTranslations(view) {
    const dict = i18n[detectLocale()] || i18n.en;
    for (const node of view.querySelectorAll('[data-i18n]')) {
        const key = node.getAttribute('data-i18n');
        if (dict[key]) {
            node.textContent = dict[key];
        }
    }
    for (const node of view.querySelectorAll('[data-i18n-placeholder]')) {
        const key = node.getAttribute('data-i18n-placeholder');
        if (dict[key]) {
            node.setAttribute('placeholder', dict[key]);
        }
    }
}

export default function (view) {
    applyTranslations(view);

    view.addEventListener('viewshow', function () {
        applyTranslations(view);
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(MedusaConfig.pluginUniqueId).then(function (config) {
            view.querySelector('#ProxyUrl').value =
                config.ProxyUrl || 'http://127.0.0.1:8773';
            view.querySelector('#TimeoutSeconds').value =
                config.TimeoutSeconds || 15;
            view.querySelector('#PrepareTimeoutSeconds').value =
                config.PrepareTimeoutSeconds || 40;
            view.querySelector('#NativeMode').checked =
                config.NativeMode === true;
            view.querySelector('#NativePrebufferSeconds').value =
                (config.NativePrebufferSeconds !== undefined && config.NativePrebufferSeconds !== null)
                    ? config.NativePrebufferSeconds
                    : 0;
            view.querySelector('#TmdbApiKey').value =
                config.TmdbApiKey || '';
            Dashboard.hideLoadingMsg();
        }).catch(function () {
            Dashboard.hideLoadingMsg();
        });
    });

    view.querySelector('#MedusaConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(MedusaConfig.pluginUniqueId).then(function (config) {
            config.ProxyUrl = view.querySelector('#ProxyUrl').value.trim() || 'http://127.0.0.1:8773';
            config.TimeoutSeconds =
                parseInt(view.querySelector('#TimeoutSeconds').value, 10) || 15;
            config.PrepareTimeoutSeconds =
                Math.max(5, Math.min(120,
                    parseInt(view.querySelector('#PrepareTimeoutSeconds').value, 10) || 40));
            config.NativeMode =
                view.querySelector('#NativeMode').checked === true;
            const rawPrebuffer = parseInt(view.querySelector('#NativePrebufferSeconds').value, 10);
            config.NativePrebufferSeconds =
                Math.max(0, Math.min(30, Number.isNaN(rawPrebuffer) ? 0 : rawPrebuffer));
            config.TmdbApiKey = view.querySelector('#TmdbApiKey').value.trim();
            return ApiClient.updatePluginConfiguration(MedusaConfig.pluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        }).catch(function () {
            Dashboard.hideLoadingMsg();
        });
        return false;
    });
}
