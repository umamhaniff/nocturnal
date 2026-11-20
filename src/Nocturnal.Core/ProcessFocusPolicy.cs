namespace Nocturnal.Core;

public static class ProcessFocusPolicy
{
    public static readonly string[] CommonRamHogApps =
    [
        "Discord",
        "Spotify",
        "Steam",
        "ms-teams",
        "Teams",
        "WhatsApp",
        "Telegram",
        "Slack",
        "EpicGamesLauncher"
    ];

    private static readonly HashSet<string> ProtectedSystemAndAgentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows core & shell
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "dwm",
        "winlogon",
        "explorer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "TextInputHost",
        "Taskmgr",
        "sihost",
        "ctfmon",
        "RuntimeBroker",
        "SecurityHealthService",
        "MsMpEng",
        "audiodg",
        "conhost",
        "OpenConsole",
        "Nocturnal",

        // Agent-agnostic AI & developer runtimes / IDEs / terminals / containers
        "antigravity",
        "claude",
        "codex",
        "gemini",
        "aider",
        "goose",
        "opencode",
        "cline",
        "ollama",
        "lmstudio",
        "node",
        "bun",
        "deno",
        "npm",
        "pnpm",
        "yarn",
        "python",
        "pythonw",
        "uv",
        "pip",
        "dotnet",
        "msbuild",
        "csc",
        "go",
        "cargo",
        "rustc",
        "java",
        "javaw",
        "git",
        "ssh",
        "ssh-agent",
        "pwsh",
        "powershell",
        "cmd",
        "WindowsTerminal",
        "wt",
        "alacritty",
        "wezterm-gui",
        "Code",
        "Cursor",
        "Windsurf",
        "zed",
        "devenv",
        "rider64",
        "idea64",
        "pycharm64",
        "docker",
        "com.docker.backend",
        "Docker Desktop",
        "wsl",
        "wslhost",
        "vmmem",
        "VmmemWSL"
    };

    public static string NormalizeProcessName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    public static ProcessAction Evaluate(
        string processName,
        int sessionId,
        int currentSessionId,
        bool hasMainWindow,
        AppConfig config)
    {
        if (sessionId != currentSessionId)
        {
            return ProcessAction.Protect;
        }

        var normalized = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return ProcessAction.Protect;
        }

        if (ProtectedSystemAndAgentNames.Contains(normalized))
        {
            return ProcessAction.Protect;
        }

        foreach (var custom in config.CustomAgentWhitelist)
        {
            var customNorm = NormalizeProcessName(custom);
            if (!string.IsNullOrEmpty(customNorm)
                && string.Equals(normalized, customNorm, StringComparison.OrdinalIgnoreCase))
            {
                return ProcessAction.Protect;
            }
        }

        if (config.FocusCloseSelectedApps)
        {
            foreach (var app in config.FocusAppsToClose)
            {
                var appNorm = NormalizeProcessName(app);
                if (!string.IsNullOrEmpty(appNorm)
                    && string.Equals(normalized, appNorm, StringComparison.OrdinalIgnoreCase))
                {
                    return ProcessAction.GracefulClose;
                }
            }
        }

        if (config.FocusEcoQosEnabled && hasMainWindow)
        {
            return ProcessAction.ThrottleEcoQos;
        }

        return ProcessAction.Protect;
    }
}
