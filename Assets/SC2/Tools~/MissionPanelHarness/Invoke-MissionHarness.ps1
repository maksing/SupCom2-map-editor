<#
.SYNOPSIS
  Runs one check of the mission panel in a scratch Unity project and prints its result lines.

.DESCRIPTION
  Modes:
    Ux        the canvas flows: a wire let go over empty canvas opens the node search, double click, Escape, Home, F, scrolling at the edge (about 2 minutes)
    Shots     a picture of every scenario, no clicks (about 10 minutes); with -ShotName only that scenario
    Library   clicks every node button of the library at every scroll position (about 15 minutes)
    Clicks    everything: pictures, a click on every control of every scenario, the flows (about 40 minutes)
    Catalog   compiles the Lua of every node inside Unity (the Mono runtime), several times each (about 2 minutes)
    Build     compiles a Windows player of the plain code (needs SC2VerifyStandaloneBuild in the project; about 5 minutes)

  The project is patched for the run and always restored to the plain code afterwards (also when the run fails).
  The pictures need a desktop session with a GPU: Shots, Library, Clicks and Ux open a real (hidden-input) editor window. Catalog and Build run with -nographics.
  The Unity project must not be open in another editor, and two runs on one project cannot overlap.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Ux', 'Shots', 'Library', 'Clicks', 'Catalog', 'Build')][string]$Mode,
    [string]$Project = $(if ($env:SC2_HARNESS_PROJECT) { $env:SC2_HARNESS_PROJECT } else { 'C:\temp\SC2UnityVerify45' }),
    [string]$Unity,
    [string]$Out = $(if ($env:MISSION_OUT) { $env:MISSION_OUT } else { Join-Path $env:TEMP 'mission_harness' }),
    [string]$ShotName,
    [int]$TimeoutMinutes = 60,
    [switch]$KeepPatched
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $Unity) {
    $version = ((Get-Content (Join-Path $repo 'ProjectSettings\ProjectVersion.txt') | Select-Object -First 1) -replace 'm_EditorVersion:\s*', '').Trim()
    $Unity = $env:UNITY_EXE
    if (-not $Unity) { $Unity = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe" }
}
if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity; pass -Unity or set UNITY_EXE" }
New-Item -ItemType Directory -Force $Out | Out-Null

$sync = Join-Path $PSScriptRoot 'Sync-MissionHarness.ps1'
$log  = Join-Path $Out ("unity_" + $Mode.ToLower() + ".log")
$env:MISSION_OUT = $Out
foreach ($v in 'MISSION_SHOTS_ONLY', 'MISSION_SHOT_NAME', 'MISSION_ONLY_PREFIX', 'MISSION_UX_ONLY') { Remove-Item "Env:\$v" -ErrorAction SilentlyContinue }

$method = 'SC2MissionClickCheck.Run'
$graphics = $true
switch ($Mode) {
    'Ux'      { $env:MISSION_UX_ONLY = '1' }
    'Shots'   { $env:MISSION_SHOTS_ONLY = '1'; if ($ShotName) { $env:MISSION_SHOT_NAME = $ShotName } }
    'Library' { $env:MISSION_ONLY_PREFIX = 'library_scroll' }
    'Clicks'  { }
    'Catalog' { $method = 'SC2MissionCatalogProbe.Run'; $graphics = $false }
    'Build'   { $method = 'SC2VerifyStandaloneBuild.Run'; $graphics = $false }
}
if ($Mode -ne 'Build') { Get-ChildItem $Out -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force }

$exit = 1
try {
    & $sync -Project $Project -Plain:($Mode -eq 'Build')
    $unityArgs = @('-batchmode')
    if (-not $graphics) { $unityArgs += '-nographics' }
    $unityArgs += @('-projectPath', $Project, '-executeMethod', $method, '-logFile', $log)
    Write-Host "Unity: $Unity`nlog:   $log"
    $p = Start-Process -FilePath $Unity -ArgumentList $unityArgs -PassThru -NoNewWindow
    if (-not $p.WaitForExit($TimeoutMinutes * 60 * 1000)) { $p.Kill(); throw "Timed out after $TimeoutMinutes minutes" }
    $p.WaitForExit()   # lets the exit code be read (a PowerShell quirk after a timed wait)
    $exit = [int]$p.ExitCode
}
finally {
    if (-not $KeepPatched) { & $sync -Project $Project -Plain | Out-Null }
}

# the result lines of the mode
$pattern = switch ($Mode) {
    'Catalog' { '^\[Catalog\]|error CS' }
    'Build'   { 'SC2VERIFY_|error CS' }
    default   { '^\[Click\] (FLOW|TOTAL|ERROR)|error CS' }
}
Select-String -Path $log -Pattern $pattern | ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
if ($Mode -in 'Shots', 'Library', 'Clicks') { Write-Host ("pictures: " + (@(Get-ChildItem $Out -Filter '*.png').Count) + " in $Out") }
if ($Mode -in 'Library', 'Clicks') { Write-Host "click report: $(Join-Path $Out 'click_report.txt')" }
Write-Host "exit code: $exit"
exit $exit
