using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SimLock.Core.Logging;

namespace SimLock.Agent;

/// <summary>
/// Pantalla completa dibujada por el panel de Fore Booking (screenUrl) dentro de un WebView2.
/// Solo se muestra cuando la página cargó bien; si no, el overlay usa sus pantallas propias.
/// La página es de solo lectura: sin menú contextual, sin herramientas, sin zoom y sin salir del dominio.
/// </summary>
public partial class ScreenWindow : Window
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromHours(6);

    private readonly ILog _log;
    private readonly string _userDataFolder;
    private IntPtr _hwnd;
    private Task? _init;
    private bool _unavailable;
    private string? _url;
    private string? _origin;
    private bool _loaded;
    private readonly Stopwatch _sinceAttempt = new();
    private readonly Stopwatch _sinceLoaded = new();
    private string? _lastPosted;
    private bool _compact;

    /// <summary>Color que se vuelve transparente en modo compacto (fondo de la ventana, casi negro).</summary>
    private const uint ColorKey = 0x00010101;

    /// <summary>
    /// Tamaño de la píldora compacta en px CSS, informado por la página (simlock-mode).
    /// Sin ancho (páginas viejas): barra de todo el ancho del monitor.
    /// </summary>
    internal int CompactHeightCss { get; private set; } = 72;
    internal int? CompactWidthCss { get; private set; }
    internal int CompactRadiusCss { get; private set; } = 12;
    internal int CompactMarginCss { get; private set; } = 12;
    /// <summary>top-center | top-left | top-right | bottom-center | bottom-left | bottom-right</summary>
    internal string CompactAnchor { get; private set; } = "top-center";

    private (int W, int H, int R)? _region;

    /// <summary>Recorta la ventana a un rectángulo redondeado del tamaño dado (px físicos). Null la quita.</summary>
    internal void SetRoundedRegion(int? width, int? height, int radius)
    {
        (int, int, int)? wanted = width is int w && height is int h ? (w, h, radius) : null;
        if (wanted == _region) return;
        _region = wanted;
        if (wanted is null)
        {
            Native.SetWindowRgn(Handle, IntPtr.Zero, true);
            return;
        }
        var (rw, rh, rr) = wanted.Value;
        var rgn = Native.CreateRoundRectRgn(0, 0, rw + 1, rh + 1, rr * 2, rr * 2);
        if (Native.SetWindowRgn(Handle, rgn, true) == 0) Native.DeleteObject(rgn);
    }

    internal ScreenWindow(ILog log, string userDataFolder)
    {
        _log = log;
        _userDataFolder = userDataFolder;
        InitializeComponent();
    }

    /// <summary>La página cargó bien y se puede mostrar.</summary>
    internal bool IsReady => !_unavailable && _loaded && _url is not null;

    /// <summary>WebView2 no está instalado o falló al iniciar: no se vuelve a intentar.</summary>
    internal bool IsUnavailable => _unavailable;

    internal IntPtr Handle
    {
        get
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(this).EnsureHandle();
                var ex = Native.GetExStyle(_hwnd);
                ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                ex &= ~Native.WS_EX_APPWINDOW;
                Native.SetExStyle(_hwnd, ex);
            }
            return _hwnd;
        }
    }

    /// <summary>Coordenada fuera de cualquier monitor donde la ventana espera cuando no se usa.</summary>
    private const int ParkedAt = -32000;

    /// <summary>
    /// WebView2 recién se inicia cuando su ventana se mostró al menos una vez. Por eso la ventana se muestra
    /// desde el arranque, pero estacionada fuera de la pantalla, y se trae al monitor solo cuando hace falta.
    /// </summary>
    internal void Prepare()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = ParkedAt;
        Top = ParkedAt;
        Width = 1280;
        Height = 720;
        _ = Handle; // aplica los estilos (sin barra de tareas, sin activar) antes de mostrarla
        Show();
        Park();
    }

    /// <summary>Saca la ventana de la vista sin ocultarla (así WebView2 sigue vivo y cargando).</summary>
    internal void Park() =>
        Native.SetWindowPos(Handle, IntPtr.Zero, ParkedAt, ParkedAt, 1280, 720,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    /// <summary>
    /// Se llama en cada cuadro con la URL que manda el servidor (o null). Carga, reintenta y recarga sola.
    /// </summary>
    internal void Sync(string? url, bool visible)
    {
        if (_unavailable) return;
        if (url is null)
        {
            _url = null;
            _loaded = false;
            return;
        }

        _init ??= InitAsync();
        if (!_init.IsCompleted || Web.CoreWebView2 is null) return;

        var changed = url != _url;
        var retry = !_loaded && _sinceAttempt.IsRunning && _sinceAttempt.Elapsed >= RetryEvery;
        // Recargar de vez en cuando (versiones nuevas del panel), pero nunca mientras se ve.
        var refresh = _loaded && !visible && _sinceLoaded.Elapsed >= ReloadEvery;
        if (changed || retry || refresh || !_sinceAttempt.IsRunning)
            Navigate(url);
    }

    /// <summary>
    /// Compacto: barra arriba de todo que deja pasar toques y clics al simulador, con lo transparente de la
    /// página mostrando el juego (clave de color). Completo: pantalla entera que recibe toques (check-in).
    /// </summary>
    internal void SetCompact(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;
        var hwnd = Handle;
        var ex = Native.GetExStyle(hwnd);
        ex = compact
            ? ex | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
            : ex & ~(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);
        Native.SetExStyle(hwnd, ex);
        if (compact)
            Native.SetLayeredWindowAttributes(hwnd, ColorKey, 255, Native.LWA_COLORKEY | Native.LWA_ALPHA);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        _log.Info(compact ? "Pantalla web: modo compacto." : "Pantalla web: modo completo.");
    }

    /// <summary>Le pasa a la página el estado que decidió el agente (opcional para la página).</summary>
    internal void PostState(object payload)
    {
        if (!IsReady || Web.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(payload, SimLock.Core.Api.Json.Options);
        if (json == _lastPosted) return;
        _lastPosted = json;
        try { Web.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { _log.Warn("No se pudo enviar el estado a la pantalla web", ex); }
    }

    private void Navigate(string url)
    {
        _url = url;
        _loaded = false;
        _lastPosted = null;
        _sinceAttempt.Restart();
        try
        {
            var uri = new Uri(url);
            _origin = uri.GetLeftPart(UriPartial.Authority);
            // Se loguea solo el dominio y la ruta base, nunca el token.
            _log.Info($"Abriendo pantalla web en {_origin}{string.Join('/', uri.AbsolutePath.Split('/').Take(3))}/…");
            Web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _log.Warn("No se pudo abrir la pantalla web", ex);
        }
    }

    private static string SafeOrigin(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "URL inválida";

    /// <summary>Mensajes de la página. Hoy: {"type":"simlock-mode","mode":"compact","height":72}.</summary>
    private void OnPageMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "simlock-mode") return;
            if (root.TryGetProperty("height", out var h) && h.TryGetDouble(out var height))
                CompactHeightCss = (int)Math.Clamp(Math.Ceiling(height), 24, 400);
            CompactWidthCss = root.TryGetProperty("width", out var w) && w.TryGetDouble(out var width) && width > 0
                ? (int)Math.Clamp(Math.Ceiling(width), 40, 2000)
                : null;
            if (root.TryGetProperty("radius", out var r) && r.TryGetDouble(out var radius))
                CompactRadiusCss = (int)Math.Clamp(Math.Round(radius), 0, 200);
            if (root.TryGetProperty("margin", out var m) && m.TryGetDouble(out var margin))
                CompactMarginCss = (int)Math.Clamp(Math.Round(margin), 0, 200);
            if (root.TryGetProperty("anchor", out var a) && a.GetString() is { } anchor
                && anchor is "top-center" or "top-left" or "top-right" or "bottom-center" or "bottom-left" or "bottom-right")
                CompactAnchor = anchor;
        }
        catch (JsonException)
        {
            // Mensaje mal formado: se ignora.
        }
    }

    private async Task InitAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _userDataFolder);
            // Transparente: lo que la página no pinta muestra el fondo de la ventana (la clave de color).
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            var st = core.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsZoomControlEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.AreDefaultScriptDialogsEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            st.IsPasswordAutosaveEnabled = false;

            core.NavigationStarting += (_, e) =>
            {
                // Solo el dominio de la pantalla. Cualquier otro link se ignora.
                var ok = _origin is not null
                         && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
                         && string.Equals(target.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase);
                if (!ok)
                {
                    e.Cancel = true;
                    _log.Warn($"Pantalla web: se bloqueó la salida a otro dominio ({SafeOrigin(e.Uri)}). Si es una redirección a un login, la URL de la pantalla no es la pública.");
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) => OnPageMessage(e.WebMessageAsJson);
            core.NavigationCompleted += (_, e) =>
            {
                _loaded = e.IsSuccess && e.HttpStatusCode is >= 200 and < 300;
                if (_loaded)
                {
                    _sinceLoaded.Restart();
                    _log.Info("Pantalla web cargada.");
                }
                else
                {
                    _log.Warn($"La pantalla web no cargó en {_origin} (HTTP {e.HttpStatusCode}, {e.WebErrorStatus}). Se reintenta en 1 min.");
                }
            };
            core.ProcessFailed += (_, e) =>
            {
                _log.Warn($"El proceso de la pantalla web falló ({e.ProcessFailedKind}). Se recarga.");
                _loaded = false;
                _sinceAttempt.Restart();
            };
            _log.Info($"WebView2 listo (versión {env.BrowserVersionString}).");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _unavailable = true;
            _log.Warn("WebView2 no está instalado: se usan las pantallas propias de SimLock. Instalar 'Microsoft Edge WebView2 Runtime' para la pantalla del panel.");
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _log.Error("No se pudo iniciar WebView2: se usan las pantallas propias de SimLock.", ex);
        }
    }
}
