using System.Text.Json;
using Nocturnal.Core;

namespace Nocturnal.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static int Main()
    {
        Run("Start_And_Stop_ManagesAssertions_Clamshell_And_Journal", TestStartAndStop);
        Run("RecoverIfNeeded_RestoresStalePowerState", TestCrashRecovery);
        Run("Tick_ExpiresTimer_And_SleepsWhenConfigured", TestTimerExpiration);
        Run("Tick_BatteryRules_EngageLowPower_ThenSleepAtCritical", TestBatterySafetyRules);
        Run("Tick_ThermalCritical_StopsAndSleeps", TestThermalCritical);
        Run("AddTime_StartsWhenIdle_AndExtendsWhenActive", TestAddTime);
        Run("FocusPolicy_ProtectsSystemAndAiAgents", TestFocusPolicyProtection);
        Run("FocusPolicy_ThrottlesNonAgents_AndAppliesSelectedAppMode", TestFocusPolicyActions);
        Run("FocusPolicy_ProtectsAgentChildTree_LikePlaywrightChrome", TestAgentTreeInheritance);
        Run("FocusPolicy_StopsInheritanceAtExplorer_AndPausesUserAppsAndWebViewChildren", TestShellBoundaryAndDeepPauseTree);
        Run("FocusPolicy_ProtectsWindowsSystemAndDriverPaths", TestProtectedSystemPaths);
        Run("FocusPolicy_ProtectsAgyAndChromeRemoteDesktop", TestAgyAndRemoteDesktopProtection);
        Run("SessionEngine_AgentFocus_EngagesOnStart_AndRestoresOnStop", TestSessionEngineAgentFocus);
        Run("RecoverIfNeeded_ResumesJournaledSuspendedProcesses", TestCrashRecoveryResumesSuspendedProcesses);
        Run("Start_WritesJournalBeforeApplyingClamshell", TestStartWritesJournalBeforeApplyingClamshell);
        Run("UpdateConfig_SyncsAutoSleepOnActiveSession", TestUpdateConfigSyncsAutoSleep);
        Run("UpdatePolicy_ParsesReleaseJson_AndDetectsNewerVersion", TestUpdatePolicyParsesNewerRelease);
        Run("UpdatePolicy_IgnoresSameOlderOrPrereleaseVersions", TestUpdatePolicyIgnoresSameOlderOrPrerelease);
        Run("UpdatePolicy_RejectsUntrustedDownloadHosts", TestUpdatePolicyRejectsUntrustedHosts);
        Run("UpdatePolicy_BuildsPerUserAndPerMachineSilentRelaunchArgs", TestUpdatePolicyInstallerArgs);
        Run("TurnOffDisplay_DisablesDisplayAssertion_AndCallsPower", TestTurnOffDisplay);
        Run("Stop_RecordsSessionHistory_AndCapsAtMaxEntries", TestSessionHistoryRecording);
        Run("WatchProcess_StopsAfterGracePeriod_WhenProcessExits", TestWatchProcessExitsAfterGrace);
        Run("WatchProcess_ResetsGraceTimer_IfProcessRestartsWithinGrace", TestWatchProcessResetsGraceOnRestart);
        Run("WatchProcess_RespectsMaxDurationCeiling", TestWatchProcessMaxDuration);
        Run("IpcCommandParser_ParsesStartExtendWatchAndFlags", TestIpcCommandParserValid);
        Run("IpcCommandParser_RejectsInvalidDurationsAndUnknownFlags", TestIpcCommandParserInvalid);
        Run("IpcProtocol_SerializesStatusAndHistoryPayloadsCleanly", TestIpcProtocolJsonRoundTrip);
        Run("WebhookPolicy_ValidatesHttps_DetectsProviders_AndBuildsPayloads", TestWebhookPolicyValidationAndPayloads);
        Run("SessionEngine_DispatchesAlerts_OnSafetyGuardAndCompletion", TestSessionEngineWebhookAlerts);
        Run("NetworkFailoverPolicy_SanitizesSsids_DebouncesAndEnforcesCooldown", TestNetworkFailoverPolicy);
        Run("SessionEngine_WifiFailover_TriggersAfterOfflineThreshold", TestSessionEngineWifiFailover);
        Run("SchedulePolicy_NightShift_TriggersOncePerDayWithinWindowOnAc", TestNightShiftScheduleTrigger);
        Run("SessionEngine_NightShift_AutoStartsInTick_AndAppliesProfile", TestSessionEngineNightShiftAndProfile);
        Run("IpcCommandParser_ParsesProfileAndMultiWordNames", TestIpcCommandParserProfile);
        Run("FocusPolicy_IsDefaultProtected_FlagsSystemAndAgentsOnly", TestFocusPolicyDefaultProtectedHelper);
        Run("AppConfig_BackwardCompatible_DeserializesOlderJsonCleanly", TestAppConfigBackwardCompatibility);
        Run("SchedulePolicy_FindProfile_SupportsExactAndAliases", TestScheduleProfileLookup);

        Console.WriteLine($"\nResults: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static (SessionEngine engine, FakePowerController power, FakeSystemMonitor monitor, InMemoryJournalStore journal, FakeClock clock, FakeAgentFocusController focus) CreateHarness()
    {
        var (engine, power, monitor, journal, clock, focus, _, _) = CreateFullHarness();
        return (engine, power, monitor, journal, clock, focus);
    }

    private static (SessionEngine engine, FakePowerController power, FakeSystemMonitor monitor, InMemoryJournalStore journal, FakeClock clock, FakeAgentFocusController focus, FakeProcessWatcher watcher, InMemorySessionHistoryStore history) CreateFullHarness(
        TimeSpan? watchGracePeriod = null,
        IAlertNotifier? alertNotifier = null,
        INetworkFailoverController? networkFailover = null)
    {
        var callLog = new List<string>();
        var power = new FakePowerController(callLog);
        var monitor = new FakeSystemMonitor();
        var journal = new InMemoryJournalStore(callLog);
        var config = new InMemoryConfigStore();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var focus = new FakeAgentFocusController();
        var watcher = new FakeProcessWatcher();
        var history = new InMemorySessionHistoryStore();
        var engine = new SessionEngine(power, monitor, journal, config, clock, focus, watcher, history, watchGracePeriod, alertNotifier, networkFailover);
        return (engine, power, monitor, journal, clock, focus, watcher, history);
    }

    private static void TestStartAndStop()
    {
        var (engine, power, _, journal, _, _) = CreateHarness();
        engine.Start(TimeSpan.FromMinutes(30));

        Assert(engine.GetStatus().IsActive, "Session should be active");
        Assert(power.AwakeEnabled, "Awake assertion should be enabled");
        Assert(power.ClamshellActive, "Clamshell should be enabled");
        Assert(journal.Current is not null, "Journal should be persisted");

        engine.Stop(StopReason.Manual);

        Assert(!engine.GetStatus().IsActive, "Session should be stopped");
        Assert(!power.AwakeEnabled, "Awake assertion should be disabled");
        Assert(!power.ClamshellActive, "Clamshell should be restored");
        Assert(journal.Current is null, "Journal should be cleared");
        Assert(!power.Slept, "Manual stop should not sleep");
    }

    private static void TestCrashRecovery()
    {
        var (engine, power, _, journal, clock, focus) = CreateHarness();
        var backup = new LidActionBackup(Guid.NewGuid(), 1, 1);
        journal.Save(new JournalEntry(
            Guid.NewGuid(),
            clock.UtcNow.AddHours(-2),
            clock.UtcNow.AddHours(-1),
            ClamshellEnabled: true,
            LidBackup: backup,
            LowPowerEngagedByApp: true,
            AutoSleepOnEnd: true));

        var recovered = engine.RecoverIfNeeded();

        Assert(recovered, "Should report crash recovery");
        Assert(power.RestoredBackup == backup, "Should restore original lid backup");
        Assert(power.PowerSaverState == false, "Should restore power saver off");
        Assert(focus.RestoreCount == 1, "Should restore any prior focus throttling on crash recovery");
        Assert(journal.Current is null, "Should clear stale journal");
        Assert(engine.GetStatus().LastStopReason == StopReason.CrashRecovery, "Should record CrashRecovery");
    }

    private static void TestTimerExpiration()
    {
        var (engine, power, _, _, clock, _) = CreateHarness();
        engine.Start(TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(10));
        engine.Tick();

        Assert(!engine.GetStatus().IsActive, "Session should expire");
        Assert(engine.GetStatus().LastStopReason == StopReason.TimerExpired, "Should record TimerExpired");
        Assert(power.Slept, "Should sleep on completion");
    }

    private static void TestBatterySafetyRules()
    {
        var (engine, power, monitor, _, clock, _) = CreateHarness();
        engine.Start(TimeSpan.FromHours(1));

        monitor.Snapshot = new SystemPowerSnapshot(false, 35, true, ThermalLevel.Normal, 50);
        clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert(engine.GetStatus().IsActive, "Should stay active at 35%");
        Assert(power.PowerSaverState == true, "Should engage power saver below 40%");

        monitor.Snapshot = new SystemPowerSnapshot(false, 10, true, ThermalLevel.Normal, 50);
        clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert(!engine.GetStatus().IsActive, "Should stop at 10% critical battery");
        Assert(engine.GetStatus().LastStopReason == StopReason.BatteryCritical, "Should record BatteryCritical");
        Assert(power.Slept, "Should sleep at critical battery");
    }

    private static void TestThermalCritical()
    {
        var (engine, power, monitor, _, clock, _) = CreateHarness();
        engine.Start(TimeSpan.FromHours(1));

        monitor.Snapshot = new SystemPowerSnapshot(true, 100, true, ThermalLevel.Critical, 96.5);
        clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert(!engine.GetStatus().IsActive, "Should stop at critical temperature");
        Assert(engine.GetStatus().LastStopReason == StopReason.ThermalCritical, "Should record ThermalCritical");
        Assert(power.Slept, "Should sleep at critical temperature");
    }

    private static void TestAddTime()
    {
        var (engine, _, _, _, _, _) = CreateHarness();
        engine.AddTime(TimeSpan.FromMinutes(30));
        Assert(engine.GetStatus().IsActive, "AddTime when idle should start session");
        Assert(engine.GetStatus().TotalDuration == TimeSpan.FromMinutes(30), "Initial duration should be 30m");

        engine.AddTime(TimeSpan.FromMinutes(15));
        Assert(engine.GetStatus().Remaining == TimeSpan.FromMinutes(45), "Remaining should extend to 45m");
    }

    private static void TestFocusPolicyProtection()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusSelectedAppMode = SelectedAppMode.GracefulClose,
            FocusAppsToClose = ["Discord"],
            CustomAgentWhitelist = ["my-custom-agent.exe"]
        };

        var nodes = new List<ProcessNode>
        {
            new(10, 4, "svchost", 0, @"C:\Windows\System32\svchost.exe"),
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(21, 20, "claude", 1, @"C:\Users\dev\claude.exe"),
            new(22, 20, "antigravity", 1, @"C:\Users\dev\antigravity.exe"),
            new(23, 20, "node", 1, @"C:\nvm4w\node.exe"),
            new(24, 20, "python", 1, @"C:\Python313\python.exe"),
            new(25, 20, "Code", 1, @"C:\VSCode\Code.exe"),
            new(26, 20, "WindowsTerminal", 1, @"C:\WindowsApps\WindowsTerminal.exe"),
            new(27, 20, "my-custom-agent", 1, @"D:\tools\my-custom-agent.exe")
        };

        var actions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        foreach (var node in nodes)
        {
            Assert(actions[node.Pid] == ProcessAction.Protect, $"{node.Name} must be protected");
        }
    }

    private static void TestFocusPolicyActions()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusSelectedAppMode = SelectedAppMode.Off,
            FocusAppsToClose = ["Discord", "Spotify"]
        };

        var nodes = new List<ProcessNode>
        {
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(100, 20, "chrome", 1, @"C:\Program Files\Google\Chrome\chrome.exe"),
            new(101, 20, "Discord", 1, @"C:\Users\dev\Discord.exe"),
            new(102, 20, "spotify.exe", 1, @"C:\Users\dev\Spotify.exe"),
            new(103, 20, "Everything", 1, @"C:\Program Files\Everything\Everything.exe")
        };

        var offActions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(offActions[100] == ProcessAction.ThrottleEcoQos, "Non-agent app should be throttled");
        Assert(offActions[101] == ProcessAction.ThrottleEcoQos, "When SelectedAppMode is Off, Discord should only be throttled");
        Assert(offActions[103] == ProcessAction.ThrottleEcoQos, "Windowless background process in Session 1 should also be throttled");

        config.FocusSelectedAppMode = SelectedAppMode.GracefulClose;
        var closeActions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(closeActions[101] == ProcessAction.GracefulClose, "When mode is GracefulClose, Discord should close gracefully");
        Assert(closeActions[102] == ProcessAction.GracefulClose, "Normalization should handle .exe suffix");
        Assert(closeActions[100] == ProcessAction.ThrottleEcoQos, "Unselected Chrome stays throttled");
    }

    private static void TestAgentTreeInheritance()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusSelectedAppMode = SelectedAppMode.DeepPause,
            FocusAppsToClose = ["chrome"]
        };

        var nodes = new List<ProcessNode>
        {
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(200, 20, "agy", 1, @"C:\Users\dev\agy.exe"),
            new(201, 200, "powershell", 1, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"),
            new(202, 201, "python", 1, @"C:\Python313\python.exe"),
            new(203, 202, "node", 1, @"C:\Python313\playwright\node.exe"),
            new(204, 203, "chrome", 1, @"C:\Program Files\Google\Chrome\chrome.exe"),
            new(205, 200, "osv-scanner", 1, @"C:\Users\dev\.gemini\osv-scanner.exe"),
            new(300, 20, "chrome", 1, @"C:\Program Files\Google\Chrome\chrome.exe")
        };

        var actions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(actions[204] == ProcessAction.Protect, "Playwright/agent child chrome must stay protected");
        Assert(actions[205] == ProcessAction.Protect, "Agent child osv-scanner must stay protected");
        Assert(actions[300] == ProcessAction.DeepPause, "User chrome launched by explorer should obey DeepPause");
    }

    private static void TestShellBoundaryAndDeepPauseTree()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusSelectedAppMode = SelectedAppMode.DeepPause,
            FocusAppsToClose = ["WhatsApp.Root"],
            CustomAppsToManage = ["poni"]
        };

        var nodes = new List<ProcessNode>
        {
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(50, 20, "SearchHost", 1, @"C:\Windows\SystemApps\SearchHost.exe"),
            new(51, 50, "msedgewebview2", 1, @"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe"),
            new(60, 20, "WhatsApp.Root", 1, @"C:\Program Files\WindowsApps\WhatsApp.Root.exe"),
            new(61, 60, "msedgewebview2", 1, @"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe"),
            new(62, 61, "msedgewebview2", 1, @"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe"),
            new(70, 20, "poni", 1, @"C:\Users\dev\poni.exe"),
            new(71, 70, "msedgewebview2", 1, @"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe")
        };

        var actions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(actions[51] == ProcessAction.Protect, "SearchHost WebView2 child must stay protected");
        Assert(actions[60] == ProcessAction.DeepPause, "WhatsApp.Root should be DeepPaused");
        Assert(actions[61] == ProcessAction.DeepPause, "WhatsApp.Root child WebView2 should be DeepPaused");
        Assert(actions[62] == ProcessAction.DeepPause, "WhatsApp.Root grandchild WebView2 should be DeepPaused");
        Assert(actions[70] == ProcessAction.DeepPause, "Custom app poni should be DeepPaused");
        Assert(actions[71] == ProcessAction.DeepPause, "Custom app poni child WebView2 should be DeepPaused");
    }

    private static void TestProtectedSystemPaths()
    {
        var config = new AppConfig { FocusEcoQosEnabled = true };
        var nodes = new List<ProcessNode>
        {
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(80, 20, "ipf_helper", 1, @"C:\WINDOWS\System32\DriverStore\FileRepository\ipf_cpu.inf\ipf_helper.exe"),
            new(81, 20, "ShellHost", 1, @"C:\Windows\System32\ShellHost.exe"),
            new(82, 20, "WidgetBoard", 1, @"C:\Program Files\WindowsApps\WidgetBoard.exe")
        };

        var actions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(actions[80] == ProcessAction.Protect, "DriverStore path must be protected across any OEM");
        Assert(actions[81] == ProcessAction.Protect, "System32 path must be protected");
        Assert(actions[82] == ProcessAction.ThrottleEcoQos, "Non-system WindowsApps widget should be throttled");
    }

    private static void TestAgyAndRemoteDesktopProtection()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusSelectedAppMode = SelectedAppMode.DeepPause,
            FocusAppsToClose = ["Discord"]
        };

        var nodes = new List<ProcessNode>
        {
            new(20, 4, "explorer", 1, @"C:\Windows\explorer.exe"),
            new(90, 20, "agy", 1, @"C:\Users\dev\agy.exe"),
            new(91, 4, "remoting_desktop", 1, @"C:\Program Files (x86)\Google\Chrome Remote Desktop\remoting_desktop.exe"),
            new(92, 4, "tailscaled", 1, @"C:\Program Files\Tailscale\tailscaled.exe")
        };

        var actions = ProcessFocusPolicy.EvaluateTree(nodes, 1, 999, config);
        Assert(actions[90] == ProcessAction.Protect, "agy must be protected");
        Assert(actions[91] == ProcessAction.Protect, "Chrome Remote Desktop desktop capture process must be protected");
        Assert(actions[92] == ProcessAction.Protect, "Tailscale must be protected");
    }

    private static void TestSessionEngineAgentFocus()
    {
        var (engine, _, _, journal, _, focus) = CreateHarness();
        engine.UpdateConfig(c => c.AgentFocusEnabled = true);

        engine.Start(TimeSpan.FromMinutes(30));
        var status = engine.GetStatus();
        Assert(focus.EngageCount == 1, "Focus should engage on session start");
        Assert(status.Focus.IsEngaged, "Status should report focus engaged");
        Assert(status.Focus.ThrottledProcessCount == 4, "Status should expose throttled count");
        Assert(status.Focus.PausedProcessCount == 1, "Status should expose paused count");
        Assert(journal.Current?.SuspendedProcesses?.Count == 1, "Journal should persist suspended PIDs for crash recovery");

        engine.Stop(StopReason.Manual);
        Assert(focus.RestoreCount == 1, "Focus should restore on session stop");
        Assert(!engine.GetStatus().Focus.IsEngaged, "Status should clear focus report after stop");
    }

    private static void TestCrashRecoveryResumesSuspendedProcesses()
    {
        var (engine, _, _, journal, clock, focus) = CreateHarness();
        var paused = new List<SuspendedProcessRecord> { new(4321, clock.UtcNow.AddHours(-2)) };
        journal.Save(new JournalEntry(
            Guid.NewGuid(),
            clock.UtcNow.AddHours(-2),
            clock.UtcNow.AddHours(-1),
            ClamshellEnabled: false,
            LidBackup: null,
            LowPowerEngagedByApp: false,
            AutoSleepOnEnd: false,
            SuspendedProcesses: paused));

        var recovered = engine.RecoverIfNeeded();

        Assert(recovered, "Should report crash recovery");
        Assert(focus.RecoveredRecords.Count == 1 && focus.RecoveredRecords[0].Pid == 4321, "Should resume journaled suspended processes");
        Assert(focus.RestoreCount == 1, "Should also call Restore()");
    }

    private static void TestStartWritesJournalBeforeApplyingClamshell()
    {
        var (engine, power, _, _, _, _) = CreateHarness();
        engine.Start(TimeSpan.FromMinutes(30));

        var journalIdx = power.CallLog.IndexOf("Journal.Save");
        var clamshellIdx = power.CallLog.IndexOf("Power.ApplyClamshellMode");
        var awakeIdx = power.CallLog.IndexOf("Power.EnableAwakeAssertion");

        Assert(journalIdx >= 0, "Journal.Save should be called");
        Assert(clamshellIdx > journalIdx, "Journal must be saved before ApplyClamshellMode");
        Assert(awakeIdx > journalIdx, "Journal must be saved before EnableAwakeAssertion");
    }

    private static void TestUpdateConfigSyncsAutoSleep()
    {
        var (engine, power, _, _, clock, _) = CreateHarness();
        engine.Start(TimeSpan.FromMinutes(10));
        engine.UpdateConfig(c => c.AutoSleepOnCompletion = false);

        clock.Advance(TimeSpan.FromMinutes(10));
        engine.Tick();

        Assert(!engine.GetStatus().IsActive, "Session should expire");
        Assert(!power.Slept, "Mid-session AutoSleepOnCompletion=false must prevent sleep on expiry");
    }

    private static void TestUpdatePolicyParsesNewerRelease()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/umamhaniff/nocturnal/releases/tag/v1.2.0",
          "draft": false,
          "prerelease": false,
          "body": "CLI support and bug fixes",
          "assets": [
            {
              "name": "Nocturnal-Setup-1.2.0.exe",
              "browser_download_url": "https://github.com/umamhaniff/nocturnal/releases/download/v1.2.0/Nocturnal-Setup-1.2.0.exe",
              "size": 51234567,
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
          ]
        }
        """;

        var info = UpdatePolicy.ParseLatestReleaseJson(json, new Version(1, 1, 0));
        Assert(info is not null, "Should detect v1.2.0 as newer than 1.1.0");
        Assert(info!.Version == new Version(1, 2, 0), "Parsed version should match 1.2.0");
        Assert(info.TagName == "v1.2.0", "TagName should preserve v1.2.0");
        Assert(info.InstallerFileName == "Nocturnal-Setup-1.2.0.exe", "Installer filename should be parsed");
        Assert(info.InstallerDownloadUrl == "https://github.com/umamhaniff/nocturnal/releases/download/v1.2.0/Nocturnal-Setup-1.2.0.exe", "Trusted HTTPS GitHub URL should be preserved");
        Assert(info.InstallerSizeBytes == 51234567, "Asset size should match");
        Assert(info.ExpectedSha256Hex == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "SHA-256 hex digest should be extracted");
    }

    private static void TestUpdatePolicyIgnoresSameOlderOrPrerelease()
    {
        const string sameJson = """{"tag_name":"v1.1.0","draft":false,"prerelease":false,"assets":[]}""";
        const string olderJson = """{"tag_name":"v1.0.0","draft":false,"prerelease":false,"assets":[]}""";
        const string prereleaseJson = """{"tag_name":"v1.3.0","draft":false,"prerelease":true,"assets":[]}""";
        const string prereleaseTagJson = """{"tag_name":"v1.3.0-beta.1","draft":false,"prerelease":false,"assets":[]}""";

        var current = new Version(1, 1, 0);
        Assert(UpdatePolicy.ParseLatestReleaseJson(sameJson, current) is null, "Same version must return null");
        Assert(UpdatePolicy.ParseLatestReleaseJson(olderJson, current) is null, "Older version must return null");
        Assert(UpdatePolicy.ParseLatestReleaseJson(prereleaseJson, current) is null, "Prerelease flag must be ignored");
        Assert(UpdatePolicy.ParseLatestReleaseJson(prereleaseTagJson, current) is null, "Prerelease tag suffix must be ignored");
    }

    private static void TestUpdatePolicyRejectsUntrustedHosts()
    {
        const string httpJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/umamhaniff/nocturnal/releases/tag/v1.2.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "Nocturnal-Setup-1.2.0.exe",
              "browser_download_url": "http://github.com/umamhaniff/nocturnal/releases/download/v1.2.0/Nocturnal-Setup-1.2.0.exe"
            }
          ]
        }
        """;

        const string rogueHostJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/umamhaniff/nocturnal/releases/tag/v1.2.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "Nocturnal-Setup-1.2.0.exe",
              "browser_download_url": "https://evil.example.com/Nocturnal-Setup-1.2.0.exe"
            }
          ]
        }
        """;

        var r1 = UpdatePolicy.ParseLatestReleaseJson(httpJson, new Version(1, 1, 0));
        var r2 = UpdatePolicy.ParseLatestReleaseJson(rogueHostJson, new Version(1, 1, 0));

        Assert(r1 is not null && r1.InstallerDownloadUrl is null, "Non-HTTPS download URL must be rejected");
        Assert(r2 is not null && r2.InstallerDownloadUrl is null, "Non-GitHub host must be rejected");
    }

    private static void TestUpdatePolicyInstallerArgs()
    {
        var userArgs = UpdatePolicy.BuildInstallerArguments(isPerMachineInstall: false);
        var machineArgs = UpdatePolicy.BuildInstallerArguments(isPerMachineInstall: true);

        Assert(userArgs.Contains("/VERYSILENT") && userArgs.Contains("/RELAUNCH=1") && userArgs.Contains("/CURRENTUSER"),
            "Per-user args must include silent, relaunch, and currentuser");
        Assert(machineArgs.Contains("/VERYSILENT") && machineArgs.Contains("/RELAUNCH=1") && machineArgs.Contains("/ALLUSERS"),
            "Per-machine args must include silent, relaunch, and allusers");
    }

    private static void TestTurnOffDisplay()
    {
        var (engine, power, _, _, _, _, _, _) = CreateFullHarness();
        engine.UpdateConfig(c => c.KeepDisplayAwake = true);
        engine.Start(TimeSpan.FromMinutes(20));

        engine.TurnOffDisplay();

        Assert(power.DisplayOffCount == 1, "TurnOffDisplay must invoke power controller");
        Assert(power.LastKeepDisplayAwake == false, "TurnOffDisplay must clear display-awake assertion so Windows turns panel off");
    }

    private static void TestSessionHistoryRecording()
    {
        var (engine, _, monitor, _, clock, _, _, history) = CreateFullHarness();
        engine.UpdateConfig(c => c.AgentFocusEnabled = true);
        monitor.Snapshot = new SystemPowerSnapshot(false, 82, true, ThermalLevel.Normal, 49.0);

        for (var i = 0; i < 12; i++)
        {
            engine.Start(TimeSpan.FromMinutes(15));
            clock.Advance(TimeSpan.FromMinutes(5));
            engine.Stop(StopReason.Manual);
        }

        var loaded = engine.GetRecentHistory();
        Assert(loaded.Count == 10, $"History should cap at 10 entries, got {loaded.Count}");
        Assert(loaded[0].StopReason == StopReason.Manual, "History should record StopReason");
        Assert(loaded[0].StartBatteryPercent == 82, "History should record start battery percentage");
        Assert(loaded[0].ReclaimedRamBytes > 0, "History should capture Focus reclaimed RAM");
    }

    private static void TestWatchProcessExitsAfterGrace()
    {
        var (engine, power, _, _, clock, _, watcher, history) = CreateFullHarness(TimeSpan.FromSeconds(15));
        watcher.IsRunning = true;
        engine.StartWatchingProcess("python.exe", pid: 4242, maxDuration: TimeSpan.FromHours(2));

        Assert(engine.GetStatus().WatchTarget?.ProcessName == "python", "Watched process name should be normalized");

        watcher.IsRunning = false;
        engine.Tick();
        Assert(engine.GetStatus().IsActive, "Must stay active when disappearance is first observed");

        clock.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();
        Assert(engine.GetStatus().IsActive, "Must stay active before grace period elapses");

        clock.Advance(TimeSpan.FromSeconds(5));
        engine.Tick();
        Assert(!engine.GetStatus().IsActive, "Session must stop once grace period elapses");
        Assert(engine.GetStatus().LastStopReason == StopReason.WatchedProcessExited, "Should record WatchedProcessExited");
        Assert(power.Slept, "Should auto-sleep when AutoSleepOnCompletion is true");
        Assert(history.Load()[0].WatchedProcessName == "python", "History should record watched process name");
    }

    private static void TestWatchProcessResetsGraceOnRestart()
    {
        var (engine, _, _, _, clock, _, watcher, _) = CreateFullHarness(TimeSpan.FromSeconds(15));
        watcher.IsRunning = true;
        engine.StartWatchingProcess("dotnet");

        watcher.IsRunning = false;
        engine.Tick();
        clock.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();

        watcher.IsRunning = true;
        clock.Advance(TimeSpan.FromSeconds(5));
        engine.Tick();
        Assert(engine.GetStatus().IsActive, "Grace timer must reset when process comes back");

        watcher.IsRunning = false;
        engine.Tick();
        clock.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();
        Assert(engine.GetStatus().IsActive, "Restarted grace timer should require full 15s again");

        clock.Advance(TimeSpan.FromSeconds(5));
        engine.Tick();
        Assert(!engine.GetStatus().IsActive, "Restarted grace timer should expire after full 15s");
    }

    private static void TestWatchProcessMaxDuration()
    {
        var (engine, power, _, _, clock, _, watcher, _) = CreateFullHarness(TimeSpan.FromSeconds(15));
        watcher.IsRunning = true;
        engine.StartWatchingProcess("node", maxDuration: TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromHours(1));
        engine.Tick();

        Assert(!engine.GetStatus().IsActive, "Max duration safety ceiling must stop session even if process is still running");
        Assert(engine.GetStatus().LastStopReason == StopReason.TimerExpired, "Max ceiling expiry must record TimerExpired");
        Assert(power.Slept, "Ceiling expiry should honor AutoSleepOnCompletion");
    }

    private static void TestIpcCommandParserValid()
    {
        Assert(IpcCommandParser.TryParseArgs(["start", "2h", "--focus", "--no-clamshell"], out var reqStart, out _)
            && reqStart is { Action: "start", DurationSeconds: 7200, AgentFocus: true, Clamshell: false },
            "Should parse start duration and flags");

        Assert(IpcCommandParser.TryParseArgs(["extend", "1h30m"], out var reqExtend, out _)
            && reqExtend is { Action: "extend", DurationSeconds: 5400 },
            "Should parse compound duration 1h30m");

        Assert(IpcCommandParser.TryParseArgs(["watch", "python.exe", "--pid", "9001", "--max", "6h"], out var reqWatch, out _)
            && reqWatch is { Action: "watch", WatchProcess: "python", WatchPid: 9001, DurationSeconds: 21600 },
            "Should parse watch target, pid, and max ceiling");

        Assert(IpcCommandParser.TryParseArgs(["screen-off"], out var reqScreen, out _)
            && reqScreen is { Action: "screen-off" },
            "Should parse screen-off command");
    }

    private static void TestIpcCommandParserInvalid()
    {
        Assert(!IpcCommandParser.TryParseArgs(["start", "0m"], out _, out _), "Zero duration must be rejected");
        Assert(!IpcCommandParser.TryParseArgs(["start", "99d"], out _, out _), "Over-30-day duration must be rejected");
        Assert(!IpcCommandParser.TryParseArgs(["start", "10m", "--bogus"], out _, out _), "Unknown flag must be rejected");
        Assert(!IpcCommandParser.TryParseArgs(["watch", "python", "--pid", "-5"], out _, out _), "Negative PID must be rejected");
    }

    private static void TestIpcProtocolJsonRoundTrip()
    {
        var resp = new IpcResponse(
            true,
            "Awake",
            new IpcStatusPayload(true, 1800, DateTimeOffset.UtcNow, true, true, 5, 1, 64 * 1024 * 1024, "python", "None", 88, true),
            [new SessionHistoryRecord(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, TimeSpan.FromHours(1), StopReason.TimerExpired, true, 90, 82, 32 * 1024 * 1024, "dotnet")]);

        var json = JsonSerializer.Serialize(resp);
        var roundTrip = JsonSerializer.Deserialize<IpcResponse>(json);
        Assert(roundTrip is { Ok: true, Status.WatchProcess: "python", History.Count: 1 }, "IPC response should round-trip cleanly");
    }

    private static void TestWebhookPolicyValidationAndPayloads()
    {
        Assert(!WebhookPolicy.IsValidWebhookUrl("http://discord.com/api/webhooks/1/2"), "HTTP URLs must be rejected");
        Assert(!WebhookPolicy.IsValidWebhookUrl("https://localhost:8080/hook"), "Loopback host must be rejected");
        Assert(!WebhookPolicy.IsValidWebhookUrl("https://127.0.0.1/hook"), "Loopback IP must be rejected");

        const string discordUrl = "https://discord.com/api/webhooks/123/abc";
        const string telegramUrl = "https://api.telegram.org/botTOKEN/sendMessage?chat_id=98765";
        Assert(WebhookPolicy.DetectProvider(discordUrl) == WebhookProvider.Discord, "Should detect Discord");
        Assert(WebhookPolicy.DetectProvider(telegramUrl) == WebhookProvider.Telegram, "Should detect Telegram");

        var evt = new WebhookAlertEvent("TimerExpired", "Session Completed", "Done", DateTimeOffset.UtcNow, 80, 128.5);
        Assert(WebhookPolicy.BuildJsonPayload(evt, discordUrl).Contains("\"content\":"), "Discord payload requires content key");
        var tgPayload = WebhookPolicy.BuildJsonPayload(evt, telegramUrl);
        Assert(tgPayload.Contains("\"chat_id\":\"98765\"") && tgPayload.Contains("\"text\":"), "Telegram payload requires chat_id and text");
    }

    private static void TestSessionEngineWebhookAlerts()
    {
        var alerts = new FakeAlertNotifier();
        var (engine, _, monitor, _, clock, _, _, _) = CreateFullHarness(alertNotifier: alerts);
        engine.UpdateConfig(c =>
        {
            c.AlertsEnabled = true;
            c.AlertWebhookUrl = "https://discord.com/api/webhooks/1/2";
            c.AlertOnCompletion = true;
            c.AlertOnSafetyGuard = true;
        });

        engine.Start(TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(10));
        engine.Tick();
        Assert(alerts.Events.Count == 1 && alerts.Events[0].EventType == "TimerExpired", "Completion alert should fire once");

        engine.Start(TimeSpan.FromMinutes(30));
        monitor.Snapshot = new SystemPowerSnapshot(false, 9, true, ThermalLevel.Normal, 45.0);
        engine.Tick();
        Assert(alerts.Events.Count == 2 && alerts.Events[1].EventType == "BatteryCritical", "Critical battery alert should fire");
    }

    private static void TestNetworkFailoverPolicy()
    {
        Assert(NetworkFailoverPolicy.IsValidSsid("My-Phone_5G"), "Valid hotspot SSID should pass");
        Assert(!NetworkFailoverPolicy.IsValidSsid("bad&cmd"), "Shell metacharacter SSID must be rejected");
        Assert(!NetworkFailoverPolicy.IsValidSsid("too\"quote"), "Quoted SSID must be rejected");
        Assert(!NetworkFailoverPolicy.IsValidSsid(new string('A', 33)), "Over-32-char SSID must be rejected");

        var now = DateTimeOffset.UtcNow;
        var offlineCount = 0;
        DateTimeOffset? lastAttempt = null;
        Assert(!NetworkFailoverPolicy.ShouldAttemptFailover(false, ref offlineCount, now, lastAttempt), "First offline tick must debounce");
        Assert(NetworkFailoverPolicy.ShouldAttemptFailover(false, ref offlineCount, now.AddSeconds(10), lastAttempt), "Second offline tick should trigger failover");
        lastAttempt = now.AddSeconds(10);
        Assert(!NetworkFailoverPolicy.ShouldAttemptFailover(false, ref offlineCount, now.AddSeconds(30), lastAttempt), "Cooldown must suppress immediate retry");
        Assert(!NetworkFailoverPolicy.ShouldAttemptFailover(true, ref offlineCount, now.AddSeconds(40), lastAttempt) && offlineCount == 0, "Online resets counter");
    }

    private static void TestSessionEngineWifiFailover()
    {
        var wifi = new FakeNetworkFailoverController { Online = false, ConnectResult = true };
        var (engine, _, _, _, _, _, _, _) = CreateFullHarness(networkFailover: wifi);
        engine.UpdateConfig(c =>
        {
            c.WifiFailoverEnabled = true;
            c.BackupWifiSsid = "HansHotspot";
        });

        engine.Start(TimeSpan.FromMinutes(30));
        engine.Tick();
        Assert(wifi.ConnectCalls.Count == 0, "Single offline tick should not trigger Wi-Fi switch");

        engine.Tick();
        Assert(wifi.ConnectCalls.Count == 1 && wifi.ConnectCalls[0] == "HansHotspot", "Second consecutive offline tick should switch to backup SSID");
        Assert(engine.GetStatus().WifiFailoverTriggered, "Status should reflect triggered Wi-Fi failover");
    }

    private static void TestNightShiftScheduleTrigger()
    {
        var cfg = new AppConfig
        {
            NightShiftEnabled = true,
            NightShiftStartTime = "22:00",
            NightShiftDurationMinutes = 240,
            NightShiftRequireAcPower = true
        };
        var local2202 = new DateTimeOffset(2026, 11, 26, 22, 2, 0, TimeSpan.FromHours(7));
        var today = DateOnly.FromDateTime(local2202.DateTime);

        Assert(!SchedulePolicy.ShouldTriggerNightShift(cfg, local2202, isSessionActive: false, isOnAcPower: false, null), "Require AC must block trigger on battery");
        Assert(SchedulePolicy.ShouldTriggerNightShift(cfg, local2202, isSessionActive: false, isOnAcPower: true, null), "Should trigger inside 5m window on AC");
        Assert(!SchedulePolicy.ShouldTriggerNightShift(cfg, local2202, isSessionActive: false, isOnAcPower: true, today), "Must not trigger twice on same local date");
    }

    private static void TestSessionEngineNightShiftAndProfile()
    {
        var (engine, _, monitor, _, clock, _, _, _) = CreateFullHarness();
        var utcFor2201Plus7 = new DateTimeOffset(2026, 11, 26, 22, 1, 0, TimeSpan.FromHours(7)).ToUniversalTime();
        clock.Advance(utcFor2201Plus7 - clock.UtcNow);
        monitor.Snapshot = new SystemPowerSnapshot(true, 100, true, ThermalLevel.Normal, 45.0);

        engine.UpdateConfig(c =>
        {
            c.NightShiftEnabled = true;
            c.NightShiftStartTime = "22:00";
            c.NightShiftDurationMinutes = 180;
            c.NightShiftRequireAcPower = true;
            c.NightShiftProfileName = "AI Overnight";
        });

        engine.Tick();
        var st = engine.GetStatus();
        Assert(st.IsActive && st.ActiveProfileName == "AI Overnight", "Night Shift tick should start AI Overnight profile");
        Assert(Math.Abs(st.TotalDuration.TotalMinutes - 180) < 0.1, "Night Shift should honor configured duration");
    }

    private static void TestIpcCommandParserProfile()
    {
        Assert(IpcCommandParser.TryParseArgs(["profile", "AI", "Overnight"], out var req1, out _)
            && req1 is { Action: "profile", ProfileName: "AI Overnight" }, "Should parse multi-word profile name");
        Assert(IpcCommandParser.TryParseArgs(["profile", "present"], out var req2, out _)
            && req2 is { Action: "profile", ProfileName: "present" }, "Should accept profile alias");
        Assert(!IpcCommandParser.TryParseArgs(["profile", "nonexistent"], out _, out _), "Should reject unknown profile");
    }

    private static void TestFocusPolicyDefaultProtectedHelper()
    {
        Assert(ProcessFocusPolicy.IsDefaultProtected("agy", null), "AI agent root must be protected");
        Assert(ProcessFocusPolicy.IsDefaultProtected("svchost", @"C:\Windows\System32\svchost.exe"), "System path must be protected");
        Assert(!ProcessFocusPolicy.IsDefaultProtected("Spotify", @"C:\Users\dev\AppData\Roaming\Spotify\Spotify.exe"), "User app must not be protected by default");
    }

    private static void TestAppConfigBackwardCompatibility()
    {
        const string legacyJson = """{"UiStyle":3,"ClamshellEnabled":true,"CriticalBatteryPercent":20}""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(legacyJson);
        Assert(cfg is not null && cfg.UiStyle == TrayUiStyle.Popover && !cfg.AlertsEnabled && cfg.NightShiftDurationMinutes == 240,
            "Legacy v1.0 config JSON must deserialize cleanly with new v1.3 defaults");
    }

    private static void TestScheduleProfileLookup()
    {
        var ai = SchedulePolicy.FindProfile("ai");
        var pres = SchedulePolicy.FindProfile("Presentation");
        Assert(ai is { Name: "AI Overnight", SelectedAppMode: SelectedAppMode.DeepPause }, "AI alias should match AI Overnight");
        Assert(pres is { Name: "Presentation", KeepDisplayAwake: true, ClamshellEnabled: false }, "Presentation should keep display awake without clamshell");
    }

    private sealed class FakePowerController(List<string> callLog) : IPowerController
    {
        public List<string> CallLog { get; } = callLog;
        public bool AwakeEnabled { get; private set; }
        public bool LastKeepDisplayAwake { get; private set; }
        public bool ClamshellActive { get; private set; }
        public LidActionBackup? RestoredBackup { get; private set; }
        public bool? PowerSaverState { get; private set; }
        public int DisplayOffCount { get; private set; }
        public bool Slept { get; private set; }

        public void EnableAwakeAssertion(bool keepDisplayAwake)
        {
            CallLog.Add("Power.EnableAwakeAssertion");
            AwakeEnabled = true;
            LastKeepDisplayAwake = keepDisplayAwake;
        }

        public void DisableAwakeAssertion() => AwakeEnabled = false;

        public LidActionBackup? ReadLidActionBackup()
        {
            CallLog.Add("Power.ReadLidActionBackup");
            return new LidActionBackup(Guid.NewGuid(), 1, 1);
        }

        public void ApplyClamshellMode(Guid schemeGuid)
        {
            CallLog.Add("Power.ApplyClamshellMode");
            ClamshellActive = true;
        }

        public void RestoreClamshellMode(LidActionBackup backup)
        {
            ClamshellActive = false;
            RestoredBackup = backup;
        }

        public bool TrySetPowerSaver(bool enabled)
        {
            PowerSaverState = enabled;
            return true;
        }

        public void TurnOffDisplay() => DisplayOffCount++;

        public void SleepSystem() => Slept = true;
    }

    private sealed class FakeSystemMonitor : ISystemMonitor
    {
        public SystemPowerSnapshot Snapshot { get; set; } =
            new(true, 100, true, ThermalLevel.Normal, 48.0);

        public SystemPowerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class InMemoryJournalStore(List<string>? callLog = null) : IJournalStore
    {
        public JournalEntry? Current { get; private set; }
        public JournalEntry? Load() => Current;
        public void Save(JournalEntry entry)
        {
            callLog?.Add("Journal.Save");
            Current = entry;
        }
        public void Clear() => Current = null;
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private AppConfig _config = new();
        public AppConfig Load() => _config;
        public void Save(AppConfig config) => _config = config;
    }

    private sealed class FakeClock(DateTimeOffset initial) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = initial;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class FakeAgentFocusController : IAgentFocusController
    {
        public int EngageCount { get; private set; }
        public int RestoreCount { get; private set; }
        public List<SuspendedProcessRecord> RecoveredRecords { get; } = [];

        public FocusReport Engage(AppConfig config)
        {
            EngageCount++;
            return new FocusReport(true, 4, 1, 0, ["Discord"], 64 * 1024 * 1024);
        }

        public IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses() =>
            [new SuspendedProcessRecord(1234, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero))];

        public void Restore() => RestoreCount++;

        public void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records) =>
            RecoveredRecords.AddRange(records);
    }

    private sealed class FakeProcessWatcher : IProcessWatcher
    {
        public bool IsRunning { get; set; } = true;
        public bool IsProcessRunning(ProcessWatchTarget target) => IsRunning;
        public IReadOnlyList<RunningAppInfo> ListRunningSessionApps() => [];
    }

    private sealed class FakeAlertNotifier : IAlertNotifier
    {
        public List<WebhookAlertEvent> Events { get; } = [];
        public void Notify(AppConfig config, WebhookAlertEvent alert) => Events.Add(alert);
    }

    private sealed class FakeNetworkFailoverController : INetworkFailoverController
    {
        public bool Online { get; set; } = true;
        public bool ConnectResult { get; set; } = true;
        public List<string> ConnectCalls { get; } = [];
        public bool HasInternetConnectivity() => Online;
        public bool TryConnectToProfile(string ssid)
        {
            ConnectCalls.Add(ssid);
            return ConnectResult;
        }
    }

    private sealed class InMemorySessionHistoryStore : ISessionHistoryStore
    {
        private readonly List<SessionHistoryRecord> _records = [];

        public IReadOnlyList<SessionHistoryRecord> Load() => _records.ToList();

        public void Append(SessionHistoryRecord record, int maxEntries = 10)
        {
            _records.Insert(0, record);
            if (_records.Count > maxEntries)
            {
                _records.RemoveRange(maxEntries, _records.Count - maxEntries);
            }
        }
    }
}
