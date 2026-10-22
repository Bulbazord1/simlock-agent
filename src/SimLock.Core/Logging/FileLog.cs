using System.Globalization;
using System.Text;

namespace SimLock.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message, Exception? ex = null);
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// Log local rotativo: un archivo por día (simlock-AAAA-MM-DD.log), se conservan los últimos
/// <see cref="KeepDays"/> días y cada archivo se corta en <see cref="MaxBytesPerFile"/>.
/// No escribe datos personales: quien loguea es responsable de no pasar nombres de clientes.
/// </summary>
public sealed class FileLog : ILog
{
    public const int KeepDays = 7;
    public const long MaxBytesPerFile = 5 * 1024 * 1024;

    private readonly string _dir;
    private readonly bool _alsoConsole;
    private readonly object _gate = new();
    private string? _currentDay;

    public FileLog(string dir, bool alsoConsole = false)
    {
        _dir = dir;
        _alsoConsole = alsoConsole;
        Directory.CreateDirectory(dir);
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (ex is not null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        var text = line.ToString();

        if (_alsoConsole) Console.Error.WriteLine(text);

        lock (_gate)
        {
            try
            {
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != _currentDay)
                {
                    _currentDay = day;
                    Prune();
                }
                var path = Path.Combine(_dir, $"simlock-{day}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytesPerFile) return; // tope diario alcanzado
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        try
        {
            var files = Directory.GetFiles(_dir, "simlock-*.log").OrderDescending().Skip(KeepDays);
            foreach (var f in files) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message, Exception? ex = null) { }
    public void Error(string message, Exception? ex = null) { }
}
