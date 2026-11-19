#requires -Version 7.0
param([string]$Name, [string]$Url, [string]$ProfileDirectory, [long]$WindowHwnd)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAutomation\ChatAutomation.csproj'
$arguments = @('run', '--project', $project, '--', 'bind-to-chat')
if ($PSBoundParameters.ContainsKey('Name')) { $arguments += @('--name', $Name) }
if ($PSBoundParameters.ContainsKey('Url')) { $arguments += @('--url', $Url) }
if ($PSBoundParameters.ContainsKey('ProfileDirectory')) { $arguments += @('--profile-directory', $ProfileDirectory) }
if ($PSBoundParameters.ContainsKey('WindowHwnd')) { $arguments += @('--window-hwnd', [string]$WindowHwnd) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "bind-to-chat falhou (exit=$LASTEXITCODE)" }
