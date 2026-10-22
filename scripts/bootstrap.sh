#!/usr/bin/env bash
# Creates the solution file from the project tree and runs restore/build/test.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f AtmNexus.sln ] || dotnet new sln -n AtmNexus
dotnet sln AtmNexus.sln add $(find src tests -name '*.csproj')
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --logger "trx;LogFileName=results.trx"
