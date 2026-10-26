using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public record ScratchGrantPair(Grant ExecuteGrant, Grant ObserveGrant, string ScratchDirectory);

public sealed partial class LabService
{
    public const string ScratchTargetPrefix = "scratch-dir:";

    internal static bool IsScratchGrant(Grant grant) =>
        grant.TargetId.StartsWith(ScratchTargetPrefix, StringComparison.Ordinal);

    internal static string NormalizeScratchPrefix(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !OwnedScratchPath(directory))
            throw new LabException("invalid_path", "Scratch grant requires a non-root fully qualified local directory.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new LabException("invalid_path", "Scratch directory must reside on a local filesystem.");
        string hostBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, hostBase, cmp))
            throw new LabException("invalid_path", "Cannot issue a scratch grant over the controller binary directory.");
        VerifyDirectoryAncestors(full);
        if (!Directory.Exists(full))
            throw new LabException("invalid_path", "Scratch directory does not exist.");
        return full + Path.DirectorySeparatorChar;
    }

    internal static bool IsPathStrictlyUnderScratch(string candidatePath, string normalizedScratchPrefix)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath)) return false;
        string full;
        try { full = Path.GetFullPath(candidatePath); }
        catch (ArgumentException) { return false; }
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(normalizedScratchPrefix, cmp)) return false;
        for (string? cur = full; cur is not null && cur.Length >= normalizedScratchPrefix.Length - 1; cur = Path.GetDirectoryName(cur))
        {
            if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    // Local CLI only: authorizes targets built and run strictly under an approved disposable scratch directory.
    public Grant IssueScratchGrant(string scratchDirectory, Permission permission, int seconds, int maximumOperations)
    {
        if (permission is not (Permission.Observe or Permission.ExecuteRegisteredScenario))
            throw new LabException("capability_unsupported", "Scratch directory grants only authorize observe or execute_registered_scenario.");
        if (seconds is < 1 or > 3600 || maximumOperations is < 1 or > 100000)
            throw new LabException("invalid_request", "Scratch grant budget is invalid.");
        string prefix = NormalizeScratchPrefix(scratchDirectory);
        string identityKey = OperatingSystem.IsWindows() ? prefix.ToUpperInvariant() : prefix;
        string targetId = ScratchTargetPrefix + Wire.Hash(new { scratch = identityKey })[..24];
        var grant = new Grant(Wire.Id("grant"), Catalog.Root, targetId, 0, permission,
            DateTimeOffset.UtcNow.AddSeconds(seconds), maximumOperations, prefix);
        Catalog.Put(grant.Id, "grant", grant);
        return grant;
    }

    public ScratchGrantPair IssueScratchGrantPair(string scratchDirectory, int seconds, int maximumOperations)
    {
        var execute = IssueScratchGrant(scratchDirectory, Permission.ExecuteRegisteredScenario, seconds, maximumOperations);
        var observe = IssueScratchGrant(scratchDirectory, Permission.Observe, seconds, maximumOperations);
        return new ScratchGrantPair(execute, observe, execute.ContentHash!);
    }

    // Local CLI only: authorizes launching a sealed registered NormalBuild (e.g. for owned JIT disassembly).
    public Grant IssueBuildGrant(string buildId, int seconds, int maximumOperations)
    {
        var build = Record<NormalBuild>(buildId, "normal_build");
        if (seconds is < 1 or > 3600 || maximumOperations is < 1 or > 1000)
            throw new LabException("invalid_request", "Build launch grant budget is invalid.");
        var grant = new Grant(Wire.Id("grant"), Catalog.Root, build.Id, 0, Permission.LaunchRegisteredBuild,
            DateTimeOffset.UtcNow.AddSeconds(seconds), maximumOperations, build.ContentHash);
        Catalog.Put(grant.Id, "grant", grant);
        return grant;
    }

    private Grant BindTargetGrant(Grant grant, TargetIdentity target) =>
        IsScratchGrant(grant)
            ? new Grant(grant.Id, Catalog.Root, target.Id, target.StartTimeUtcTicks, grant.Permission, grant.ExpiresAt, grant.MaximumOperations)
            : grant;

    private async Task<Grant> AuthorizeTargetOrScratchAsync(string grantId, TargetIdentity target, Permission permission,
        HarnessHandshake? knownHandshake, CancellationToken cancellation)
    {
        if (Catalog.Get(grantId).Kind != "grant")
            throw new LabException("permission_denied", "Expected an issued grant ID.");
        var grant = Catalog.Get<Grant>(grantId);
        await ReauthorizeTargetOrScratchAsync(grant, target, permission, knownHandshake, cancellation);
        return grant;
    }

    private async Task ReauthorizeTargetOrScratchAsync(Grant grant, TargetIdentity target, Permission permission,
        HarnessHandshake? knownHandshake, CancellationToken cancellation)
    {
        if (!IsScratchGrant(grant))
        {
            Policies.Authorize(grant, Catalog.Root, target, permission, DateTimeOffset.UtcNow);
            targets.Verify(target, target.Epoch);
            return;
        }

        if (permission is not (Permission.Observe or Permission.ExecuteRegisteredScenario) ||
            grant.Workspace != Catalog.Root || grant.Permission != permission ||
            grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.MaximumOperations <= 0 ||
            grant.MethodScope is not null || string.IsNullOrEmpty(grant.ContentHash))
        {
            throw new LabException("permission_denied", "Scratch grant is expired, exhausted, or outside the requested scope.");
        }

        string prefix = NormalizeScratchPrefix(grant.ContentHash);
        targets.Verify(target, target.Epoch);

        bool directMatch = false;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited) throw new LabException("target_gone", "Target exited.");
            if (ProcessIdentity.StartTimeUtcTicks(process) != target.StartTimeUtcTicks)
                throw new LabException("target_identity_changed", "Process ID was reused.");

            try
            {
                string? mainFile = process.MainModule?.FileName;
                if (mainFile is not null && IsPathStrictlyUnderScratch(mainFile, prefix))
                    directMatch = true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }

            if (!directMatch && OperatingSystem.IsLinux())
            {
                directMatch = TryMatchLinuxCmdlineUnderScratch(target.ProcessId, prefix);
            }
        }
        catch (ArgumentException)
        {
            throw new LabException("target_gone", "Target exited.");
        }

        if (directMatch) return;

        var handshake = knownHandshake ?? await harness.HandshakeAsync(target, cancellation);
        if (handshake.Target.ProcessId != target.ProcessId ||
            handshake.Target.StartTimeUtcTicks != target.StartTimeUtcTicks ||
            handshake.Runtime is null)
        {
            throw new LabException("permission_denied", "Scratch target identity or runtime footprint could not be verified.");
        }

        if (!EntryHashExistsUnderScratch(prefix, handshake.Runtime.EntryAssemblyHash))
            throw new LabException("permission_denied", "Target entry assembly does not reside within the granted scratch directory.");
    }

    private static bool TryMatchLinuxCmdlineUnderScratch(int pid, string scratchPrefix)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
            if (bytes.Length == 0 || bytes.Length > 65536) return false;
            string[] parts = Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(part) && IsPathStrictlyUnderScratch(part, scratchPrefix))
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static bool EntryHashExistsUnderScratch(string scratchPrefix, string expectedSha256Lower)
    {
        var pending = new Stack<string>();
        pending.Push(Path.TrimEndingDirectorySeparator(scratchPrefix));
        int inspected = 0;
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            DirectoryInfo info = new(current);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string[] subdirs;
            string[] dlls;
            try
            {
                subdirs = Directory.GetDirectories(current);
                dlls = Directory.GetFiles(current, "*.dll");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (string dll in dlls)
            {
                if (++inspected > 512)
                    throw new LabException("resource_limit", "Scratch directory exceeds the 512-DLL verification budget.");
                if ((File.GetAttributes(dll) & FileAttributes.ReparsePoint) != 0) continue;
                var fileInfo = new FileInfo(dll);
                if (fileInfo.Length is <= 0 or > 134217728) continue;
                using var stream = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (hash == expectedSha256Lower) return true;
            }
            foreach (string sub in subdirs) pending.Push(sub);
        }
        return false;
    }
}
