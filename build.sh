#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec dotnet test --solution "$root/Qyl.Telemetry.Tests.slnx" -c Release --report-trx --results-directory "$root/artifacts/results" "$@"
