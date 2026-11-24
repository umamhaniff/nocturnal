using System.Text.Json;

namespace Nocturnal.Core;

public static class UpdatePolicy
{
    private static readonly HashSet<string> TrustedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    public static Version NormalizeVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    public static bool TryParseTagVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var raw = tag.Trim();
        if (raw.StartsWith('v') || raw.StartsWith('V'))
        {
            raw = raw[1..];
        }

        var plusIndex = raw.IndexOf('+');
        if (plusIndex >= 0)
        {
            raw = raw[..plusIndex];
        }

        if (raw.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0
            || !int.TryParse(parts[1], out var minor) || minor < 0)
        {
            return false;
        }

        var build = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out build) || build < 0))
        {
            return false;
        }

        version = new Version(major, minor, build);
        return true;
    }

    public static bool IsNewerVersion(Version current, Version latest) =>
        NormalizeVersion(latest) > NormalizeVersion(current);

    public static bool IsTrustedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TrustedDownloadHosts.Contains(uri.Host);
    }

    public static UpdateReleaseInfo? ParseLatestReleaseJson(string json, Version currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var tagName = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
            ? tagProp.GetString()
            : null;

        if (!TryParseTagVersion(tagName, out var latestVersion)
            || !IsNewerVersion(currentVersion, latestVersion))
        {
            return null;
        }

        var releaseUrl = root.TryGetProperty("html_url", out var htmlProp) && htmlProp.ValueKind == JsonValueKind.String
            ? htmlProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            releaseUrl = $"https://github.com/umamhaniff/nocturnal/releases/tag/{tagName}";
        }

        var releaseNotes = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
            ? bodyProp.GetString()
            : null;

        string? installerUrl = null;
        string? installerName = null;
        long? installerSize = null;
        string? sha256Hex = null;

        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(name)
                    || !name.StartsWith("Nocturnal-Setup-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawUrl = asset.TryGetProperty("browser_download_url", out var dlProp) && dlProp.ValueKind == JsonValueKind.String
                    ? dlProp.GetString()
                    : null;

                installerName = Path.GetFileName(name);
                installerUrl = IsTrustedDownloadUrl(rawUrl) ? rawUrl : null;

                if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize) && parsedSize > 0)
                {
                    installerSize = parsedSize;
                }

                if (asset.TryGetProperty("digest", out var digestProp) && digestProp.ValueKind == JsonValueKind.String)
                {
                    sha256Hex = TryExtractSha256Hex(digestProp.GetString());
                }

                break;
            }
        }

        return new UpdateReleaseInfo(
            latestVersion,
            tagName!,
            releaseUrl,
            installerUrl,
            installerName,
            installerSize,
            sha256Hex,
            releaseNotes);
    }

    public static string BuildInstallerArguments(bool isPerMachineInstall) =>
        isPerMachineInstall
            ? "/VERYSILENT /NORESTART /CLOSEAPPLICATIONS /ALLUSERS /RELAUNCH=1"
            : "/VERYSILENT /NORESTART /CLOSEAPPLICATIONS /CURRENTUSER /RELAUNCH=1";

    private static string? TryExtractSha256Hex(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var trimmed = digest.Trim();
        const string prefix = "sha256:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = trimmed[prefix.Length..].Trim();
        if (hex.Length != 64)
        {
            return null;
        }

        foreach (var ch in hex)
        {
            var isHex = (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return null;
            }
        }

        return hex;
    }
}
