using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Host;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class InstallerTests
{
    [Fact]
    public async Task InstallAll_CopiesBundledSkillsAndMergesAllFourClientConfigsWithoutClobberingExistingSettings()
    {
        using var workspace = new TestWorkspace();
        string labWorkspace = Path.Combine(workspace.Root, "lab-workspace");
        string codexHome = Path.Combine(workspace.Root, "codex-home");
        string claudeHome = Path.Combine(workspace.Root, "claude-home");
        string claudeConfig = Path.Combine(workspace.Root, "claude.json");
        string cursorHome = Path.Combine(workspace.Root, "cursor-home");
        string antigravityHome = Path.Combine(workspace.Root, "antigravity-home");

        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(cursorHome);
        Directory.CreateDirectory(antigravityHome);

        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"),
            """
            model = "gpt-5-codex"

            [mcp_servers.other_tool]
            command = "node"
            args = ["server.js"]
            """);

        await File.WriteAllTextAsync(claudeConfig,
            """
            {
              "theme": "dark",
              "mcpServers": {
                "existing": {
                  "type": "stdio",
                  "command": "npx",
                  "args": ["-y", "existing-mcp"]
                }
              }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(cursorHome, "mcp.json"),
            """
            {
              "mcpServers": {
                "other_cursor": {
                  "command": "npx",
                  "args": ["cursor-mcp"]
                }
              }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(antigravityHome, "mcp_config.json"),
            """
            {
              "mcpServers": {
                "other_agy": {
                  "command": "npx",
                  "args": ["agy-mcp"]
                }
              }
            }
            """);

        var first = await Installer.RunAsync([
            "install", "all",
            "--workspace", labWorkspace,
            "--codex-home", codexHome,
            "--claude-home", claudeHome,
            "--claude-config", claudeConfig,
            "--cursor-home", cursorHome,
            "--antigravity-home", antigravityHome
        ]);

        Assert.Equal("ok", first.Status);
        Assert.Equal("install", first.Action);
        Assert.Equal(4, first.Clients.Count);
        Assert.False(first.NativeExtensionEnabled);
        Assert.True(File.Exists(Path.Combine(labWorkspace, "catalog.sqlite")));

        Assert.True(File.Exists(Path.Combine(codexHome, "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(codexHome, "skills", "dotnetlab", "agents", "openai.yaml")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(codexHome, "skills", "dotnetlab", "references"), "*.md", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(codexHome, "skills", "dotnetlab", "references", "mcp-loop.md")));
        Assert.True(File.Exists(Path.Combine(codexHome, "skills", "dotnetlab", "references", "playbooks", "simd-and-intrinsics.md")));
        Assert.True(File.Exists(Path.Combine(claudeHome, "skills", "dotnetlab", "SKILL.md")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(claudeHome, "skills", "dotnetlab", "references"), "*.md", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(claudeHome, "skills", "dotnetlab", "references", "playbooks", "jit-cpu-and-bounds.md")));
        Assert.True(File.Exists(Path.Combine(cursorHome, "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(antigravityHome, "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(antigravityHome, "skills", "dotnetlab", "references", "playbooks", "simd-and-intrinsics.md")));

        string codexToml = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
        Assert.Contains("model = \"gpt-5-codex\"", codexToml);
        Assert.Contains("[mcp_servers.other_tool]", codexToml);
        Assert.Contains("[mcp_servers.dotnetlab]", codexToml);
        Assert.Contains("DOTNET_ROLL_FORWARD_TO_PRERELEASE = \"1\"", codexToml);
        Assert.DoesNotContain("--enable-native-extension", codexToml);

        // Re-running with --enable-native-extension replaces the section idempotently.
        var second = await Installer.RunAsync([
            "install", "all",
            "--workspace", labWorkspace,
            "--codex-home", codexHome,
            "--claude-home", claudeHome,
            "--claude-config", claudeConfig,
            "--cursor-home", cursorHome,
            "--antigravity-home", antigravityHome,
            "--enable-native-extension"
        ]);
        Assert.True(second.NativeExtensionEnabled);

        codexToml = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
        Assert.Equal(1, codexToml.Split("[mcp_servers.dotnetlab]", StringSplitOptions.None).Length - 1);
        Assert.Contains("--enable-native-extension", codexToml);
        Assert.Contains("[mcp_servers.other_tool]", codexToml);

        using (var claudeDoc = JsonDocument.Parse(await File.ReadAllTextAsync(claudeConfig)))
        {
            Assert.Equal("dark", claudeDoc.RootElement.GetProperty("theme").GetString());
            var servers = claudeDoc.RootElement.GetProperty("mcpServers");
            Assert.Equal("npx", servers.GetProperty("existing").GetProperty("command").GetString());
            var installed = servers.GetProperty("dotnetlab");
            Assert.Equal("stdio", installed.GetProperty("type").GetString());
            Assert.Equal("1", installed.GetProperty("env").GetProperty("DOTNET_ROLL_FORWARD_TO_PRERELEASE").GetString());
            var args = installed.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("--roll-forward", args);
            Assert.Contains("Major", args);
            Assert.Contains("connect", args);
            Assert.Contains(labWorkspace, args);
            Assert.Contains("--enable-native-extension", args);
        }

        using (var cursorDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cursorHome, "mcp.json"))))
        {
            var servers = cursorDoc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("other_cursor", out _));
            Assert.True(servers.TryGetProperty("dotnetlab", out var cursorServer));
            Assert.Equal("1", cursorServer.GetProperty("env").GetProperty("DOTNET_ROLL_FORWARD_TO_PRERELEASE").GetString());
        }

        using (var agyDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(antigravityHome, "mcp_config.json"))))
        {
            var servers = agyDoc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("other_agy", out _));
            Assert.True(servers.TryGetProperty("dotnetlab", out var agyServer));
            Assert.Equal("1", agyServer.GetProperty("env").GetProperty("DOTNET_ROLL_FORWARD_TO_PRERELEASE").GetString());
        }

        // Uninstall cleanly removes dotnetlab while preserving unrelated entries.
        var removed = await Installer.RunAsync([
            "uninstall", "all",
            "--workspace", labWorkspace,
            "--codex-home", codexHome,
            "--claude-home", claudeHome,
            "--claude-config", claudeConfig,
            "--cursor-home", cursorHome,
            "--antigravity-home", antigravityHome
        ]);
        Assert.Equal("uninstall", removed.Action);
        Assert.False(Directory.Exists(Path.Combine(codexHome, "skills", "dotnetlab")));
        Assert.False(Directory.Exists(Path.Combine(claudeHome, "skills", "dotnetlab")));
        Assert.False(Directory.Exists(Path.Combine(cursorHome, "skills", "dotnetlab")));
        Assert.False(Directory.Exists(Path.Combine(antigravityHome, "skills", "dotnetlab")));

        codexToml = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
        Assert.DoesNotContain("[mcp_servers.dotnetlab]", codexToml);
        Assert.Contains("[mcp_servers.other_tool]", codexToml);

        using (var claudeDoc = JsonDocument.Parse(await File.ReadAllTextAsync(claudeConfig)))
        {
            var servers = claudeDoc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("existing", out _));
            Assert.False(servers.TryGetProperty("dotnetlab", out _));
        }

        using (var cursorDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cursorHome, "mcp.json"))))
        {
            var servers = cursorDoc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("other_cursor", out _));
            Assert.False(servers.TryGetProperty("dotnetlab", out _));
        }

        using (var agyDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(antigravityHome, "mcp_config.json"))))
        {
            var servers = agyDoc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("other_agy", out _));
            Assert.False(servers.TryGetProperty("dotnetlab", out _));
        }
    }

    [Fact]
    public async Task CliInstallProjectScope_WritesProjectSkillsAndMcpFilesViaHostProcess()
    {
        using var workspace = new TestWorkspace();
        string projectDir = Path.Combine(workspace.Root, "sample-project");
        string labWorkspace = Path.Combine(workspace.Root, "project-lab");
        Directory.CreateDirectory(projectDir);

        string hostDll = Path.Combine(TestWorkspace.TaskRoot, "bin", "Tedd.DotNetLab.Host", "Release", "net10.0", "Tedd.DotNetLab.Host.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = projectDir
        };
        start.ArgumentList.Add(hostDll);
        start.ArgumentList.Add("install");
        start.ArgumentList.Add("all");
        start.ArgumentList.Add("--scope");
        start.ArgumentList.Add("project");
        start.ArgumentList.Add("--project-dir");
        start.ArgumentList.Add(projectDir);
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(labWorkspace);

        using var process = Process.Start(TestWorkspace.PinRuntime(start))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.ExitCode == 0, await stderr);

        var result = Wire.Deserialize<InstallResult>(await stdout);
        Assert.Equal("ok", result.Status);
        Assert.Equal(4, result.Clients.Count);
        Assert.True(File.Exists(Path.Combine(projectDir, ".agents", "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".codex", "config.toml")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".claude", "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".cursor", "skills", "dotnetlab", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".cursor", "mcp.json")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".agents", "mcp_config.json")));

        // Uninstalling codex in project scope keeps shared .agents/skills/dotnetlab while Antigravity remains configured.
        await Installer.RunAsync(["uninstall", "codex", "--scope", "project", "--project-dir", projectDir]);
        Assert.True(File.Exists(Path.Combine(projectDir, ".agents", "skills", "dotnetlab", "SKILL.md")));
        await Installer.RunAsync(["uninstall", "agy", "--scope", "project", "--project-dir", projectDir]);
        Assert.False(Directory.Exists(Path.Combine(projectDir, ".agents", "skills", "dotnetlab")));
    }
}
