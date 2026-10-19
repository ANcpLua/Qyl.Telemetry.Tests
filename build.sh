#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$root"
dotnet tool restore
exec dotnet run --project eng/build/_build.csproj --configuration Release -- "$@"
