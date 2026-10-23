using System.Security.Cryptography;

namespace SimLock.Core.Storage;

/// <summary>Configuración local del equipo. No contiene el token (va aparte, cifrado).</summary>
public sealed class AgentConfig
{
    public const string DefaultBaseUrl = "https://panel.forebooking.com";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Nombre de dispositivo del monitor (ej. \\.\DISPLAY2). Null = monitor principal.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>
    /// Mientras se juega, usar la barra que dibuja el panel (modo compacto de la pantalla web).
    /// Si en una bay no se ve bien, se apaga y se usa el reloj propio de SimLock.
    /// </summary>
    public bool UseWebCompactBar { get; set; } = true;

    // Datos informativos de la vinculación (para la pantalla de configuración).
    public string? BayId { get; set; }
    public string? BayName { get; set; }
    public string? VenueId { get; set; }
    public string? VenueName { get; set; }
    public DateTimeOffset? PairedAt { get; set; }

    // PIN local que protege la configuración. PBKDF2-SHA256.
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }

    public bool HasPin => !string.IsNullOrEmpty(PinHash) && !string.IsNullOrEmpty(PinSalt);
}

public sealed class ConfigStore(string path)
{
    public AgentConfig Load() => JsonFile.Read<AgentConfig>(path) ?? new AgentConfig();
    public void Save(AgentConfig config) => JsonFile.Write(path, config);
}

/// <summary>Guarda el deviceToken. En Windows se implementa con DPAPI.</summary>
public interface ISecretStore
{
    string? ReadToken();
    void WriteToken(string token);
    void DeleteToken();
}

public static class Pin
{
    private const int Iterations = 120_000;
    public const int MinLength = 4;

    public static bool IsValidFormat(string pin) => pin.Length >= MinLength && pin.Length <= 12 && pin.All(char.IsAsciiDigit);

    public static void Set(AgentConfig config, string pin)
    {
        if (!IsValidFormat(pin)) throw new ArgumentException($"El PIN debe tener entre {MinLength} y 12 dígitos.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(16);
        config.PinSalt = Convert.ToBase64String(salt);
        config.PinHash = Convert.ToBase64String(Derive(pin, salt));
    }

    public static bool Verify(AgentConfig config, string pin)
    {
        if (!config.HasPin) return false;
        try
        {
            var salt = Convert.FromBase64String(config.PinSalt!);
            var expected = Convert.FromBase64String(config.PinHash!);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
}
