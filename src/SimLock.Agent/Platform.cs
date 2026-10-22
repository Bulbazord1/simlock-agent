using System.IO;
using System.Text;
using Microsoft.Win32;
using SimLock.Core.Logging;
using SimLock.Core.Storage;

namespace SimLock.Agent;

/// <summary>Token del equipo cifrado con DPAPI (usuario actual). Si se copia el archivo a otra PC no sirve.</summary>
internal sealed class DpapiSecretStore(string path) : ISecretStore
{
    public string? ReadToken()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(Native.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception)
        {
            // Archivo dañado o de otro usuario: equivale a no estar vinculado.
            return null;
        }
    }

    public void WriteToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Native.Protect(Encoding.UTF8.GetBytes(token)));
        File.Move(tmp, path, overwrite: true);
    }

    public void DeleteToken()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

internal sealed record MonitorInfo(string Device, string Label, Native.RECT Bounds, bool IsPrimary, IntPtr Handle)
{
    /// <summary>Escala del monitor (1.0 = 96 dpi).</summary>
    public double Scale
    {
        get
        {
            try
            {
                if (Native.GetDpiForMonitor(Handle, 0, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1.0;
        }
    }
}

internal static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info))
            {
                var primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
                var r = info.rcMonitor;
                var label = $"{info.szDevice.Replace(@"\\.\", "")} — {r.Width}×{r.Height}{(primary ? " (principal)" : "")}";
                list.Add(new MonitorInfo(info.szDevice, label, r, primary, h));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>El monitor configurado; si no existe (se desconectó), el principal.</summary>
    public static MonitorInfo? Resolve(string? device)
    {
        var all = All();
        return all.FirstOrDefault(m => device is not null && string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.IsPrimary)
               ?? all.FirstOrDefault();
    }
}

/// <summary>Arranque automático con Windows (clave Run del usuario).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimLock";

    public static void Ensure(ILog log)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var wanted = $"\"{exe}\"";
            if (!string.Equals(key.GetValue(ValueName) as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, wanted);
                log.Info($"Arranque automático configurado: {wanted}");
            }
        }
        catch (Exception ex)
        {
            log.Warn("No se pudo configurar el arranque automático", ex);
        }
    }
}
