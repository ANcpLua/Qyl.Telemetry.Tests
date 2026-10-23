#!/bin/sh
set -eu
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-2}"
export MSBUILDDISABLENODEREUSE=1
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$root"
dotnet tool restore
exec dotnet run --project eng/build/_build.csproj --configuration Release -- "$@"
