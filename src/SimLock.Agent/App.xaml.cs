using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SimLock.Core.Api;
using SimLock.Core.Logging;
using SimLock.Core.Runtime;
using SimLock.Core.Storage;

namespace SimLock.Agent;

public partial class App : Application
{
    internal AgentPaths Paths { get; private set; } = null!;
    internal ILog Log { get; private set; } = NullLog.Instance;
    internal ConfigStore ConfigStore { get; private set; } = null!;
    internal AgentConfig Config { get; private set; } = null!;
    internal ISecretStore Secrets { get; private set; } = null!;
    internal AgentRuntime Runtime { get; private set; } = null!;
    internal string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private SimLockApiClient? _api;
    private EventQueue _queue = null!;
    private SeenStore _seen = null!;
    private Mutex? _singleInstance;
    private OverlayWindow? _overlay;
    private SetupWindow? _setup;
    private bool _pinPromptOpen;

    /// <summary>Mientras el personal configura, el overlay se oculta para no tapar la ventana de configuración.</summary>
    internal bool IsConfiguring => _setup is not null || _pinPromptOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Un solo proceso por usuario. Si ya hay uno corriendo, abrirlo de nuevo le pide que muestre la configuración.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\SimLockAgent", out var createdNew);
        if (!createdNew)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        Paths = AgentPaths.Default();
        Paths.EnsureCreated();
        Log = new FileLog(Paths.Logs);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Excepción no controlada", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Tarea con error no observado", ev.Exception); ev.SetObserved(); };

        ConfigStore = new ConfigStore(Paths.Config);
        Config = ConfigStore.Load();
        Secrets = new DpapiSecretStore(Paths.Token);
        _queue = new EventQueue(Paths.Events);
        _seen = new SeenStore(Paths.Seen);

        BuildRuntime();
        Autostart.Ensure(Log);
        ListenForSecondLaunch();

        _overlay = new OverlayWindow(this);
        _overlay.Initialize();
        Runtime.Start();

        if (!Runtime.IsPaired)
            Dispatcher.InvokeAsync(OpenSetup, DispatcherPriority.ApplicationIdle);
    }

    private void BuildRuntime()
    {
        if (!Uri.TryCreate(Config.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            Log.Warn($"URL de servidor inválida en la configuración ('{Config.BaseUrl}'); se usa la de producción.");
            Config.BaseUrl = AgentConfig.DefaultBaseUrl;
            baseUri = new Uri(AgentConfig.DefaultBaseUrl + "/");
        }
        _api = new SimLockApiClient(baseUri, $"SimLock/{Version} (Windows)");
        Runtime = new AgentRuntime(_api, Secrets, _queue, _seen, Log, Version, Paths.LastState);
        Runtime.Unpaired += () => Dispatcher.InvokeAsync(() =>
        {
            Config.BayId = Config.BayName = Config.VenueId = Config.VenueName = null;
            Config.PairedAt = null;
            SaveConfig();
        });
    }

    /// <summary>Cuando cambia la URL del servidor hay que rearmar el cliente.</summary>
    internal async Task RebuildRuntimeAsync()
    {
        var old = Runtime;
        await old.StopAsync();
        old.Dispose();
        _api?.Dispose();
        BuildRuntime();
        Runtime.Start();
    }

    internal void SaveConfig()
    {
        try { ConfigStore.Save(Config); }
        catch (Exception ex) { Log.Error("No se pudo guardar la configuración", ex); }
    }

    /// <summary>Atajo Ctrl+Alt+Shift+S: pide PIN (si hay) y abre la configuración.</summary>
    internal void RequestSetup()
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        if (_pinPromptOpen) return;
        if (Config.HasPin)
        {
            _pinPromptOpen = true;
            var ok = new PinWindow(Config).ShowDialog() == true;
            _pinPromptOpen = false;
            if (!ok)
            {
                Log.Warn("PIN incorrecto o cancelado al abrir la configuración.");
                return;
            }
        }
        OpenSetup();
    }

    private void OpenSetup()
    {
        if (_setup is not null) { _setup.Activate(); return; }
        _setup = new SetupWindow(this);
        _setup.Closed += (_, _) => _setup = null;
        _setup.Show();
        _setup.Activate();
    }

    internal void ExitAgent()
    {
        Log.Info("Salida manual desde la configuración.");
        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // El overlay no debe cerrarse por un error de dibujo.
        Log.Error("Error en la interfaz", e.Exception);
        e.Handled = true;
    }

    private const string OpenSetupEventName = @"Local\SimLockAgent-OpenSetup";
    private EventWaitHandle? _openSetupSignal;
    private RegisteredWaitHandle? _openSetupWait;

    /// <summary>Instancia que ya corre: cuando alguien vuelve a abrir SimLock.exe, mostrar la configuración (con PIN).</summary>
    private void ListenForSecondLaunch()
    {
        try
        {
            _openSetupSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSetupEventName);
            _openSetupWait = ThreadPool.RegisterWaitForSingleObject(_openSetupSignal, (_, _) =>
            {
                Log.Info("SimLock se abrió de nuevo: se muestra la configuración.");
                Dispatcher.InvokeAsync(RequestSetup);
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo escuchar la segunda apertura de SimLock", ex);
        }
    }

    /// <summary>Segunda instancia: avisa a la que ya corre y le cede el primer plano.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            // Quien abre el .exe tiene el foco: se lo cede a la instancia que va a mostrar el PIN.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            if (EventWaitHandle.TryOpenExisting(OpenSetupEventName, out var ev))
            {
                using (ev) ev.Set();
            }
        }
        catch (Exception)
        {
            // Si no se puede avisar, simplemente se cierra como antes.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _overlay?.Shutdown();
            Runtime?.StopAsync().Wait(TimeSpan.FromSeconds(2));
            Runtime?.Dispose();
            _api?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al cerrar", ex);
        }
        _openSetupWait?.Unregister(null);
        _openSetupSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
