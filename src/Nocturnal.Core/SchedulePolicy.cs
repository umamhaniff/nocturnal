using System.Globalization;

namespace Nocturnal.Core;

public static class SchedulePolicy
{
    public static IReadOnlyList<SessionProfile> DefaultProfiles { get; } =
    [
        new("AI Overnight", 240, ClamshellEnabled: true, AgentFocusEnabled: true, SelectedAppMode.DeepPause, KeepDisplayAwake: false, AutoSleepOnCompletion: true),
        new("Build / Scraper", 120, ClamshellEnabled: true, AgentFocusEnabled: true, SelectedAppMode.Off, KeepDisplayAwake: false, AutoSleepOnCompletion: true),
        new("Presentation", 90, ClamshellEnabled: false, AgentFocusEnabled: false, SelectedAppMode.Off, KeepDisplayAwake: true, AutoSleepOnCompletion: false)
    ];

    public static SessionProfile? FindProfile(string? nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
        {
            return null;
        }

        var key = nameOrAlias.Trim();
        foreach (var profile in DefaultProfiles)
        {
            if (string.Equals(profile.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return key.ToLowerInvariant() switch
        {
            "ai" or "overnight" or "night" or "ai-overnight" or "ainight" => DefaultProfiles[0],
            "build" or "scraper" or "build-scraper" => DefaultProfiles[1],
            "present" or "presentation" or "demo" => DefaultProfiles[2],
            _ => null
        };
    }

    public static bool TryParseTimeOfDay(string? hhmm, out TimeSpan timeOfDay)
    {
        timeOfDay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(hhmm))
        {
            return false;
        }

        return TimeSpan.TryParseExact(hhmm.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out timeOfDay)
            && timeOfDay >= TimeSpan.Zero
            && timeOfDay < TimeSpan.FromDays(1);
    }

    public static bool ShouldTriggerNightShift(
        AppConfig config,
        DateTimeOffset localNow,
        bool isSessionActive,
        bool isOnAcPower,
        DateOnly? lastTriggeredDate)
    {
        if (!config.NightShiftEnabled || isSessionActive)
        {
            return false;
        }

        if (config.NightShiftRequireAcPower && !isOnAcPower)
        {
            return false;
        }

        if (config.NightShiftDurationMinutes <= 0)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        if (lastTriggeredDate == today)
        {
            return false;
        }

        if (!TryParseTimeOfDay(config.NightShiftStartTime, out var start))
        {
            return false;
        }

        var current = localNow.TimeOfDay;
        var windowEnd = start.Add(TimeSpan.FromMinutes(5));
        return current >= start && current < windowEnd;
    }
}
