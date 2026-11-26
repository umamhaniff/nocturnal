using System.Net.Http;
using System.Text;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public sealed class HttpAlertNotifier : IAlertNotifier
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public void Notify(AppConfig config, WebhookAlertEvent alert, bool waitForDelivery = false)
    {
        if (!config.AlertsEnabled || !WebhookPolicy.IsValidWebhookUrl(config.AlertWebhookUrl))
        {
            return;
        }

        var url = config.AlertWebhookUrl.Trim();
        var payload = WebhookPolicy.BuildJsonPayload(alert, url);

        if (waitForDelivery)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                Task.Run(() => SendPayloadAsync(url, payload, cts.Token)).Wait(TimeSpan.FromMilliseconds(3200));
            }
            catch
            {
                // Bounded delivery attempt must never prevent sleep or power restoration.
            }
            return;
        }

        _ = Task.Run(() => SendPayloadAsync(url, payload, CancellationToken.None));
    }

    private static async Task SendPayloadAsync(string url, string payload, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch
        {
            // Alert failures must never disrupt power restoration or UI loops.
        }
    }

    public async Task<(bool Ok, string Message)> SendTestAlertAsync(AppConfig config, CancellationToken ct = default)
    {
        if (!WebhookPolicy.IsValidWebhookUrl(config.AlertWebhookUrl))
        {
            return (false, "Enter a valid https:// webhook URL");
        }

        var url = config.AlertWebhookUrl.Trim();
        var payload = WebhookPolicy.BuildJsonPayload(
            new WebhookAlertEvent(
                "Test",
                "Test Alert",
                "Nocturnal webhook alerts are connected.",
                DateTimeOffset.UtcNow,
                null,
                0),
            url);

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, $"Sent ({WebhookPolicy.DetectProvider(url)})")
                : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch
        {
            return (false, "Webhook request failed");
        }
    }
}
