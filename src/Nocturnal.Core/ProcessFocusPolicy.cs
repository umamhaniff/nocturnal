namespace Nocturnal.Core;

public static class ProcessFocusPolicy
{
    private static readonly HashSet<string> ShellLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "svchost",
        "sihost",
        "services",
        "wininit",
        "userinit",
        "taskhostw",
        "RuntimeBroker"
    };

    private static readonly HashSet<string> ProtectedRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows core & shell
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "lsass",
        "fontdrvhost",
        "dwm",
        "winlogon",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "TextInputHost",
        "Taskmgr",
        "ctfmon",
        "SecurityHealthService",
        "SecurityHealthSystray",
        "ApplicationFrameHost",
        "DllHost",
        "ShellHost",
        "LockApp",
        "RtkAudUService64",
        "igfxEM",
        "MsMpEng",
        "audiodg",
        "Nocturnal",

        // AI agents & local LLM hosts
        "agy",
        "antigravity",
        "claude",
        "codex",
        "gemini",
        "aider",
        "goose",
        "opencode",
        "cline",
        "ollama",
        "ollama app",
        "lmstudio",
        "llama-server",
        "koboldcpp",

        // Terminals
        "WindowsTerminal",
        "wt",
        "pwsh",
        "powershell",
        "cmd",
        "alacritty",
        "wezterm-gui",
        "mintty",
        "bash",
        "Hyper",
        "Tabby",
        "ConEmu64",
        "conhost",
        "OpenConsole",

        // IDEs & editors
        "Code",
        "Cursor",
        "Windsurf",
        "zed",
        "devenv",
        "rider64",
        "idea64",
        "pycharm64",
        "webstorm64",
        "goland64",
        "clion64",
        "sublime_text",

        // Dev runtimes & containers
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
        "docker",
        "com.docker.backend",
        "Docker Desktop",
        "wsl",
        "wslhost",
        "vmmem",
        "VmmemWSL",

        // Remote access, VPNs, & local DNS
        "remoting_desktop",
        "remoting_host",
        "remoting_network",
        "tailscaled",
        "tailscale-ipn",
        "cloudflared",
        "sshd",
        "rustdesk",
        "AnyDesk",
        "parsecd",
        "TeamViewer",
        "TeamViewer_Service",
        "mstsc",
        "wireguard",
        "openvpn",
        "zerotier-one",
        "warp-svc",
        "Cloudflare WARP",
        "sunshine",
        "Moonlight",
        "nxnode",
        "nxserver",
        "v2rayN",
        "clash",
        "clash-verge",
        "ngrok",
        "AdGuardHome"
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

    public static bool IsDefaultProtected(string processName, string? imagePath)
    {
        var norm = NormalizeProcessName(processName);
        return string.IsNullOrEmpty(norm)
            || IsProtectedSystemPath(imagePath)
            || ShellLaunchers.Contains(norm)
            || ProtectedRootNames.Contains(norm);
    }

    public static bool IsProtectedSystemPath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var path = imagePath.Trim().Replace('/', '\\');
        if (path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            path = path[2..];
        }

        return path.StartsWith(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Windows\SystemApps\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Windows\ImmersiveControlPanel\", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<int, ProcessAction> EvaluateTree(
        IReadOnlyList<ProcessNode> snapshot,
        int currentSessionId,
        int currentPid,
        AppConfig config)
    {
        var byPid = new Dictionary<int, ProcessNode>(snapshot.Count);
        foreach (var node in snapshot)
        {
            byPid[node.Pid] = node;
        }

        var customWhitelist = BuildNormalizedSet(config.CustomAgentWhitelist);
        var targets = BuildNormalizedSet(config.FocusAppsToClose);
        foreach (var extra in config.CustomAppsToManage)
        {
            var normExtra = NormalizeProcessName(extra);
            if (!string.IsNullOrEmpty(normExtra))
            {
                targets.Add(normExtra);
            }
        }

        var mode = config.FocusSelectedAppMode != SelectedAppMode.Off
            ? config.FocusSelectedAppMode
            : (config.FocusCloseSelectedApps ? SelectedAppMode.GracefulClose : SelectedAppMode.Off);

        var result = new Dictionary<int, ProcessAction>(snapshot.Count);
        foreach (var node in snapshot)
        {
            result[node.Pid] = EvaluateNode(node, byPid, currentSessionId, currentPid, customWhitelist, targets, mode, config);
        }

        return result;
    }

    private static ProcessAction EvaluateNode(
        ProcessNode node,
        Dictionary<int, ProcessNode> byPid,
        int currentSessionId,
        int currentPid,
        HashSet<string> customWhitelist,
        HashSet<string> targets,
        SelectedAppMode mode,
        AppConfig config)
    {
        if (node.Pid == currentPid || node.SessionId != currentSessionId)
        {
            return ProcessAction.Protect;
        }

        var norm = NormalizeProcessName(node.Name);
        if (string.IsNullOrEmpty(norm)
            || IsProtectedSystemPath(node.ImagePath)
            || ShellLaunchers.Contains(norm)
            || ProtectedRootNames.Contains(norm)
            || customWhitelist.Contains(norm))
        {
            return ProcessAction.Protect;
        }

        var hasTargetAncestor = false;
        var visited = new HashSet<int> { node.Pid };
        var curPid = node.ParentPid;
        for (var depth = 0; depth < 24 && curPid != 0 && visited.Add(curPid); depth++)
        {
            if (!byPid.TryGetValue(curPid, out var parent))
            {
                break;
            }

            var parentNorm = NormalizeProcessName(parent.Name);
            if (ShellLaunchers.Contains(parentNorm))
            {
                break;
            }

            if (ProtectedRootNames.Contains(parentNorm) || customWhitelist.Contains(parentNorm))
            {
                return ProcessAction.Protect;
            }

            if (targets.Contains(parentNorm))
            {
                hasTargetAncestor = true;
            }

            curPid = parent.ParentPid;
        }

        if (mode != SelectedAppMode.Off)
        {
            if (config.FocusApplyToAllNonAiApps)
            {
                return mode == SelectedAppMode.DeepPause
                    ? ProcessAction.DeepPause
                    : ProcessAction.GracefulClose;
            }

            if (targets.Contains(norm))
            {
                return mode == SelectedAppMode.DeepPause
                    ? ProcessAction.DeepPause
                    : ProcessAction.GracefulClose;
            }

            if (hasTargetAncestor && mode == SelectedAppMode.DeepPause)
            {
                return ProcessAction.DeepPause;
            }
        }

        return config.FocusEcoQosEnabled
            ? ProcessAction.ThrottleEcoQos
            : ProcessAction.Protect;
    }

    private static HashSet<string> BuildNormalizedSet(IEnumerable<string> items)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var norm = NormalizeProcessName(item);
            if (!string.IsNullOrEmpty(norm))
            {
                set.Add(norm);
            }
        }
        return set;
    }
}
