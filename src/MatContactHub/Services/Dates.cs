using System.Globalization;
using MatContactHub.Localization;

namespace MatContactHub.Services;

/// <summary>Dates as a person reads them, in the language of the interface (English or German), and "today" in the time zone of a person.</summary>
public static class Dates
{
    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Berlin", out var berlin) ? berlin : TimeZoneInfo.Utc;
    }

    public static bool IsZone(string? id) => !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    public static DateTime Local(DateTime utc, string? zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(zone));

    public static DateOnly Today(string? zone) => DateOnly.FromDateTime(Local(DateTime.UtcNow, zone));

    /// <summary>A moment (UTC) in the zone of the person: "19.10.2026 14:05" / "19/10/2026 14:05".</summary>
    public static string Stamp(DateTime? utc, string? zone)
        => utc is null ? string.Empty : Local(utc.Value, zone).ToString(Languages.IsGerman ? "dd.MM.yyyy HH:mm" : "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The time zones a person can choose (the IANA ids of the system).</summary>
    public static IReadOnlyList<string> ZoneIds { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(z => z.Id).Where(id => id.Contains('/', StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
}
