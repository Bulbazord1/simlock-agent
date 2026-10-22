using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SimLock.Core.Api;
using SimLock.Core.Storage;

namespace SimLock.Agent;

public partial class SetupWindow : Window
{
    private readonly App _app;

    internal SetupWindow(App app)
    {
        _app = app;
        InitializeComponent();
        LoadValues();
    }

    private AgentConfig Config => _app.Config;

    private void LoadValues()
    {
        DeviceNameBox.Text = Config.DeviceName;
        BaseUrlBox.Text = Config.BaseUrl;
        VersionText.Text = $"SimLock {_app.Version}";

        // Ítems armados a mano (sin data binding) para no depender de tipos públicos.
        var monitors = Monitors.All();
        var selected = monitors.FirstOrDefault(m => string.Equals(m.Device, Config.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                       ?? monitors.FirstOrDefault(m => m.IsPrimary);
        foreach (var m in monitors)
        {
            var item = new System.Windows.Controls.ComboBoxItem { Content = m.Label, Tag = m.Device };
            MonitorBox.Items.Add(item);
            if (ReferenceEquals(m, selected)) MonitorBox.SelectedItem = item;
        }

        PinHint.Text = Config.HasPin
            ? "Dejalo vacío para mantener el PIN actual."
            : "Todavía no hay PIN. Definilo antes de vincular: protege esta pantalla.";
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_app.Runtime.IsPaired)
        {
            StatusText.Text = $"Vinculado a {Config.BayName ?? "una bay"}{(Config.VenueName is null ? "" : $" — {Config.VenueName}")}";
            StatusDetail.Text = Config.PairedAt is { } at
                ? $"Desde el {at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}. Vincular con un código nuevo reemplaza la vinculación actual."
                : "";
            UnpairButton.IsEnabled = true;
        }
        else
        {
            StatusText.Text = "Equipo sin vincular";
            StatusDetail.Text = "Generá un código en el panel e ingresalo abajo.";
            UnpairButton.IsEnabled = false;
        }
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            ShowResult(PairResult, "El código tiene 6 dígitos.", error: true);
            return;
        }
        if (!TryApplyPin(requireIfMissing: true)) return;

        var name = string.IsNullOrWhiteSpace(DeviceNameBox.Text) ? Environment.MachineName : DeviceNameBox.Text.Trim();
        Config.DeviceName = name;

        PairButton.IsEnabled = false;
        ShowResult(PairResult, "Vinculando…", error: false);
        try
        {
            var res = await _app.Runtime.PairAsync(code, name, CancellationToken.None);
            Config.BayId = res.Bay?.Id;
            Config.BayName = res.Bay?.Name;
            Config.VenueId = res.Venue?.Id;
            Config.VenueName = res.Venue?.Name;
            Config.PairedAt = DateTimeOffset.Now;
            _app.SaveConfig();
            CodeBox.Clear();
            ShowResult(PairResult, $"Listo: vinculado a {res.Bay?.Name} — {res.Venue?.Name}.", error: false);
            RefreshStatus();
        }
        catch (UnauthorizedException)
        {
            ShowResult(PairResult, "Código inválido, ya usado o vencido. Generá uno nuevo en el panel.", error: true);
        }
        catch (ApiException ex)
        {
            _app.Log.Warn("Fallo al vincular", ex);
            ShowResult(PairResult, $"No se pudo conectar con Fore Booking: {ex.Message}", error: true);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryApplyPin(requireIfMissing: false)) return;

        if (!string.IsNullOrWhiteSpace(DeviceNameBox.Text)) Config.DeviceName = DeviceNameBox.Text.Trim();
        if (MonitorBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string device }) Config.MonitorDevice = device;

        var url = BaseUrlBox.Text.Trim().TrimEnd('/');
        var urlChanged = !string.Equals(url, Config.BaseUrl, StringComparison.OrdinalIgnoreCase);
        if (urlChanged)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !u.IsLoopback))
            {
                ShowResult(SaveResult, "El servidor tiene que ser una dirección https://", error: true);
                return;
            }
            Config.BaseUrl = url;
        }

        _app.SaveConfig();
        if (urlChanged)
        {
            await _app.RebuildRuntimeAsync();
            _app.Log.Info($"Servidor cambiado a {url}.");
        }
        ShowResult(SaveResult, urlChanged && _app.Runtime.IsPaired
            ? "Guardado. Ojo: cambiaste de servidor; si el equipo no figura ahí, vinculalo de nuevo."
            : "Guardado.", error: false);
    }

    /// <summary>Si se escribió un PIN, lo valida y guarda. Devuelve false si hay un error para mostrar.</summary>
    private bool TryApplyPin(bool requireIfMissing)
    {
        var pin = PinBox.Password;
        var confirm = PinConfirmBox.Password;
        if (pin.Length == 0 && confirm.Length == 0)
        {
            if (requireIfMissing && !Config.HasPin)
            {
                ShowResult(PairResult, "Primero definí un PIN de configuración (abajo).", error: true);
                return false;
            }
            return true;
        }
        var target = requireIfMissing ? PairResult : SaveResult;
        if (pin != confirm)
        {
            ShowResult(target, "Los dos PIN no coinciden.", error: true);
            return false;
        }
        if (!Pin.IsValidFormat(pin))
        {
            ShowResult(target, "El PIN tiene que tener entre 4 y 12 dígitos.", error: true);
            return false;
        }
        Pin.Set(Config, pin);
        _app.SaveConfig();
        PinBox.Clear();
        PinConfirmBox.Clear();
        PinHint.Text = "PIN actualizado. Dejalo vacío para mantenerlo.";
        return true;
    }

    private void OnUnpair(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "Este equipo deja de mostrar turnos hasta que lo vincules otra vez. ¿Seguir?",
            "Desvincular", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!ok) return;
        _app.Runtime.Unpair();
        Config.BayId = Config.BayName = Config.VenueId = Config.VenueName = null;
        Config.PairedAt = null;
        _app.SaveConfig();
        RefreshStatus();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Paths.Logs}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _app.Log.Warn("No se pudo abrir la carpeta de logs", ex); }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "El overlay deja de funcionar hasta que se vuelva a abrir SimLock (o se reinicie la PC). ¿Salir?",
            "Salir de SimLock", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (ok) _app.ExitAgent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void ShowResult(System.Windows.Controls.TextBlock target, string text, bool error)
    {
        target.Text = text;
        target.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x7D, 0x4F));
        target.Visibility = Visibility.Visible;
    }
}
