#requires -Version 7.0
param(
    [Parameter(Mandatory, Position=0)]
    [ValidateSet('focus-current-chat','open-current-chat','detect-text-area','send-message',
        'inspect-edge','list-edge-windows','show-binding','clear-binding','self-test',
        'list-chats','refresh-chats','add-chat','get-text','clear-chat',
        'focus-chat','set-text','send-chat-message','open-chat','remove-chat','shell','help',
        'settings','set-send-human-text','is-busy','is-generating','busy-window',
        'check-max-limit','inspect-max-limit')]
    [string]$Command,
    [string]$Chat,
    [string]$Name,
    [string]$Url,
    [string]$Text,
    [ValidateSet('true','false')]
    [string]$Enabled,
    [string]$File,
    [switch]$Stdin,
    [string]$Mode,
    [long]$WindowHwnd,
    [string]$ProfileDirectory
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChatAutomation\ChatAutomation.csproj'
$arguments = @('run', '--project', $project, '--', $Command)
if ($PSBoundParameters.ContainsKey('Name')) { $arguments += @('--name', $Name) }
if ($PSBoundParameters.ContainsKey('Url')) { $arguments += @('--url', $Url) }
if ($PSBoundParameters.ContainsKey('Chat')) { $arguments += @('--chat', $Chat) }
if ($PSBoundParameters.ContainsKey('Text')) { $arguments += @('--text', $Text) }
if ($PSBoundParameters.ContainsKey('Enabled')) { $arguments += @('--enabled', $Enabled) }
if ($PSBoundParameters.ContainsKey('File')) { $arguments += @('--file', $File) }
if ($Stdin.IsPresent) { $arguments += '--stdin' }
if ($PSBoundParameters.ContainsKey('Mode')) { $arguments += @('--mode', $Mode) }
if ($PSBoundParameters.ContainsKey('WindowHwnd')) { $arguments += @('--window-hwnd', [string]$WindowHwnd) }
if ($PSBoundParameters.ContainsKey('ProfileDirectory')) { $arguments += @('--profile-directory', $ProfileDirectory) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "$Command falhou (exit=$LASTEXITCODE)" }
