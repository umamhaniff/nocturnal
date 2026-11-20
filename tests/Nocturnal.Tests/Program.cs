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
        Run("FocusPolicy_ThrottlesWindowedNonAgents_AndClosesOptInApps", TestFocusPolicyActions);
        Run("SessionEngine_AgentFocus_EngagesOnStart_AndRestoresOnStop", TestSessionEngineAgentFocus);
        Run("Start_WritesJournalBeforeApplyingClamshell", TestStartWritesJournalBeforeApplyingClamshell);
        Run("UpdateConfig_SyncsAutoSleepOnActiveSession", TestUpdateConfigSyncsAutoSleep);

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
        var callLog = new List<string>();
        var power = new FakePowerController(callLog);
        var monitor = new FakeSystemMonitor();
        var journal = new InMemoryJournalStore(callLog);
        var config = new InMemoryConfigStore();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var focus = new FakeAgentFocusController();
        var engine = new SessionEngine(power, monitor, journal, config, clock, focus);
        return (engine, power, monitor, journal, clock, focus);
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

        // 35% on battery -> engage Low Power Mode
        monitor.Snapshot = new SystemPowerSnapshot(false, 35, true, ThermalLevel.Normal, 50);
        clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert(engine.GetStatus().IsActive, "Should stay active at 35%");
        Assert(power.PowerSaverState == true, "Should engage power saver below 40%");

        // 10% on battery -> stop & sleep
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
            FocusCloseSelectedApps = true,
            FocusAppsToClose = ["Discord"],
            CustomAgentWhitelist = ["my-custom-agent.exe"]
        };

        Assert(ProcessFocusPolicy.Evaluate("svchost", 0, 1, true, config) == ProcessAction.Protect, "Session 0 must be protected");
        Assert(ProcessFocusPolicy.Evaluate("explorer", 1, 1, true, config) == ProcessAction.Protect, "Explorer must be protected");
        Assert(ProcessFocusPolicy.Evaluate("claude", 1, 1, true, config) == ProcessAction.Protect, "Claude must be protected");
        Assert(ProcessFocusPolicy.Evaluate("antigravity", 1, 1, true, config) == ProcessAction.Protect, "Antigravity must be protected");
        Assert(ProcessFocusPolicy.Evaluate("node", 1, 1, true, config) == ProcessAction.Protect, "Node must be protected");
        Assert(ProcessFocusPolicy.Evaluate("python", 1, 1, true, config) == ProcessAction.Protect, "Python must be protected");
        Assert(ProcessFocusPolicy.Evaluate("Code", 1, 1, true, config) == ProcessAction.Protect, "VS Code must be protected");
        Assert(ProcessFocusPolicy.Evaluate("WindowsTerminal", 1, 1, true, config) == ProcessAction.Protect, "Windows Terminal must be protected");
        Assert(ProcessFocusPolicy.Evaluate("my-custom-agent", 1, 1, true, config) == ProcessAction.Protect, "Custom whitelist must be protected");
    }

    private static void TestFocusPolicyActions()
    {
        var config = new AppConfig
        {
            FocusEcoQosEnabled = true,
            FocusCloseSelectedApps = false,
            FocusAppsToClose = ["Discord", "Spotify"]
        };

        Assert(ProcessFocusPolicy.Evaluate("chrome", 1, 1, true, config) == ProcessAction.ThrottleEcoQos, "Windowed non-agent app should be throttled");
        Assert(ProcessFocusPolicy.Evaluate("Discord", 1, 1, true, config) == ProcessAction.ThrottleEcoQos, "When close toggle is off, selected app should only be throttled");
        Assert(ProcessFocusPolicy.Evaluate("background-worker", 1, 1, false, config) == ProcessAction.Protect, "Windowless background process should be protected");

        config.FocusCloseSelectedApps = true;
        Assert(ProcessFocusPolicy.Evaluate("Discord", 1, 1, true, config) == ProcessAction.GracefulClose, "When close toggle is on, Discord should close gracefully");
        Assert(ProcessFocusPolicy.Evaluate("spotify.exe", 1, 1, true, config) == ProcessAction.GracefulClose, "Normalization should handle .exe suffix");
    }

    private static void TestSessionEngineAgentFocus()
    {
        var (engine, _, _, _, _, focus) = CreateHarness();
        engine.UpdateConfig(c => c.AgentFocusEnabled = true);

        engine.Start(TimeSpan.FromMinutes(30));
        var status = engine.GetStatus();
        Assert(focus.EngageCount == 1, "Focus should engage on session start");
        Assert(status.Focus.IsEngaged, "Status should report focus engaged");
        Assert(status.Focus.ThrottledProcessCount == 4, "Status should expose focus counts");

        engine.Stop(StopReason.Manual);
        Assert(focus.RestoreCount == 1, "Focus should restore on session stop");
        Assert(!engine.GetStatus().Focus.IsEngaged, "Status should clear focus report after stop");
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

    private sealed class FakePowerController(List<string> callLog) : IPowerController
    {
        public List<string> CallLog { get; } = callLog;
        public bool AwakeEnabled { get; private set; }
        public bool ClamshellActive { get; private set; }
        public LidActionBackup? RestoredBackup { get; private set; }
        public bool? PowerSaverState { get; private set; }
        public bool Slept { get; private set; }

        public void EnableAwakeAssertion(bool keepDisplayAwake)
        {
            CallLog.Add("Power.EnableAwakeAssertion");
            AwakeEnabled = true;
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

        public FocusReport Engage(AppConfig config)
        {
            EngageCount++;
            return new FocusReport(true, 4, 1, ["Discord"]);
        }

        public void Restore() => RestoreCount++;
    }
}
