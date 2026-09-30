#!/usr/bin/env bash
set -euo pipefail
task_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$task_root"
mkdir -p .tmp/temp .tmp/dotnet-home .tmp/nuget/http
export TMPDIR="$task_root/.tmp/temp"
export TMP="$TMPDIR" TEMP="$TMPDIR"
export DOTNET_CLI_HOME="$task_root/.tmp/dotnet-home"
export NUGET_PACKAGES="$task_root/.tmp/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$task_root/.tmp/nuget/http"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
if [[ -x "$task_root/.tmp/dotnet/dotnet" ]]; then
    task_dotnet="$task_root/.tmp/dotnet/dotnet"
else
    task_dotnet="$(command -v dotnet)"
fi
case "${1:-verify}" in
    verify)
        "$task_dotnet" restore VantreLingo.slnx --locked-mode --nologo
        "$task_dotnet" build VantreLingo.slnx -c Release --no-restore --nologo
        "$task_dotnet" run --project tests/VantreLingo.Core.Checks -c Release --no-build
        ;;
    publish)
        task_publish="$task_root/.tmp/publish/win-x64"
        rm -rf -- "$task_publish"
        "$task_dotnet" publish src/VantreLingo.Desktop -c Release --self-contained false \
            -p:RestoreLockedMode=true -o "$task_publish" --nologo
        mkdir -p "$task_root/artifacts"
        rm -rf -- "$task_root/artifacts/win-x64"
        mv -- "$task_publish" "$task_root/artifacts/win-x64"
        printf '运行文件：%s/artifacts/win-x64/VantreLingo.exe\n' "$task_root"
        ;;
    *) printf '用法：scripts/build.sh [verify|publish]\n' >&2; exit 2 ;;
esac
