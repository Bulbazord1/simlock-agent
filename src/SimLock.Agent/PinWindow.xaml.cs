using System.Windows;
using SimLock.Core.Storage;

namespace SimLock.Agent;

public partial class PinWindow : Window
{
    private readonly AgentConfig _config;
    private int _attempts;

    internal PinWindow(AgentConfig config)
    {
        _config = config;
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e) => TryAccept();

    private void TryAccept()
    {
        if (Pin.Verify(_config, PinBox.Password))
        {
            DialogResult = true;
            return;
        }
        _attempts++;
        PinBox.Clear();
        ErrorText.Text = "PIN incorrecto.";
        ErrorText.Visibility = Visibility.Visible;
        if (_attempts >= 5) DialogResult = false; // corta tras 5 intentos; se puede volver a abrir con el atajo
    }
}
