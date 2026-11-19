#requires -Version 7.0
param(
    [ValidateSet('probe', 'send-three', 'self-test')]
    [string] $Mode = 'probe',
    [string] $Chat = 'chat00',
    [ValidateRange(20,5000)]
    [int] $IntervalMs = 20,
    [ValidateRange(3,120)]
    [int] $DurationSec = 15,
    [ValidateRange(100,10000)]
    [int] $StableIdleMs = 300,
    [ValidateRange(5,90)]
    [int] $AckTimeoutSec = 30,
    [ValidateRange(10,600)]
    [int] $ResponseTimeoutSec = 180,
    [switch] $AllowSend
)
$ErrorActionPreference = 'Stop'
$Project = Join-Path $PSScriptRoot 'tests\ChatAutomation.PollingLab\ChatAutomation.PollingLab.csproj'
if (-not (Test-Path -LiteralPath $Project)) {
    throw "Projeto de laboratório não encontrado: $Project"
}
$Arguments = @(
    'run', '--project', $Project, '--configuration', 'Release', '--',
    '--mode', $Mode, '--chat', $Chat,
    '--interval-ms', $IntervalMs,
    '--duration-sec', $DurationSec,
    '--stable-idle-ms', $StableIdleMs,
    '--ack-timeout-sec', $AckTimeoutSec,
    '--response-timeout-sec', $ResponseTimeoutSec
)
if ($AllowSend) {
    $Arguments += '--allow-send'
}
& dotnet @Arguments
if ($LASTEXITCODE -ne 0) {
    throw "PollingLab terminou com falha (exit=$LASTEXITCODE)."
}
