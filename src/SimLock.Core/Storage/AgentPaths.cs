namespace SimLock.Core.Storage;

/// <summary>Dónde guarda cosas el agente. En Windows: %LOCALAPPDATA%\SimLock.</summary>
public sealed class AgentPaths(string root)
{
    public string Root { get; } = root;
    public string Config => Path.Combine(Root, "config.json");
    public string Token => Path.Combine(Root, "device.token");
    public string Seen => Path.Combine(Root, "seen.json");
    public string Events => Path.Combine(Root, "events-queue.json");
    public string LastState => Path.Combine(Root, "last-state.json");
    public string Logs => Path.Combine(Root, "logs");

    public static AgentPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimLock"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}

/// <summary>Token en un archivo plano (solo para la herramienta de consola / pruebas, NO para el agente en Windows).</summary>
public sealed class PlainFileSecretStore(string path) : ISecretStore
{
    public string? ReadToken() => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    public void WriteToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void DeleteToken()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
