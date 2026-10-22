using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SimLock.Core.Display;
using SimLock.Core.Runtime;
using SimLock.Core.Time;

namespace SimLock.Agent;

/// <summary>
/// La ventana del overlay. Es siempre la misma ventana; cambia de tamaño según el modo:
/// pantalla completa (tapa el juego), píldora o tarjeta en la esquina (no tapa, deja pasar clics)
/// o aviso centrado unos segundos. Nunca toma el foco: el teclado sigue yendo al simulador.
/// </summary>
public partial class OverlayWindow : Window
{
    private enum Mode { Hidden, Full, Rest, Pill, Warn, Toast }

    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TopmostEvery = TimeSpan.FromSeconds(2);
    private const double CornerMargin = 24;
    private const int HotkeyId = 0x5131;

    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private Mode _mode = Mode.Hidden;
    private bool _clickThrough;
    private Native.RECT _lastRect;
    private readonly Stopwatch _sinceTopmost = Stopwatch.StartNew();
    private long _toastUntil;
    private int _toastMinutes;
    private string? _qrUrl;
    private ImageSource? _qrImage;
    private bool _hotkeyRegistered;
    private readonly Stopwatch _sinceRestMove = Stopwatch.StartNew();
    private bool _restNeedsMove = true;
    private static readonly TimeSpan RestMoveEvery = TimeSpan.FromSeconds(60);

