namespace Nocturnal.Core;

public static class NetworkFailoverPolicy
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    public static bool IsValidSsid(string? ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return false;
        }

        var trimmed = ssid.Trim();
        if (trimmed.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || ch is '"' or '&' or '|' or ';' or '`')
            {
                return false;
            }
        }

        return true;
    }

    public static bool ShouldAttemptFailover(
        bool isOnline,
        ref int consecutiveOfflineTicks,
        DateTimeOffset utcNow,
        DateTimeOffset? lastAttemptUtc,
        int offlineThresholdTicks = 2,
        TimeSpan? cooldown = null)
    {
        if (isOnline)
        {
            consecutiveOfflineTicks = 0;
            return false;
        }

        consecutiveOfflineTicks++;
        if (consecutiveOfflineTicks < Math.Max(1, offlineThresholdTicks))
        {
            return false;
        }

        var effectiveCooldown = cooldown ?? DefaultCooldown;
        if (lastAttemptUtc.HasValue && utcNow - lastAttemptUtc.Value < effectiveCooldown)
        {
            return false;
        }

        return true;
    }
}
