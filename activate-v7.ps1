#requires -Version 7.0
[CmdletBinding()]
param([switch]$LaunchConsole)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Set-Location $Root

$Dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'V7_GIT_STATUS_FAILED' }
if ($Dirty.Count -gt 0) { throw 'V7_WORKTREE_DIRTY: preserve alterações antes de ativar.' }

$Branch = (git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) { throw 'V7_BRANCH_DISCOVERY_FAILED' }
if ($Branch -notin @('feat/projects-new-chat-registry', 'feat/project-compose-in-place-v7-1')) {
    throw "V7_WRONG_BRANCH: atual=$Branch; esperado=feat/projects-new-chat-registry ou feat/project-compose-in-place-v7-1."
}

$Commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $Commit.Length -ne 40) { throw 'V7_COMMIT_UNKNOWN' }

& (Join-Path $Root 'setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "V7_SELF_TEST_FAILED: exit=$LASTEXITCODE" }

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'V7_LOCALAPPDATA_NOT_FOUND'
}
$DataDirectory = Join-Path $env:LOCALAPPDATA 'ChatAutomation'
$ProjectData = Join-Path $DataDirectory 'projects.json'
$Preflight = [ordered]@{
    schema = 'chat-automation.v7.activation-preflight/1'
    status = 'PASS_COMPILE_AND_SELFTEST'
    branch = $Branch
    commit = $Commit
    projectStorePresent = (Test-Path -LiteralPath $ProjectData)
    chatsStoreUntouched = $true
    edgeTouched = $false
    messageSent = $false
}
Write-Output ('CHAT_AUTOMATION_V7_PREFLIGHT_JSON=' +
    ($Preflight | ConvertTo-Json -Compress -Depth 4))

if ($LaunchConsole) {
    & (Join-Path $Root 'chat-automation.ps1')
    if ($LASTEXITCODE -ne 0) { throw "V7_CONSOLE_FAILED: exit=$LASTEXITCODE" }
}
