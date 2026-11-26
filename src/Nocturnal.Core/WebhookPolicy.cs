using System.Net;
using System.Text.Json;

namespace Nocturnal.Core;

public enum WebhookProvider
{
    Generic,
    Discord,
    Telegram
}

public static class WebhookPolicy
{
    public static bool IsValidWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return false;
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.OrdinalIgnoreCase)
            || host == "::1")
        {
            return false;
        }

        return true;
    }

    public static WebhookProvider DetectProvider(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return WebhookProvider.Generic;
        }

        var host = uri.Host;
        var path = uri.AbsolutePath;

        if ((host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase))
            && path.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            return WebhookProvider.Discord;
        }

        if (host.Equals("api.telegram.org", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/sendMessage", StringComparison.OrdinalIgnoreCase))
        {
            return WebhookProvider.Telegram;
        }

        return WebhookProvider.Generic;
    }

    public static string BuildJsonPayload(WebhookAlertEvent evt, string webhookUrl)
    {
        var provider = DetectProvider(webhookUrl);
        var formatted = FormatSummaryLine(evt);

        switch (provider)
        {
            case WebhookProvider.Discord:
                return JsonSerializer.Serialize(new { content = formatted });

            case WebhookProvider.Telegram:
                var chatId = TryExtractQueryValue(webhookUrl, "chat_id");
                return string.IsNullOrWhiteSpace(chatId)
                    ? JsonSerializer.Serialize(new { text = formatted })
                    : JsonSerializer.Serialize(new { chat_id = chatId, text = formatted });

            default:
                return JsonSerializer.Serialize(new
                {
                    app = "Nocturnal",
                    eventType = evt.EventType,
                    title = evt.Title,
                    message = evt.Message,
                    timestampUtc = evt.TimestampUtc,
                    batteryPercent = evt.BatteryPercent,
                    reclaimedRamMb = evt.ReclaimedRamMb
                });
        }
    }

    private static string FormatSummaryLine(WebhookAlertEvent evt)
    {
        var details = new List<string>();
        if (evt.BatteryPercent.HasValue)
        {
            details.Add($"Battery: {evt.BatteryPercent}%");
        }
        if (evt.ReclaimedRamMb > 0)
        {
            details.Add($"RAM trimmed: ~{evt.ReclaimedRamMb:F1} MB");
        }

        var suffix = details.Count > 0 ? $" ({string.Join(" | ", details)})" : "";
        return $"[Nocturnal] {evt.Title}: {evt.Message}{suffix}";
    }

    private static string? TryExtractQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var k = WebUtility.UrlDecode(pair[..idx]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.UrlDecode(pair[(idx + 1)..]);
            }
        }

        return null;
    }
}
