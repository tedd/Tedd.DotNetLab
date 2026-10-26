using System.Globalization;
using System.Text;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;

namespace Tedd.DotNetLab.Host;

internal record CliForwardRequest(string SchemaVersion, string[] Args, string? StandardInput);
internal record CliForwardReply(string SchemaVersion, int ExitCode, string StandardOutput, string StandardError);

internal static class CliCommands
{
    public static async Task<(int ExitCode, string Output)> ExecuteAsync(LabService service, string[] args, string? stdIn)
    {
        string command = args[0];
        var stdout = new StringBuilder();
        int exitCode = 0;

        if (command == "init" && args.Length == 2)
        {
            stdout.AppendLine(Wire.Serialize(new { schema_version = Wire.Version, workspace = service.Catalog.Root }));
            return (0, stdout.ToString());
        }
        if (command == "backup" && args.Length == 2)
        {
            stdout.AppendLine(Wire.Serialize(service.Catalog.CreateBackup()));
            return (0, stdout.ToString());
        }
        if (command == "checkpoint" && args.Length is 2 or 3)
        {
            if (args.Length == 3 && args[2] != "--truncate")
                throw new LabException("invalid_request", "Unknown checkpoint option.");
            stdout.AppendLine(Wire.Serialize(service.Catalog.Checkpoint(args.Length == 3)));
            return (0, stdout.ToString());
        }
        if (command == "call" && args.Length == 3)
        {
            string body = stdIn ?? "";
            if (body.Length > 1048576) throw new LabException("resource_limit", "CLI request exceeds 1 MiB.");
            var response = await new LabTools(service).InvokeAsync(args[2], body);
            stdout.AppendLine(Wire.Serialize(response));
            if (response.Status == "error") exitCode = 1;
            return (exitCode, stdout.ToString());
        }
        if (command == "grant" && args.Length == 6)
        {
            int pid = int.Parse(args[2], CultureInfo.InvariantCulture);
            var identity = service.Discover().SingleOrDefault(t => t.Identity.ProcessId == pid)?.Identity
                ?? throw new LabException("target_gone", "Diagnostic target is not accessible.");
            var permission = args[3] switch
            {
                "observe" => Permission.Observe,
                "execute_registered_scenario" => Permission.ExecuteRegisteredScenario,
                "inspect_live_code" => Permission.InspectLiveCode,
                _ => throw new LabException("invalid_request", "Unsupported permission class.")
            };
            if (permission == Permission.InspectLiveCode) await service.ProfilerAsync(identity.Id, CancellationToken.None);
            var grant = service.IssueGrant(identity.Id, permission, int.Parse(args[4], CultureInfo.InvariantCulture), int.Parse(args[5], CultureInfo.InvariantCulture));
            stdout.AppendLine(Wire.Serialize(grant));
            return (0, stdout.ToString());
        }
        if (command == "grant-scratch" && args.Length is 5 or 6)
        {
            string scratchDir = args[2];
            if (args.Length == 5)
            {
                int seconds = int.Parse(args[3], CultureInfo.InvariantCulture);
                int maxOps = int.Parse(args[4], CultureInfo.InvariantCulture);
                stdout.AppendLine(Wire.Serialize(service.IssueScratchGrantPair(scratchDir, seconds, maxOps)));
                return (0, stdout.ToString());
            }
            var permission = args[3] switch
            {
                "observe" => Permission.Observe,
                "execute_registered_scenario" => Permission.ExecuteRegisteredScenario,
                _ => throw new LabException("invalid_request", "Scratch grant permission must be observe or execute_registered_scenario.")
            };
            stdout.AppendLine(Wire.Serialize(service.IssueScratchGrant(scratchDir, permission,
                int.Parse(args[4], CultureInfo.InvariantCulture), int.Parse(args[5], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "grant-build" && args.Length == 5)
        {
            stdout.AppendLine(Wire.Serialize(service.IssueBuildGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "import" && args.Length == 4)
        {
            stdout.AppendLine(Wire.Serialize(await service.ImportAsync(args[2], args[3])));
            return (0, stdout.ToString());
        }
        if (command == "grant-cil" && args.Length == 5)
        {
            var candidate = service.Catalog.Get<CilCandidateRecord>(args[2]);
            service.Discover();
            await service.ProfilerAsync(candidate.SourceTarget.Id, CancellationToken.None);
            stdout.AppendLine(Wire.Serialize(service.IssueMutationGrant(candidate.SourceTarget.Id, candidate.Id,
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "grant-profiler-attach" && args.Length == 5)
        {
            stdout.AppendLine(Wire.Serialize(service.IssueProfilerAttachmentGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "grant-confirmation" && args.Length == 5)
        {
            stdout.AppendLine(Wire.Serialize(service.IssueConfirmationGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "grant-export" && args.Length is 5 or 6)
        {
            if (args.Length == 6 && args[5] != "--include-raw-traces")
                throw new LabException("invalid_request", "Unknown export consent option.");
            stdout.AppendLine(Wire.Serialize(service.IssueExportGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture), args.Length == 6)));
            return (0, stdout.ToString());
        }
        if (command == "grant-deletion" && args.Length == 5)
        {
            stdout.AppendLine(Wire.Serialize(service.IssueDeletionGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "grant-release" && args.Length == 5)
        {
            stdout.AppendLine(Wire.Serialize(service.IssueReferenceReleaseGrant(args[2],
                int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture))));
            return (0, stdout.ToString());
        }
        if (command == "capture" && args.Length == 5)
        {
            int pid = int.Parse(args[2], CultureInfo.InvariantCulture), seconds = int.Parse(args[4], CultureInfo.InvariantCulture);
            var target = service.Discover().SingleOrDefault(t => t.Identity.ProcessId == pid)?.Identity
                ?? throw new LabException("target_gone", "Diagnostic target is not accessible.");
            var grant = service.IssueGrant(target.Id, Permission.Observe, Math.Min(3600, seconds + 60), 1);
            var operation = service.StartCapture(target.Id, target.Epoch, args[3], seconds, grant.Id, Wire.Id("cli"));
            stdout.AppendLine(Wire.Serialize(operation));
            while (!Lifecycle.Terminal((operation = service.Status(operation.Id)).State)) await Task.Delay(100);
            stdout.AppendLine(Wire.Serialize(operation));
            if (operation.State != OperationState.Completed) exitCode = 1;
            return (exitCode, stdout.ToString());
        }

        throw new LabException("invalid_request",
            "Usage: dotnetlab compatibility | profiles | scaffold <output-dir> [--scenario <id>] [--force] | init <workspace> | connect <workspace> | serve <workspace> | call <workspace> <tool> | grant <workspace> <pid> <permission> <seconds> <maximum-operations> | grant-scratch <workspace> <scratch-dir> [permission] <seconds> <maximum-operations> | grant-build <workspace> <build-id> <seconds> <maximum-operations> | grant-profiler-attach <workspace> <plan-id> <seconds> 1 | capture <workspace> <pid> <profile> <seconds>");
    }

    public static async Task<CliForwardReply> ExecuteCapturedAsync(LabService service, string[] args, string? stdIn)
    {
        try
        {
            var (exit, output) = await ExecuteAsync(service, args, stdIn);
            return new CliForwardReply(Wire.Version, exit, output, "");
        }
        catch (LabException e)
        {
            string err = Wire.Serialize(new { schema_version = Wire.Version, status = "error", code = e.Code, message = e.Message, recovery = e.Recovery }) + Environment.NewLine;
            return new CliForwardReply(Wire.Version, 1, "", err);
        }
        catch (Exception e) when (e is FormatException or OverflowException or IOException or ArgumentException)
        {
            string err = Wire.Serialize(new { schema_version = Wire.Version, status = "error", code = "invalid_request", message = e.Message }) + Environment.NewLine;
            return new CliForwardReply(Wire.Version, 1, "", err);
        }
    }
}
