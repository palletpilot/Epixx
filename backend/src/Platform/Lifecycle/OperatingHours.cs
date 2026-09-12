using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Lifecycle;

/// <summary>
/// Parses Tenant.OperatingHours. Supported shapes:
/// JSON {"open":"06:00","close":"22:00","days":[1,2,3,4,5,6]} (Mon=1..Sun=7),
/// or null → default 06:00–22:00 Mon–Sat Europe/Stockholm.
/// night_shift tenants treat "closed window start" as next 06:00 local.
/// </summary>
public static class OperatingHours
{
    public static readonly TimeZoneInfo Stockholm =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Stockholm");

    public static DateTimeOffset NextClosedWindowStart(
        TenantHours hours,
        DateTimeOffset afterUtc)
    {
        if (hours.NightShift)
        {
            return NextLocalTime(afterUtc, new TimeOnly(6, 0));
        }

        var local = TimeZoneInfo.ConvertTime(afterUtc, Stockholm);
        // Search up to 8 days for the next close instant strictly after afterUtc.
        for (var day = 0; day < 8; day++)
        {
            var date = DateOnly.FromDateTime(local.DateTime.Date.AddDays(day));
            if (!hours.OpenDays.Contains(IsoDay(date)))
            {
                // Whole day closed: closed window starts at local midnight of that day,
                // but only if that instant is after afterUtc.
                var midnight = LocalToUtc(date, TimeOnly.MinValue);
                if (midnight > afterUtc)
                {
                    return midnight;
                }

                continue;
            }

            var close = LocalToUtc(date, hours.Close);
            if (close > afterUtc)
            {
                return close;
            }
        }

        // Fallback: tomorrow 22:00
        return NextLocalTime(afterUtc, hours.Close);
    }

    public static TenantHours Parse(string? operatingHoursJson, bool nightShift)
    {
        if (string.IsNullOrWhiteSpace(operatingHoursJson))
        {
            return TenantHours.Default(nightShift);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<HoursDto>(operatingHoursJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (dto is null)
            {
                return TenantHours.Default(nightShift);
            }

            var open = TimeOnly.ParseExact(dto.Open ?? "06:00", "HH:mm", CultureInfo.InvariantCulture);
            var close = TimeOnly.ParseExact(dto.Close ?? "22:00", "HH:mm", CultureInfo.InvariantCulture);
            var days = dto.Days is { Length: > 0 }
                ? dto.Days.ToHashSet()
                : new HashSet<int> { 1, 2, 3, 4, 5, 6 };
            return new TenantHours(open, close, days, nightShift);
        }
        catch (Exception)
        {
            return TenantHours.Default(nightShift);
        }
    }

    private static DateTimeOffset NextLocalTime(DateTimeOffset afterUtc, TimeOnly time)
    {
        var local = TimeZoneInfo.ConvertTime(afterUtc, Stockholm);
        var date = DateOnly.FromDateTime(local.DateTime);
        var candidate = LocalToUtc(date, time);
        if (candidate > afterUtc)
        {
            return candidate;
        }

        return LocalToUtc(date.AddDays(1), time);
    }

    private static DateTimeOffset LocalToUtc(DateOnly date, TimeOnly time)
    {
        var unspecified = date.ToDateTime(time, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, Stockholm);
    }

    private static int IsoDay(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    private sealed record HoursDto(
        [property: JsonPropertyName("open")] string? Open,
        [property: JsonPropertyName("close")] string? Close,
        [property: JsonPropertyName("days")] int[]? Days);

    public sealed record TenantHours(TimeOnly Open, TimeOnly Close, HashSet<int> OpenDays, bool NightShift)
    {
        public static TenantHours Default(bool nightShift) =>
            new(new TimeOnly(6, 0), new TimeOnly(22, 0), [1, 2, 3, 4, 5, 6], nightShift);
    }
}
