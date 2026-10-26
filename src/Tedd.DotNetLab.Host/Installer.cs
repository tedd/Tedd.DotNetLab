using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Host;

public record InstalledClientSummary(
    string Client,
    string Scope,
    string ServerName,
    string SkillDirectory,
    string McpConfigPath,
    bool DryRun);

public record InstallResult(
    string SchemaVersion,
    string Status,
    string Action,
    string DotnetExecutable,
    string HostAssembly,
    string Workspace,
    bool NativeExtensionEnabled,
    bool DryRun,
    IReadOnlyList<InstalledClientSummary> Clients,
    string GrantCommandExample);

public static class Installer
{
    private static readonly string[] AllowedSkillExtensions = [".md", ".yaml", ".yml"];
    private static readonly string[] AllClients = ["codex", "claude", "cursor", "antigravity"];

    public static async Task<InstallResult> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("install" or "uninstall"))
            throw new LabException("invalid_request", UsageText);

        bool uninstall = args[0] == "uninstall";
        string targetSpec = "all";
        string scope = "user";
        string serverName = "dotnetlab";
        string? workspace = null;
        string? projectDir = null;
        string? dotnetPath = null;
        string? hostAssembly = null;
        string? skillSource = null;
        string? codexHome = null;
        string? claudeHome = null;
        string? claudeConfig = null;
        string? cursorHome = null;
        string? antigravityHome = null;
        bool enableNative = false;
        bool dryRun = false;

        int index = 1;
        if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            targetSpec = args[index].ToLowerInvariant();
            index++;
        }

        var selectedClients = ParseClients(targetSpec);

        for (; index < args.Length; index++)
        {
            string flag = args[index];
            string RequireValue()
            {
                if (index + 1 >= args.Length)
                    throw new LabException("invalid_request", $"Option '{flag}' requires a value.");
                return args[++index];
            }
            switch (flag)
            {
                case "--workspace": workspace = RequireValue(); break;
                case "--scope": scope = RequireValue().ToLowerInvariant(); break;
                case "--project-dir": projectDir = RequireValue(); break;
                case "--server-name": serverName = RequireValue(); break;
                case "--dotnet": dotnetPath = RequireValue(); break;
                case "--host": hostAssembly = RequireValue(); break;
                case "--skill-source": skillSource = RequireValue(); break;
                case "--codex-home": codexHome = RequireValue(); break;
                case "--claude-home": claudeHome = RequireValue(); break;
                case "--claude-config": claudeConfig = RequireValue(); break;
                case "--cursor-home": cursorHome = RequireValue(); break;
                case "--antigravity-home": antigravityHome = RequireValue(); break;
                case "--enable-native-extension": enableNative = true; break;
                case "--dry-run": dryRun = true; break;
                case "--help" or "-h":
                    throw new LabException("invalid_request", UsageText);
                default:
                    throw new LabException("invalid_request", $"Unknown installer option '{flag}'. {UsageText}");
            }
        }

        if (scope is not ("user" or "project"))
            throw new LabException("invalid_request", "Option '--scope' must be 'user' or 'project'.");
        if (!Regex.IsMatch(serverName, @"\A[A-Za-z0-9_-]{1,64}\z"))
            throw new LabException("invalid_request", "Option '--server-name' must contain only ASCII letters, digits, underscores, or hyphens.");

        string resolvedHost = Path.GetFullPath(hostAssembly ?? typeof(Installer).Assembly.Location);
        if (!File.Exists(resolvedHost))
            throw new LabException("invalid_path", $"Host assembly was not found at '{resolvedHost}'.");

        string resolvedDotnet = ResolveDotnetExecutable(dotnetPath);
        string resolvedWorkspace = Path.GetFullPath(workspace ?? DefaultWorkspacePath());
        VerifyNoReparsePoints(resolvedWorkspace);

        string resolvedProjectDir = Path.GetFullPath(projectDir ?? Environment.CurrentDirectory);
        if (scope == "project")
            VerifyNoReparsePoints(resolvedProjectDir);

        string resolvedSkillSource = ResolveSkillSource(skillSource, resolvedHost);
        if (!uninstall && !Directory.Exists(resolvedSkillSource))
            throw new LabException("invalid_path", $"Bundled dotnetlab skill directory was not found at '{resolvedSkillSource}'.");

        if (!uninstall && !dryRun)
        {
            Directory.CreateDirectory(resolvedWorkspace);
            await using var service = new LabService(resolvedWorkspace);
        }

        string[] mcpArgs = enableNative
            ? ["--roll-forward", "Major", resolvedHost, "connect", resolvedWorkspace, "--enable-native-extension"]
            : ["--roll-forward", "Major", resolvedHost, "connect", resolvedWorkspace];

        var mcpEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1"
        };

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var summaries = new List<InstalledClientSummary>();

        if (selectedClients.Contains("codex"))
        {
            string resolvedCodexHome = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".codex")
                : Path.GetFullPath(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
                    Path.Combine(userHome, ".codex"));
            string codexSkillDir = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".agents", "skills", serverName)
                : Path.Combine(resolvedCodexHome, "skills", serverName);
            string codexConfigPath = Path.Combine(resolvedCodexHome, "config.toml");

            VerifyNoReparsePoints(codexSkillDir);
            VerifyNoReparsePoints(codexConfigPath);

            if (!dryRun)
            {
                if (uninstall)
                {
                    RemoveCodexMcpServer(codexConfigPath, serverName);
                    bool keepSharedAgentsSkill = scope == "project" &&
                        !selectedClients.Contains("antigravity") &&
                        HasJsonMcpServer(Path.Combine(resolvedProjectDir, ".agents", "mcp_config.json"), serverName);
                    if (!keepSharedAgentsSkill)
                        RemoveSkillDirectoryIfExists(codexSkillDir);
                }
                else
                {
                    CopySkillDirectory(resolvedSkillSource, codexSkillDir);
                    UpsertCodexMcpServer(codexConfigPath, serverName, resolvedDotnet, mcpArgs, mcpEnv);
                }
            }
            summaries.Add(new("codex", scope, serverName, codexSkillDir, codexConfigPath, dryRun));
        }

        if (selectedClients.Contains("claude"))
        {
            string resolvedClaudeHome = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".claude")
                : Path.GetFullPath(claudeHome ?? Path.Combine(userHome, ".claude"));
            string claudeSkillDir = Path.Combine(resolvedClaudeHome, "skills", serverName);
            string claudeMcpPath = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".mcp.json")
                : Path.GetFullPath(claudeConfig ?? Path.Combine(userHome, ".claude.json"));

            VerifyNoReparsePoints(claudeSkillDir);
            VerifyNoReparsePoints(claudeMcpPath);

            if (!dryRun)
            {
                if (uninstall)
                {
                    RemoveSkillDirectoryIfExists(claudeSkillDir);
                    RemoveJsonMcpServer(claudeMcpPath, serverName);
                }
                else
                {
                    CopySkillDirectory(resolvedSkillSource, claudeSkillDir);
                    UpsertJsonMcpServer(claudeMcpPath, serverName, resolvedDotnet, mcpArgs, mcpEnv, includeStdioType: true);
                }
            }
            summaries.Add(new("claude", scope, serverName, claudeSkillDir, claudeMcpPath, dryRun));
        }

        if (selectedClients.Contains("cursor"))
        {
            string resolvedCursorHome = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".cursor")
                : Path.GetFullPath(cursorHome ?? Path.Combine(userHome, ".cursor"));
            string cursorSkillDir = Path.Combine(resolvedCursorHome, "skills", serverName);
            string cursorMcpPath = Path.Combine(resolvedCursorHome, "mcp.json");

            VerifyNoReparsePoints(cursorSkillDir);
            VerifyNoReparsePoints(cursorMcpPath);

            if (!dryRun)
            {
                if (uninstall)
                {
                    RemoveSkillDirectoryIfExists(cursorSkillDir);
                    RemoveJsonMcpServer(cursorMcpPath, serverName);
                }
                else
                {
                    CopySkillDirectory(resolvedSkillSource, cursorSkillDir);
                    UpsertJsonMcpServer(cursorMcpPath, serverName, resolvedDotnet, mcpArgs, mcpEnv, includeStdioType: false);
                }
            }
            summaries.Add(new("cursor", scope, serverName, cursorSkillDir, cursorMcpPath, dryRun));
        }

        if (selectedClients.Contains("antigravity"))
        {
            string resolvedAgyHome = scope == "project"
                ? Path.Combine(resolvedProjectDir, ".agents")
                : Path.GetFullPath(antigravityHome ?? Path.Combine(userHome, ".gemini", "config"));
            string agySkillDir = Path.Combine(resolvedAgyHome, "skills", serverName);
            string agyMcpPath = Path.Combine(resolvedAgyHome, "mcp_config.json");

            VerifyNoReparsePoints(agySkillDir);
            VerifyNoReparsePoints(agyMcpPath);

            if (!dryRun)
            {
                if (uninstall)
                {
                    RemoveJsonMcpServer(agyMcpPath, serverName);
                    bool keepSharedAgentsSkill = scope == "project" &&
                        !selectedClients.Contains("codex") &&
                        HasCodexMcpServer(Path.Combine(resolvedProjectDir, ".codex", "config.toml"), serverName);
                    if (!keepSharedAgentsSkill)
                        RemoveSkillDirectoryIfExists(agySkillDir);
                }
                else
                {
                    CopySkillDirectory(resolvedSkillSource, agySkillDir);
                    UpsertJsonMcpServer(agyMcpPath, serverName, resolvedDotnet, mcpArgs, mcpEnv, includeStdioType: false);
                }
            }
            summaries.Add(new("antigravity", scope, serverName, agySkillDir, agyMcpPath, dryRun));
        }

        string grantExample = $"\"{resolvedDotnet}\" \"{resolvedHost}\" grant \"{resolvedWorkspace}\" <pid> observe 300 5";
        return new(
            Wire.Version,
            "ok",
            uninstall ? "uninstall" : "install",
            resolvedDotnet,
            resolvedHost,
            resolvedWorkspace,
            enableNative,
            dryRun,
            summaries,
            grantExample);
    }

    public const string UsageText =
        "Usage: dotnetlab <install|uninstall> [all|codex|claude|cursor|antigravity] [--workspace <path>] [--scope user|project] [--project-dir <path>] [--enable-native-extension] [--server-name <name>] [--dotnet <executable>] [--dry-run]";

    private static HashSet<string> ParseClients(string spec)
    {
        if (spec == "all")
            return new HashSet<string>(AllClients, StringComparer.Ordinal);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = raw == "agy" ? "antigravity" : raw;
            if (!AllClients.Contains(normalized, StringComparer.Ordinal))
                throw new LabException("invalid_request", $"Unsupported client '{raw}'. Expected codex, claude, cursor, antigravity, or all. {UsageText}");
            result.Add(normalized);
        }
        if (result.Count == 0)
            throw new LabException("invalid_request", $"No target client specified. {UsageText}");
        return result;
    }

    public static string DefaultWorkspacePath()
    {
        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                return Path.Combine(localAppData, "Tedd.DotNetLab", "workspace");
        }
        string? xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgData) && Path.IsPathFullyQualified(xdgData))
            return Path.Combine(xdgData, "dotnetlab", "workspace");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "dotnetlab", "workspace");
    }

    private static string ResolveDotnetExecutable(string? explicitDotnet)
    {
        if (!string.IsNullOrWhiteSpace(explicitDotnet))
            return explicitDotnet;
        string runtimeHost = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(runtimeHost) ? runtimeHost : "dotnet";
    }

    private static string ResolveSkillSource(string? explicitSkillSource, string hostAssembly)
    {
        if (!string.IsNullOrWhiteSpace(explicitSkillSource))
            return Path.GetFullPath(explicitSkillSource);
        string besideHost = Path.Combine(Path.GetDirectoryName(hostAssembly)!, "skills", "dotnetlab");
        if (Directory.Exists(besideHost))
            return besideHost;
        string besideBase = Path.Combine(AppContext.BaseDirectory, "skills", "dotnetlab");
        return besideBase;
    }

    private static void VerifyNoReparsePoints(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new LabException("invalid_path", $"Installer refuses to traverse reparse point '{full}'.");
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LabException("invalid_path", $"Installer refuses to traverse reparse point '{dir.FullName}'.");
    }

    private static void CopySkillDirectory(string sourceDir, string destinationDir)
    {
        string fullSource = Path.GetFullPath(sourceDir);
        string fullDest = Path.GetFullPath(destinationDir);
        VerifyNoReparsePoints(fullSource);
        VerifyNoReparsePoints(fullDest);
        if (!File.Exists(Path.Combine(fullSource, "SKILL.md")))
            throw new LabException("invalid_path", $"Skill source '{fullSource}' does not contain SKILL.md.");

        RemoveSkillDirectoryIfExists(fullDest);
        Directory.CreateDirectory(fullDest);

        foreach (string file in Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!AllowedSkillExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;
            string relative = Path.GetRelativePath(fullSource, file);
            string target = Path.Combine(fullDest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RemoveSkillDirectoryIfExists(string skillDir)
    {
        if (!Directory.Exists(skillDir))
            return;
        VerifyNoReparsePoints(skillDir);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillDir)));
        if (string.IsNullOrEmpty(parent))
            throw new LabException("invalid_path", "Refusing to remove root directory.");
        Directory.Delete(skillDir, recursive: true);
    }

    public static void UpsertCodexMcpServer(
        string configTomlPath,
        string serverName,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configTomlPath)!);
        string existing = File.Exists(configTomlPath) ? File.ReadAllText(configTomlPath, Encoding.UTF8) : "";
        string newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string cleaned = StripTomlServerSection(existing, serverName);

        var block = new StringBuilder();
        block.Append($"[mcp_servers.{serverName}]").Append(newline);
        block.Append($"command = \"{EscapeTomlBasicString(command)}\"").Append(newline);
        block.Append("args = [");
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) block.Append(", ");
            block.Append($"\"{EscapeTomlBasicString(args[i])}\"");
        }
        block.Append(']').Append(newline);

        if (env is { Count: > 0 })
        {
            block.Append($"[mcp_servers.{serverName}.env]").Append(newline);
            foreach (var pair in env.OrderBy(p => p.Key, StringComparer.Ordinal))
                block.Append($"{pair.Key} = \"{EscapeTomlBasicString(pair.Value)}\"").Append(newline);
        }

        string updated = string.IsNullOrWhiteSpace(cleaned)
            ? block.ToString()
            : cleaned.TrimEnd() + newline + newline + block;
        WriteFileAtomically(configTomlPath, updated);
    }

    public static void RemoveCodexMcpServer(string configTomlPath, string serverName)
    {
        if (!File.Exists(configTomlPath))
            return;
        string existing = File.ReadAllText(configTomlPath, Encoding.UTF8);
        string cleaned = StripTomlServerSection(existing, serverName);
        if (cleaned != existing)
            WriteFileAtomically(configTomlPath, cleaned);
    }

    private static bool HasCodexMcpServer(string configTomlPath, string serverName)
    {
        if (!File.Exists(configTomlPath))
            return false;
        string existing = File.ReadAllText(configTomlPath, Encoding.UTF8);
        return StripTomlServerSection(existing, serverName) != existing;
    }

    private static string StripTomlServerSection(string toml, string serverName)
    {
        if (string.IsNullOrEmpty(toml))
            return "";
        string newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = toml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        string exactHeader = $"mcp_servers.{serverName}";
        string subHeaderPrefix = $"mcp_servers.{serverName}.";
        string quotedHeader = $"mcp_servers.\"{serverName}\"";
        bool skipping = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                int close = trimmed.IndexOf(']');
                if (close > 1)
                {
                    string tableName = trimmed[1..close].Trim();
                    skipping = tableName == exactHeader ||
                               tableName == quotedHeader ||
                               tableName.StartsWith(subHeaderPrefix, StringComparison.Ordinal);
                }
            }
            if (!skipping)
                kept.Add(line);
        }

        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);

        return kept.Count == 0 ? "" : string.Join(newline, kept) + newline;
    }

    private static string EscapeTomlBasicString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    public static void UpsertJsonMcpServer(
        string jsonPath,
        string serverName,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        bool includeStdioType = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        JsonObject root;
        if (File.Exists(jsonPath))
        {
            string raw = File.ReadAllText(jsonPath, Encoding.UTF8);
            root = string.IsNullOrWhiteSpace(raw)
                ? new JsonObject()
                : JsonNode.Parse(raw) as JsonObject
                  ?? throw new LabException("invalid_config", $"Configuration file '{jsonPath}' must contain a top-level JSON object.");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var argsArray = new JsonArray();
        foreach (string arg in args)
            argsArray.Add(arg);

        var entry = new JsonObject();
        if (includeStdioType)
            entry["type"] = "stdio";
        entry["command"] = command;
        entry["args"] = argsArray;

        if (env is { Count: > 0 })
        {
            var envObj = new JsonObject();
            foreach (var pair in env.OrderBy(p => p.Key, StringComparer.Ordinal))
                envObj[pair.Key] = pair.Value;
            entry["env"] = envObj;
        }

        servers[serverName] = entry;

        string formatted = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        WriteFileAtomically(jsonPath, formatted);
    }

    public static void RemoveJsonMcpServer(string jsonPath, string serverName)
    {
        if (!File.Exists(jsonPath))
            return;
        string raw = File.ReadAllText(jsonPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw))
            return;
        if (JsonNode.Parse(raw) is not JsonObject root || root["mcpServers"] is not JsonObject servers)
            return;
        if (servers.Remove(serverName))
        {
            string formatted = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            WriteFileAtomically(jsonPath, formatted);
        }
    }

    private static bool HasJsonMcpServer(string jsonPath, string serverName)
    {
        if (!File.Exists(jsonPath))
            return false;
        string raw = File.ReadAllText(jsonPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return JsonNode.Parse(raw) is JsonObject root &&
               root["mcpServers"] is JsonObject servers &&
               servers.ContainsKey(serverName);
    }

    private static void WriteFileAtomically(string destinationPath, string content)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".dotnetlab-tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
