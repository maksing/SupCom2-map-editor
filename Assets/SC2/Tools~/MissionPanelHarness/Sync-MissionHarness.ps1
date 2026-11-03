<#
.SYNOPSIS
  Copies the repository's Assets/SC2 code into a scratch Unity project and patches it for the mission panel harness, or restores it.

.DESCRIPTION
  -Plain      (the state a project must be left in): copy the code as it is, and remove every file of the harness.
  (default)   copy the code, then patch the mission panel's GUI calls so that every button, switch, text field and popup it draws is recorded
              (MissionProbe), generate HarnessDropdown from SC2RuntimePopups.cs, and add the harness scripts.

  The scratch project is shared with other work, so a patched project must never be left behind: Invoke-MissionHarness.ps1 always ends with -Plain.
  Copying is done with robocopy, never by piping text through PowerShell (that re-encodes lines and breaks compilation).
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [switch]$Plain,
    [switch]$Purge   # also delete files in the project's Scripts and Runtime that the repository no longer has (robocopy /MIR)
)
$ErrorActionPreference = 'Stop'

$repo      = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$sc2       = Join-Path $repo 'Assets\SC2'
$assets    = Join-Path $Project 'Assets'
$dst       = Join-Path $assets 'SC2'
$mission   = Join-Path $dst 'Scripts\Unified\Mission'
$editorDir = Join-Path $assets 'VerifyBuildScript\Editor'
if (-not (Test-Path $assets)) { throw "Not a Unity project: $Project" }

function Copy-Tree($from, $to, [string[]]$excludeFiles) {
    $mode = if ($Purge) { '/MIR' } else { '/E' }
    $roboArgs = @($from, $to, $mode, '/XF', '*.meta') + $excludeFiles + @('/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    robocopy @roboArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $from" }
}

# the code itself; /XF keeps the harness files of an earlier patched copy out of a purge
Copy-Tree (Join-Path $sc2 'Scripts') (Join-Path $dst 'Scripts') @('MissionProbe.cs', 'HarnessDropdown.cs')
Copy-Tree (Join-Path $sc2 'Runtime') (Join-Path $dst 'Runtime') @()

$harnessFiles = @(
    (Join-Path $mission 'MissionProbe.cs'), (Join-Path $mission 'MissionProbe.cs.meta'),
    (Join-Path $mission 'HarnessDropdown.cs'), (Join-Path $mission 'HarnessDropdown.cs.meta'),
    (Join-Path $editorDir 'SC2MissionClickCheck.cs'), (Join-Path $editorDir 'SC2MissionClickCheck.cs.meta'),
    (Join-Path $editorDir 'SC2MissionCatalogProbe.cs'), (Join-Path $editorDir 'SC2MissionCatalogProbe.cs.meta')
)

if ($Plain) {
    foreach ($f in $harnessFiles) { if (Test-Path $f) { Remove-Item $f -Force } }
    "plain: the project holds the repository's code and no harness file"
    return
}

# patch the panel's files: the GUI calls go through the probe, and the editor-only branches are switched off so that the player path runs
foreach ($f in Get-ChildItem $mission -Filter 'SC2MissionPanel*.cs') {
    $t = [System.IO.File]::ReadAllText($f.FullName)
    $t = $t -replace '\bGUILayout\.Button\(',    'MissionProbe.LButton('
    $t = $t -replace '\bGUI\.Button\(',           'MissionProbe.Button('
    $t = $t -replace '\bGUILayout\.Toggle\(',     'MissionProbe.LToggle('
    $t = $t -replace '\bGUI\.Toggle\(',           'MissionProbe.Toggle('
    $t = $t -replace '\bGUILayout\.TextField\(',  'MissionProbe.TextField('
    $t = $t -replace '\bGUILayout\.TextArea\(',   'MissionProbe.TextArea('
    $t = $t -replace '\bEditorGUILayout\.Popup\(', 'MissionProbe.Popup('
    $t = $t -replace '#if UNITY_EDITOR', '#if SC2_NEVER'
    [System.IO.File]::WriteAllText($f.FullName, $t)
}

# HarnessDropdown: the host's runtime dropdown list, with the screen size fixed to the harness window (1500 x 900)
$src = [System.IO.File]::ReadAllText((Join-Path $sc2 'Scripts\SC2RuntimePopups.cs')).Replace("`r`n", "`n")
$a = $src.IndexOf('internal static class SC2RuntimeDropdown')
$b = $src.IndexOf("/// <summary>`n/// Runtime replacement for the Editor")
if ($a -lt 0 -or $b -lt $a) { throw 'SC2RuntimePopups.cs no longer has the shape this script expects' }
$cls = $src.Substring($a, $b - $a).Replace('SC2RuntimeDropdown', 'HarnessDropdown').Replace('internal static class', 'public static class').Replace('Screen.width', '1500f').Replace('Screen.height', '900f')
[System.IO.File]::WriteAllText((Join-Path $mission 'HarnessDropdown.cs'), "using System.Collections.Generic;`nusing UnityEngine;`n`n" + $cls)

Copy-Item (Join-Path $PSScriptRoot 'Runtime\MissionProbe.cs') (Join-Path $mission 'MissionProbe.cs') -Force
New-Item -ItemType Directory -Force $editorDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Editor\SC2MissionClickCheck.cs')   (Join-Path $editorDir 'SC2MissionClickCheck.cs') -Force
Copy-Item (Join-Path $PSScriptRoot 'Editor\SC2MissionCatalogProbe.cs') (Join-Path $editorDir 'SC2MissionCatalogProbe.cs') -Force
"patched: the project holds the repository's code with the mission panel patched for the harness"