    internal OverlayWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
    }

    internal void Initialize()
    {
        // Crear el HWND sin mostrar la ventana todavía.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = Native.GetExStyle(_hwnd);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetExStyle(_hwnd, ex);

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _hotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId,
            Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x53 /* S */);
        if (!_hotkeyRegistered) _app.Log.Warn("No se pudo registrar Ctrl+Alt+Shift+S (otro programa lo usa).");

        _timer.Start();
        Tick();
    }

    internal void Shutdown()
    {
        _timer.Stop();
        if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
        Close();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.InvokeAsync(_app.RequestSetup);
        }
        return IntPtr.Zero;
    }

    // ───────────────────────── ciclo de dibujo ─────────────────────────

    private void Tick()
    {
        AgentFrame frame;
        try
        {
            frame = _app.Runtime.NextFrame();
        }
        catch (Exception ex)
        {
            _app.Log.Error("Error calculando el cuadro", ex);
            return;
        }

        var m = frame.Display;
        if (frame.Announcement is { } a)
        {
            _toastMinutes = a.WarningMinutes;
            _toastUntil = Stopwatch.GetTimestamp() + (long)(ToastDuration.TotalSeconds * Stopwatch.Frequency);
        }
        var toastActive = Stopwatch.GetTimestamp() < _toastUntil;

        Mode mode;
        if (_app.IsConfiguring) mode = Mode.Hidden;
        else if (!frame.Paired) mode = Mode.Full;
        else mode = m.State switch
        {
            AgentState.Disabled => Mode.Hidden,
            AgentState.Unknown => m.Offline ? Mode.Pill : Mode.Hidden,
            AgentState.InProgress or AgentState.LastMinutes when toastActive => Mode.Toast,
            AgentState.InProgress => Mode.Pill,
            AgentState.LastMinutes => Mode.Warn,
            _ when m.Resting => Mode.Rest,
            _ => Mode.Full,
        };

        ApplyBranding(m);
        switch (mode)
        {
            case Mode.Full: RenderFull(m, frame.Paired); break;
            case Mode.Rest: RenderRest(m, mode != _mode); break;
            case Mode.Pill: RenderPill(m); break;
            case Mode.Warn: RenderWarn(m); break;
            case Mode.Toast: RenderToast(m); break;
        }
        ApplyLayout(mode);
    }

    private Color? _appliedBrand;

    private void ApplyBranding(DisplayModel m)
    {
        var brand = Visuals.BrandColor(m.Branding?.PrimaryColor);
        if (_appliedBrand == brand) return;
        _appliedBrand = brand;
        var fg = Visuals.ForegroundFor(brand);
        var brandBrush = new SolidColorBrush(brand);
        FullPanel.Background = brandBrush;
        WarnPanel.Background = new SolidColorBrush(Color.FromArgb(0xF2, brand.R, brand.G, brand.B));
        ToastPanel.Background = WarnPanel.Background;
        foreach (var tb in new[] { FullTitle, FullSubtitle, FullOffline, FullFooter, WarnLabel, WarnTime, WarnEnds, WarnOffline, ToastTitle, ToastSubtitle })
            tb.Foreground = fg;
    }

    private void RenderFull(DisplayModel m, bool paired)
    {
        string title, subtitle;
        string? caption = null;
        var qr = m.ShowQr ? m.QrUrl : null;
        var hour = m.StartsAt is { } st ? VenueTime.FormatHour(st, m.VenueTimezone) : null;

        if (!paired)
        {
            title = "Equipo sin vincular";
            subtitle = "Pedile al personal que lo vincule desde el panel de Fore Booking.";
            qr = null;
        }
        else switch (m.State)
        {
            case AgentState.Upcoming:
                title = string.IsNullOrWhiteSpace(m.CustomerFirstName) ? "Tu turno está por empezar" : $"Hola, {m.CustomerFirstName}";
                subtitle = hour is null ? "Tu turno está por empezar" : $"Tu turno empieza a las {hour}";
                caption = "Escaneá para hacer check-in";
                break;
            case AgentState.AwaitingCheckin:
                title = "Hacé check-in para empezar";
                subtitle = "Escaneá el código con tu celular";
                caption = "Check-in";
                break;
            case AgentState.Ended:
                title = "Tu turno terminó";
                subtitle = qr is not null ? "¿Querés seguir jugando? Extendé tu turno desde el celular." : "¡Gracias por jugar!";
                caption = "Escaneá para extender tu turno";
                break;
            default: // Idle
                title = m.BayName ?? "SimLock";
                subtitle = m.VenueName ?? "";
                break;
        }

        Set(FullTitle, title);
        Set(FullSubtitle, subtitle);
        SetLogo(FullLogo, m.Branding?.LogoUrl);
        SetQr(FullQrCard, FullQr, qr);
        if (caption is not null) Set(FullQrCaption, caption);
        SetVisible(FullMessage, m.MessageText);
        Set(FullMessageText, m.MessageText ?? "");
        SetVisible(FullPending, m.PendingPaymentCents > 0 && paired);
        SetVisible(FullOffline, m.Offline);
        Set(FullFooter, paired
            ? string.Join(" · ", new[] { m.BayName, m.VenueName }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : $"SimLock {_app.Version} · Configuración: Ctrl+Alt+Shift+S");
    }

    private void RenderRest(DisplayModel m, bool entering)
    {
        Set(RestClock, VenueTime.FormatHour(m.Now, m.VenueTimezone));
        Set(RestBay, m.BayName ?? "");
        SetLogo(RestLogo, m.Branding?.LogoUrl);
        SetQr(RestQrCard, RestQr, m.ShowQr ? m.QrUrl : null);
        Set(RestQrCaption, m.State == AgentState.Ended ? "¿Seguís jugando? Extendé tu turno" : "Reservá este simulador desde tu celular");
        SetVisible(RestMessage, m.MessageText);
        Set(RestMessage, m.MessageText ?? "");
        SetVisible(RestOffline, m.Offline);

        // Cambia de lugar cada minuto para no marcar la pantalla con una imagen fija.
        if (entering || _restNeedsMove || _sinceRestMove.Elapsed >= RestMoveEvery)
        {
            _sinceRestMove.Restart();
            _restNeedsMove = false;
            Dispatcher.InvokeAsync(MoveRestCard, DispatcherPriority.Loaded);
        }
    }

    private void MoveRestCard()
    {
        RestCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = RestPanel.ActualWidth;
        var h = RestPanel.ActualHeight;
        var cw = RestCard.DesiredSize.Width;
        var ch = RestCard.DesiredSize.Height;
        if (w <= 0 || h <= 0)
        {
            _restNeedsMove = true; // todavía no tiene tamaño: reintentar en el próximo cuadro
            return;
        }
        const double pad = 48;
        var x = pad + Random.Shared.NextDouble() * Math.Max(0, w - cw - 2 * pad);
        var y = pad + Random.Shared.NextDouble() * Math.Max(0, h - ch - 2 * pad);
        Canvas.SetLeft(RestCard, x);
        Canvas.SetTop(RestCard, y);
    }

    private void RenderPill(DisplayModel m)
    {
        Set(PillTime, m.Remaining is { } r ? VenueTime.FormatCountdown(r) : "—");
        SetVisible(PillMessage, m.MessageText);
        Set(PillMessage, m.MessageText ?? "");
        SetVisible(PillPending, m.PendingPaymentCents > 0);
        SetVisible(PillOffline, m.Offline);
    }

    private void RenderWarn(DisplayModel m)
    {
        Set(WarnTime, m.Remaining is { } r ? VenueTime.FormatCountdown(r) : "0:00");
        Set(WarnEnds, m.EndsAt is { } e ? $"Tu turno termina a las {VenueTime.FormatHour(e, m.VenueTimezone)}" : "");
        SetQr(WarnQrCard, WarnQr, m.ShowQr ? m.QrUrl : null);
        SetVisible(WarnMessage, m.MessageText);
        Set(WarnMessage, m.MessageText ?? "");
        SetVisible(WarnPending, m.PendingPaymentCents > 0);
        SetVisible(WarnOffline, m.Offline);
    }

    private void RenderToast(DisplayModel m)
    {
        Set(ToastTitle, _toastMinutes == 1 ? "Queda 1 minuto" : $"Quedan {_toastMinutes} minutos");
        Set(ToastSubtitle, m.EndsAt is { } e ? $"Tu turno termina a las {VenueTime.FormatHour(e, m.VenueTimezone)}" : "");
        SetQr(ToastQrCard, ToastQr, m.ShowQr ? m.QrUrl : null);
    }

    // ───────────────────────── posición y estilo de ventana ─────────────────────────

    private void ApplyLayout(Mode mode)
    {
        if (mode == Mode.Hidden)
        {
            if (IsVisible) Hide();
            _mode = mode;
            return;
        }

        FullPanel.Visibility = mode == Mode.Full ? Visibility.Visible : Visibility.Collapsed;
        RestPanel.Visibility = mode == Mode.Rest ? Visibility.Visible : Visibility.Collapsed;
        PillPanel.Visibility = mode == Mode.Pill ? Visibility.Visible : Visibility.Collapsed;
        WarnPanel.Visibility = mode == Mode.Warn ? Visibility.Visible : Visibility.Collapsed;
        ToastPanel.Visibility = mode == Mode.Toast ? Visibility.Visible : Visibility.Collapsed;

        var monitor = Monitors.Resolve(_app.Config.MonitorDevice);
        if (monitor is null) return;
        var b = monitor.Bounds;
        var scale = monitor.Scale;

        Native.RECT rect;
        if (mode is Mode.Full or Mode.Rest)
        {
            rect = b;
        }
        else
        {
            FrameworkElement panel = mode switch { Mode.Pill => PillPanel, Mode.Warn => WarnPanel, _ => ToastPanel };
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = (int)Math.Ceiling(panel.DesiredSize.Width * scale);
            var h = (int)Math.Ceiling(panel.DesiredSize.Height * scale);
            var margin = (int)(CornerMargin * scale);
            int x, y;
            if (mode == Mode.Toast)
            {
                x = b.Left + (b.Width - w) / 2;
                y = b.Top + (b.Height - h) / 2;
            }
            else
            {
                // Esquina superior derecha: donde menos estorba en la mayoría de los simuladores.
                x = b.Right - w - margin;
                y = b.Top + margin;
            }
            rect = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }

        // Pantalla completa bloquea clics; el resto los deja pasar al juego.
        SetClickThrough(mode is not (Mode.Full or Mode.Rest));

        if (!IsVisible) Show();

        var moved = mode != _mode || !SameRect(rect, _lastRect);
        if (moved || _sinceTopmost.Elapsed >= TopmostEvery)
        {
            // Reafirma "siempre arriba": algunos juegos se ponen encima al tomar el foco.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            _lastRect = rect;
            _sinceTopmost.Restart();
        }
        _mode = mode;
    }

    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough) return;
        var ex = Native.GetExStyle(_hwnd);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetExStyle(_hwnd, ex);
        _clickThrough = on;
    }

    private static bool SameRect(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // ───────────────────────── utilidades ─────────────────────────

    private static void Set(TextBlock tb, string text)
    {
        if (tb.Text != text) tb.Text = text;
    }

    private static void SetVisible(UIElement e, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (e.Visibility != v) e.Visibility = v;
    }

    private static void SetVisible(UIElement e, string? text) => SetVisible(e, !string.IsNullOrWhiteSpace(text));

    private void SetQr(UIElement card, Image image, string? url)
    {
        if (url is null)
        {
            SetVisible(card, false);
            return;
        }
        if (url != _qrUrl)
        {
            _qrUrl = url;
            try { _qrImage = Visuals.QrImage(url); }
            catch (ArgumentException ex) { _app.Log.Error("qrUrl demasiado largo para un QR", ex); _qrImage = null; }
        }
        if (!ReferenceEquals(image.Source, _qrImage)) image.Source = _qrImage;
        SetVisible(card, _qrImage is not null);
    }

    private static void SetLogo(Image image, string? url)
    {
        var logo = Visuals.Logo(url);
        if (!ReferenceEquals(image.Source, logo)) image.Source = logo;
        SetVisible(image, logo is not null);
    }
}
