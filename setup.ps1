#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAutomation\ChatAutomation.csproj'
& dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore falhou (exit=$LASTEXITCODE)" }
& dotnet build $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build falhou (exit=$LASTEXITCODE)" }
& dotnet run --project $project --no-build --configuration Release -- self-test
if ($LASTEXITCODE -ne 0) { throw "self-test falhou (exit=$LASTEXITCODE)" }
