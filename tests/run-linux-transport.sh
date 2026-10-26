#!/usr/bin/env bash
set -euo pipefail

# This lane qualifies managed Linux transport, quiet execution, and direct capture.
# Native profiler and mutation qualification require the native startup switch.
task_root=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux
task_tmp=/mnt/d/Workspaces/AI/dotnetlab-linux/tmp
task_sdk=${1:?Supply the absolute directory containing the pinned Linux SDK.}
task_repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "$task_root/cli" "$task_root/packages" "$task_root/evidence" "$task_tmp"
export PATH="$task_sdk:$PATH" DOTNET_ROOT="$task_sdk"
export DOTNET_CLI_HOME="$task_root/cli" NUGET_PACKAGES="$task_root/packages"
export TMPDIR="$task_tmp" TEMP="$task_tmp" TMP="$task_tmp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
export PYTHONDONTWRITEBYTECODE=1
cd "$task_repo"
[[ $(uname -m) == x86_64 && $(id -u) != 0 ]] || { echo 'Linux x64 under a regular user is required.' >&2; exit 1; }
[[ $(dotnet --version) == 10.0.401 ]] || { echo 'Pinned Linux SDK is required.' >&2; exit 1; }
dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 10.0.12 ' || { echo 'Pinned runtime is required.' >&2; exit 1; }
task_run=$(mktemp -d "$task_root/evidence/run-$(date -u +%Y%m%dT%H%M%S)-XXXXXXXX")
export DOTNETLAB_TEST_METADATA_PATH="$task_run/declarations.json"
{
    printf 'Scope: managed transport, quiet registered execution and individual timing, direct retained EventPipe/GC analysis\n'
    uname -srm
    id
    cat /etc/os-release
    dotnet --list-sdks
    dotnet --list-runtimes
    sha256sum "$task_sdk/dotnet" "$task_sdk/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so"
    printf 'Parser delegated cgroup: %s\n' "${DOTNETLAB_PARSER_CGROUP_ROOT:?Delegated parser root is required.}"
    printf 'Parser limits: request memory.max; memory.swap.max=0; memory.oom.group=1; pids.max=256\n'
    printf 'Parser accounting: charged anon/file/kernel memory; kernel transient overshoot possible; private commit not collected\n'
    cat "$DOTNETLAB_PARSER_CGROUP_ROOT/cgroup.controllers" "$DOTNETLAB_PARSER_CGROUP_ROOT/cgroup.subtree_control"
} > "$task_run/platform.txt"
if [[ ${DOTNETLAB_LINUX_NATIVE_STARTUP:-0} == 1 ]]; then
    task_native=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-native-exec
    cmake -S native -B "$task_native" -G Ninja -DCMAKE_CXX_COMPILER=clang++ -DCMAKE_BUILD_TYPE=Release > "$task_run/native-build.log" 2>&1
    cmake --build "$task_native" >> "$task_run/native-build.log" 2>&1
    ctest --test-dir "$task_native" --output-on-failure > "$task_run/native-tests.log" 2>&1 || { cat "$task_run/native-tests.log"; exit 1; }
    export DOTNETLAB_LINUX_PROFILER_PATH="$task_native/Tedd.DotNetLab.Profiler.so"
    {
        clang++ --version
        pkg-config --modversion openssl
        sha256sum "$DOTNETLAB_LINUX_PROFILER_PATH" "$(readlink -f /usr/lib/x86_64-linux-gnu/libcrypto.so)"
        ldd "$DOTNETLAB_LINUX_PROFILER_PATH"
    } >> "$task_run/platform.txt"
