param([string]$Distribution='Ubuntu-24.04',[string]$LinuxUser='tedd',[string]$Filter,[switch]$NativeStartup,[switch]$CrashDiagnostics,[switch]$NativeCatalogStorage=$true)
$ErrorActionPreference='Stop'
if($LinuxUser -eq 'root' -or $LinuxUser -notmatch '^[a-z_][a-z0-9_-]*$') { throw 'A regular Linux user is required.' }
$taskSdk='D:\Workspaces\AI\dotnetlab-implementation\linux-sdk'
$taskRuntime=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'linux-runtime.lock.json') -Raw | ConvertFrom-Json
$taskArchive=Join-Path $taskSdk 'dotnet-sdk-10.0.401-linux-x64.tar.gz'
if(-not (Test-Path -LiteralPath $taskArchive) -or (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $taskRuntime.archive_sha512) { throw 'The task SDK archive must match linux-runtime.lock.json.' }
if(-not (Test-Path -LiteralPath (Join-Path $taskSdk 'sdk\10.0.401\dotnet.dll'))) { throw 'Extract the hash-verified Linux SDK 10.0.401 into the task SDK directory first.' }
New-Item -ItemType Directory -Path 'D:\Workspaces\AI\dotnetlab-implementation\linux-sdk-exec','D:\Workspaces\AI\dotnetlab-linux\tmp' -Force | Out-Null
if($NativeStartup) { New-Item -ItemType Directory -Path 'D:\Workspaces\AI\dotnetlab-implementation\linux-native-build','D:\Workspaces\AI\dotnetlab-implementation\linux-native-exec' -Force | Out-Null }
# A single WSL invocation owns both mounts. No system packages are installed.
$taskCommand=@'
set -euo pipefail
task_user=__USER__
task_uid=$(id -u "$task_user")
task_gid=$(id -g "$task_user")
[[ $task_uid != 0 ]] || { echo 'Run qualification as a regular user.' >&2; exit 1; }
task_sdk=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-sdk-exec
task_tmp=/mnt/d/Workspaces/AI/dotnetlab-linux/tmp
task_sdk_mounted=0
task_tmp_mounted=0
task_native=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-native-exec
task_native_mounted=0
task_catalog=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux/tests
task_catalog_mounted=0
task_cgroup=/sys/fs/cgroup/dotnetlab-parser-task-$(cat /proc/sys/kernel/random/uuid)
task_cgroup_created=0
cleanup() {
    task_exit=$?
    task_cleanup_failed=0
    if [[ $task_cgroup_created == 1 ]]; then
        while IFS= read -r task_leaf; do
            if [[ -e $task_leaf/cgroup.kill ]]; then printf 1 > "$task_leaf/cgroup.kill" || task_cleanup_failed=1; fi
            rmdir "$task_leaf" || task_cleanup_failed=1
        done < <(find "$task_cgroup" -mindepth 1 -maxdepth 1 -type d)
        rmdir "$task_cgroup" || task_cleanup_failed=1
    fi
    if [[ $task_catalog_mounted == 1 ]]; then umount "$task_catalog" || task_cleanup_failed=1; fi
    if [[ $task_native_mounted == 1 ]]; then umount "$task_native" || task_cleanup_failed=1; fi
    if [[ $task_tmp_mounted == 1 ]]; then umount "$task_tmp" || task_cleanup_failed=1; fi
    if [[ $task_sdk_mounted == 1 ]]; then umount "$task_sdk" || task_cleanup_failed=1; fi
    if [[ $task_exit != 0 ]]; then return "$task_exit"; fi
    return "$task_cleanup_failed"
}
trap cleanup EXIT
if mountpoint -q "$task_sdk" || mountpoint -q "$task_tmp" || mountpoint -q "$task_catalog"; then
    echo 'A qualification mount is already active; do not overlap runs.' >&2
    exit 1
fi
mount -t drvfs 'D:\Workspaces\AI\dotnetlab-implementation\linux-sdk' "$task_sdk" -o "uid=$task_uid,gid=$task_gid,metadata,umask=022,fmask=0000"
task_sdk_mounted=1
chmod u+x "$task_sdk/dotnet"
if [[ ${3:-0} == 1 ]]; then chmod u+x "$task_sdk/shared/Microsoft.NETCore.App/10.0.12/createdump"; fi
mount -t tmpfs -o "size=256m,mode=0700,uid=$task_uid,gid=$task_gid,nodev,nosuid" tmpfs "$task_tmp"
task_tmp_mounted=1
if [[ ${4:-0} == 1 ]]; then
    task_catalog_image=/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-catalog-tests.ext4
    if [[ ! -e $task_catalog_image ]]; then
        ( set -o noclobber; : > "$task_catalog_image" )
        truncate -s 1G "$task_catalog_image"
        mkfs.ext4 -q -F -m 0 "$task_catalog_image"
    fi
    [[ -f $task_catalog_image && ! -L $task_catalog_image && $(blkid -p -s TYPE -o value "$task_catalog_image") == ext4 ]] || { echo 'Unqualified task catalog image.' >&2; exit 1; }
    mkdir -p "$task_catalog"
    mount -o loop,nodev,nosuid,noexec "$task_catalog_image" "$task_catalog"
    task_catalog_mounted=1
    chown "$task_uid:$task_gid" "$task_catalog"
fi
task_filter=${1:-}
if [[ $task_filter == __DOTNETLAB_DEFAULT_FILTER__ ]]; then task_filter=''; fi
if [[ ${2:-0} == 1 ]]; then
    if mountpoint -q "$task_native"; then echo 'Native qualification mount is already active.' >&2; exit 1; fi
    mount -t drvfs 'D:\Workspaces\AI\dotnetlab-implementation\linux-native-build' "$task_native" -o "uid=$task_uid,gid=$task_gid,metadata,umask=022,fmask=0000"
    task_native_mounted=1
fi
[[ $(stat -f -c %t /sys/fs/cgroup) == 63677270 ]] || { echo 'cgroup v2 is required for parser qualification.' >&2; exit 1; }
mkdir "$task_cgroup"; task_cgroup_created=1
printf '+memory +pids' > "$task_cgroup/cgroup.subtree_control"
chown "$task_uid:$task_gid" "$task_cgroup" "$task_cgroup/cgroup.procs" "$task_cgroup/cgroup.threads" "$task_cgroup/cgroup.subtree_control"
mkdir "$task_cgroup/controller"
(
    printf '%s' "$BASHPID" > "$task_cgroup/controller/cgroup.procs"
    exec runuser -u "$task_user" -- /usr/bin/env "DOTNETLAB_PARSER_CGROUP_ROOT=$task_cgroup" "DOTNETLAB_LINUX_TEST_FILTER=$task_filter" "DOTNETLAB_LINUX_NATIVE_STARTUP=${2:-0}" "DOTNETLAB_LINUX_CRASH_DIAGNOSTICS=${3:-0}" /bin/bash --noprofile --norc /mnt/d/SourceCode/Tedd.DotNetLab/tests/run-linux-transport.sh "$task_sdk"
)
'@
$taskFilterArgument=if($Filter) { $Filter } else { '__DOTNETLAB_DEFAULT_FILTER__' }
& wsl.exe --distribution $Distribution --user root --exec /bin/bash --noprofile --norc -c ($taskCommand.Replace('__USER__',$LinuxUser)) -- $taskFilterArgument ([int]$NativeStartup.IsPresent) ([int]$CrashDiagnostics.IsPresent) ([int]$NativeCatalogStorage.IsPresent)
if($LASTEXITCODE) { throw "Linux transport qualification failed: $LASTEXITCODE" }
