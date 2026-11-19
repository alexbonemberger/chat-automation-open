#requires -Version 7.0
# Abre sessão persistente: use "chat00", setText(...), chat00.setText(...), etc.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAutomation\ChatAutomation.csproj'
& dotnet run --project $project -- shell
if ($LASTEXITCODE -ne 0) { throw "chat-automation shell falhou (exit=$LASTEXITCODE)" }