fi
dotnet build Tedd.DotNetLab.sln -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false -p:DotNetLabBuildRoot="$task_root" > "$task_run/build.log" 2>&1 || { cat "$task_run/build.log"; exit 1; }
task_exit=0
task_filter=${DOTNETLAB_LINUX_TEST_FILTER:-'FullyQualifiedName~Tedd.DotNetLab.Tests.PeerIdentityTests|FullyQualifiedName~Tedd.DotNetLab.Tests.ProcessIdentityTests|FullyQualifiedName~Tedd.DotNetLab.Tests.HarnessTests|FullyQualifiedName~Tedd.DotNetLab.Tests.CaptureTests|FullyQualifiedName~OwnedCleanupTests.CleanupCannotDeleteItsOwnerOrSibling|FullyQualifiedName~ExecutionQualificationTests|FullyQualifiedName~PlatformExecutionTests|FullyQualifiedName~IndividualLatencyTests|FullyQualifiedName~QuantileHarnessTests|FullyQualifiedName~HarnessListenerLifetimeTests|FullyQualifiedName~CatalogMigrationTests.OperationStatusContention_IsReadOnlyAndBounded|FullyQualifiedName~ServiceTests.Execution_NotebookAndComparison_SurviveControllerRestart|FullyQualifiedName~ServiceTests.Execution_ObservationGrant_IsDenied'}
if [[ ${DOTNETLAB_LINUX_NATIVE_STARTUP:-0} == 1 && -z ${DOTNETLAB_LINUX_TEST_FILTER:-} ]]; then task_filter="$task_filter|FullyQualifiedName~NativeStartupTests|FullyQualifiedName~PlatformSnapshotTests|FullyQualifiedName~RejitTests|FullyQualifiedName~InlinerTests|FullyQualifiedName~MutationServiceTests|FullyQualifiedName~MutationRecoveryTests|FullyQualifiedName~LiveProbeTests|FullyQualifiedName~ProbeServiceTests|FullyQualifiedName~FaultProbeBoundaryTests|FullyQualifiedName~LiveProfilerTests|FullyQualifiedName~CatalogFileSystemTests|FullyQualifiedName~CatalogMigrationTests|FullyQualifiedName~CompilationControlTests|FullyQualifiedName~CompilationAdmissionTests|FullyQualifiedName~NativeTrialTests|FullyQualifiedName~NativeSeriesTests|FullyQualifiedName~NativeDiagnosticExecutionTests"; fi
if [[ -z ${DOTNETLAB_LINUX_TEST_FILTER:-} ]]; then task_filter="$task_filter|FullyQualifiedName~ConfirmationTests|FullyQualifiedName~TraceWorkerTests|FullyQualifiedName~ParserSchedulingTests|FullyQualifiedName~Tedd.DotNetLab.Tests.DiagnosticExecutionTests|FullyQualifiedName~WindowTests.CapturedGcStimulus|FullyQualifiedName~DiagnosticObjectiveTests.LiveCapturedGcGuardrail|FullyQualifiedName~CounterTests.ModernRuntimeCapture"; fi
printf 'Selected test filter: %s\n' "$task_filter" >> "$task_run/platform.txt"
task_diagnostics=()
if [[ ${DOTNETLAB_LINUX_CRASH_DIAGNOSTICS:-0} == 1 ]]; then
    export VSTEST_DUMP_PATH="$task_run/dumps"
    mkdir -m 0700 "$VSTEST_DUMP_PATH"
    task_diagnostics=(--blame-crash --blame-crash-dump-type mini --diag "$task_run/vstest.log")
fi
dotnet test tests/Tedd.DotNetLab.Tests/Tedd.DotNetLab.Tests.csproj -c Release --no-build -p:DotNetLabBuildRoot="$task_root" \
    --filter "$task_filter" "${task_diagnostics[@]}" \
    --logger 'trx;LogFileName=results.trx' --results-directory "$task_run" > "$task_run/tests.log" 2>&1 || task_exit=$?
cat "$task_run/tests.log"
python3 tests/test_evidence_audit.py --spec 02-functional-system-specification.md --test-plan 03-unit-test-plan.md \
    --declarations "$DOTNETLAB_TEST_METADATA_PATH" --trx "$task_run/results.trx" --output "$task_run/requirement-evidence.json"
printf 'Linux qualification evidence: %s\n' "$task_run"
exit "$task_exit"
