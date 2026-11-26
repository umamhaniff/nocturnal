using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Error
}

public sealed record UpdateUiState(
    UpdatePhase Phase,
    Version CurrentVersion,
    UpdateReleaseInfo? Release,
    double DownloadProgress,
    string? Message);

public sealed class AppUpdateService
{
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/umamhaniff/nocturnal/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/umamhaniff/nocturnal/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string _updatesDir;
    private string? _lastNotifiedTag;

    public Version CurrentVersion { get; }
    public UpdateUiState State { get; private set; }

    public event Action<UpdateUiState>? StateChanged;
    public event Action<UpdateReleaseInfo>? NewUpdateDetected;

    public AppUpdateService(string? updatesDir = null)
    {
        var rawVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0);
        CurrentVersion = UpdatePolicy.NormalizeVersion(rawVersion);
        _updatesDir = updatesDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal",
            "updates");

        State = new UpdateUiState(UpdatePhase.Idle, CurrentVersion, null, 0.0, null);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        var rawVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0);
        var ver = UpdatePolicy.NormalizeVersion(rawVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Nocturnal/{ver}");
        return client;
    }

    public void CleanupStaleUpdateFiles()
    {
        try
        {
            if (!Directory.Exists(_updatesDir))
            {
                return;
            }

            foreach (var pattern in new[] { "*.exe", "*.part" })
            {
                foreach (var file in Directory.GetFiles(_updatesDir, pattern))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore files locked by a recently exited installer process.
                    }
                }
            }
        }
        catch
        {
            // Never fail startup over temporary update cleanup.
        }
    }

    public async Task<UpdateUiState> CheckForUpdatesAsync(bool isBackgroundCheck = false, CancellationToken ct = default)
    {
        if (State.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing)
        {
            return State;
        }

        if (!isBackgroundCheck)
        {
            SetState(new UpdateUiState(UpdatePhase.Checking, CurrentVersion, State.Release, 0.0, "Checking for updates..."));
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await Http.GetAsync(LatestReleaseApiUrl, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            var release = UpdatePolicy.ParseLatestReleaseJson(json, CurrentVersion);
            if (release is not null)
            {
                SetState(new UpdateUiState(
                    UpdatePhase.Available,
                    CurrentVersion,
                    release,
                    0.0,
                    $"Update {release.TagName} ready"));

                if (!string.Equals(_lastNotifiedTag, release.TagName, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNotifiedTag = release.TagName;
                    NewUpdateDetected?.Invoke(release);
                }
            }
            else
            {
                SetState(new UpdateUiState(UpdatePhase.UpToDate, CurrentVersion, null, 0.0, "Up to date"));
            }
        }
        catch
        {
            if (!isBackgroundCheck)
            {
                SetState(new UpdateUiState(
                    UpdatePhase.Error,
                    CurrentVersion,
                    State.Release,
                    0.0,
                    "Could not check for updates"));
            }
        }

        return State;
    }

    public async Task<bool> DownloadAndInstallAsync(
        Action prepareForExit,
        Action finalizeExit,
        CancellationToken ct = default)
    {
        if (State.Phase is UpdatePhase.Downloading or UpdatePhase.Installing)
        {
            return false;
        }

        var release = State.Release;
        if (release is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(release.InstallerDownloadUrl))
        {
            OpenUrl(release.ReleaseUrl);
            return false;
        }

        var safeName = Path.GetFileName(release.InstallerFileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"Nocturnal-Setup-{release.Version}.exe";
        }

        Directory.CreateDirectory(_updatesDir);
        var partPath = Path.Combine(_updatesDir, $"{safeName}.part");
        var finalPath = Path.Combine(_updatesDir, safeName);

        try
        {
            SetState(new UpdateUiState(UpdatePhase.Downloading, CurrentVersion, release, 0.0, $"Downloading {release.TagName}..."));

            using var response = await Http.GetAsync(
                release.InstallerDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? release.InstallerSizeBytes ?? -1L;

            using (var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                var lastReportedPercent = -1;

                while (true)
                {
                    var read = await networkStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha256.AppendData(buffer, 0, read);
                    downloaded += read;

                    if (totalBytes > 0)
                    {
                        var fraction = Math.Clamp((double)downloaded / totalBytes, 0.0, 1.0);
                        var percent = (int)(fraction * 100);
                        if (percent >= lastReportedPercent + 2 || percent == 100)
                        {
                            lastReportedPercent = percent;
                            SetState(new UpdateUiState(
                                UpdatePhase.Downloading,
                                CurrentVersion,
                                release,
                                fraction,
                                $"Downloading {percent}%..."));
                        }
                    }
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(release.ExpectedSha256Hex))
                {
                    SafeDeleteFile(partPath);
                    SetState(new UpdateUiState(
                        UpdatePhase.Error,
                        CurrentVersion,
                        release,
                        0.0,
                        "Missing SHA-256 digest — download manually from GitHub"));
                    return false;
                }

                var actualHex = Convert.ToHexString(sha256.GetHashAndReset());
                if (!string.Equals(actualHex, release.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    SafeDeleteFile(partPath);
                    SetState(new UpdateUiState(
                        UpdatePhase.Error,
                        CurrentVersion,
                        release,
                        0.0,
                        "Installer checksum mismatch"));
                    return false;
                }
            }

            File.Move(partPath, finalPath, overwrite: true);

            SetState(new UpdateUiState(
                UpdatePhase.Installing,
                CurrentVersion,
                release,
                1.0,
                "Launching installer..."));

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var isPerMachine = !string.IsNullOrWhiteSpace(programFiles)
                && AppContext.BaseDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase);

            Process.Start(new ProcessStartInfo
            {
                FileName = finalPath,
                Arguments = UpdatePolicy.BuildInstallerArguments(isPerMachine),
                UseShellExecute = true
            });

            prepareForExit();
            finalizeExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetState(new UpdateUiState(
                UpdatePhase.Error,
                CurrentVersion,
                release,
                0.0,
                "Update cancelled by user"));
            return false;
        }
        catch
        {
            SafeDeleteFile(partPath);
            SetState(new UpdateUiState(
                UpdatePhase.Error,
                CurrentVersion,
                release,
                0.0,
                "Update download failed"));
            return false;
        }
    }

    private void SetState(UpdateUiState next)
    {
        State = next;
        StateChanged?.Invoke(next);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failure.
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch failure if no default browser is registered.
        }
    }
}
