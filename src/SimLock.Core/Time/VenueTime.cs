using System.Globalization;

namespace SimLock.Core.Time;

/// <summary>Formatea horas en la zona horaria de la sede (venue.timezone, IANA).</summary>
public static class VenueTime
{
    private static readonly Dictionary<string, TimeZoneInfo?> Cache = new(StringComparer.Ordinal);

    public static TimeZoneInfo Resolve(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return TimeZoneInfo.Local;
        lock (Cache)
        {
            if (!Cache.TryGetValue(ianaId, out var tz))
            {
                tz = TryFind(ianaId);
                Cache[ianaId] = tz;
            }
            return tz ?? TimeZoneInfo.Local;
        }
    }

    private static TimeZoneInfo? TryFind(string ianaId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }

        // Windows sin ICU: convertir el id IANA a id de Windows.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>"HH:mm" en la zona de la sede.</summary>
    public static string FormatHour(DateTimeOffset instant, string? ianaId) =>
        TimeZoneInfo.ConvertTime(instant, Resolve(ianaId)).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Cuenta regresiva "M:SS" o "H:MM:SS". Nunca negativa.</summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var totalSeconds = (long)Math.Ceiling(remaining.TotalSeconds);
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }
}
