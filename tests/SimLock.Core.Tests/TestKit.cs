using System.Reflection;
using SimLock.Core.Time;

namespace SimLock.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

/// <summary>Mini corredor de pruebas sin dependencias (el entorno de build no siempre tiene NuGet).</summary>
public static class Runner
{
    public static async Task<int> RunAll(string? filter)
    {
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        var failed = 0;
        foreach (var t in tests)
        {
            var name = $"{t.DeclaringType!.Name}.{t.Name}";
            try
            {
                var r = t.Invoke(null, null);
                if (r is Task task) await task;
                Console.WriteLine($"  ok   {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
                Console.WriteLine($"  FAIL {name}\n       {inner.GetType().Name}: {inner.Message}");
            }
        }
        Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} pruebas OK");
        return failed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool cond, string what)
    {
        if (!cond) throw new Exception($"Se esperaba verdadero: {what}");
    }

    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} esperado <{expected}> pero fue <{actual}>");
    }

    public static async Task<TEx> Throws<TEx>(Func<Task> action) where TEx : Exception
    {
        try { await action(); }
        catch (TEx ex) { return ex; }
        throw new Exception($"Se esperaba {typeof(TEx).Name}");
    }
}

/// <summary>Reloj monotónico manejado a mano.</summary>
public sealed class FakeMonotonic : IMonotonicClock
{
    private long _ticks = 1_000_000;
    public long GetTimestamp() => _ticks;
    public TimeSpan GetElapsed(long from) => TimeSpan.FromTicks(_ticks - from);
    public void Advance(TimeSpan d) => _ticks += d.Ticks;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simlock-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
