<# : batch part -- cmd.exe runs these lines; to PowerShell they are a comment
@echo off
rem miniVivisection: removes a copy installed from the zip, before the Rhino package (.yak).
rem One file: double-click it. It shows what it found and asks first. The PowerShell script
rem below this part is the tool; cmd.exe only starts it. Options (from a console): -ListOnly,
rem -Yes, -DeleteFiles, -ResetSettings, -IncludePackage.
set "MV_SELF=%~f0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create([IO.File]::ReadAllText($env:MV_SELF))) %*"
set MV_EXIT=%ERRORLEVEL%
if "%~1"=="" (echo. & pause)
exit /b %MV_EXIT%
#>
<#
.SYNOPSIS
  Finds miniVivisection installed from the zip (or an older ScriptEditorWatchViz, or a build
  folder) and removes it, so the Package Manager's version starts fresh.

.DESCRIPTION
  A zip copy and the Package Manager's package have the same plug-in id: Rhino would find the
  plug-in twice. This script looks for
    - Rhino's registrations of the plug-in (HKCU\Software\McNeel\Rhinoceros\<version>\Plug-Ins\
      c198ec17-...), every Rhino version; one pointing into the Package Manager's folder is the
      package itself and is kept (unless -IncludePackage);
    - Grasshopper links (*.ghlink in %APPDATA%\Grasshopper\Libraries) to a folder with
      miniVivisection's Grasshopper part, and copies of that part (.gha) in Libraries;
    - with -DeleteFiles, the zip copies' folders (never a build folder, bin\Debug or bin\Release);
    - with -ResetSettings, the plug-in's settings (kept otherwise: the package uses them too);
    - with -IncludePackage, the Package Manager's package as well (Yak.exe uninstall).
  It lists what it found and asks before removing anything. Nothing is deleted: registry keys
  are exported as .reg files and files and folders are moved, into a backup folder
  (%TEMP%\miniVivisection_removed_<time>), so all of it can be put back.

  Rhino must be closed: Rhino writes its registrations back when it exits.

.EXAMPLE
  Double-click miniVivisection_Remove_Old_Install.cmd (one file: the part above starts this script).

.EXAMPLE
  miniVivisection_Remove_Old_Install.cmd -ListOnly    (in a console: only lists, doesn't pause)
#>
[CmdletBinding()]
param(
    [switch]$ListOnly,        # only show what is found
    [switch]$Yes,             # don't ask
    [switch]$DeleteFiles,     # also move the zip copies' folders to the backup
    [switch]$ResetSettings,   # also the plug-in's settings
    [switch]$IncludePackage   # also the Package Manager's package
)

$ErrorActionPreference = 'Stop'
# The folder this tool runs from (the .cmd's; MV_SELF is set by its batch part).
$SelfFolder = if ($env:MV_SELF) { Split-Path $env:MV_SELF -Parent } else { $PSScriptRoot }
# Written for Windows PowerShell 5.1 as well as PowerShell 7: no ?? or ?: here.

$PlugInId = 'c198ec17-3078-46a0-8f9b-94a40e9ba298'
$GhaNames = @('miniVivisection.GH.gha', 'ScriptEditorWatchViz.GH.gha', 'ScriptEditorWatchViz.gha')
$RhpNames = @('miniVivisection.rhp', 'ScriptEditorWatchViz.rhp')
$AppData = [Environment]::GetFolderPath('ApplicationData')
$Packages = Join-Path $AppData 'McNeel\Rhinoceros\packages'
$Libraries = Join-Path $AppData 'Grasshopper\Libraries'
$RhinoKey = 'HKCU:\Software\McNeel\Rhinoceros'

function Write-Note([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }

function Test-Under([string]$path, [string]$folder) {
    try {
        $full = [IO.Path]::GetFullPath($path)
        $root = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
        return $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Test-BuildFolder([string]$folder) {
    # A development build: ...\bin\Debug\... or ...\bin\Release\..., or a project file nearby.
    if ($folder -match '\\bin\\(Debug|Release)(\\|$)') { return $true }
    $dir = $folder
    for ($i = 0; $i -lt 4 -and $dir; $i++) {
        if (Get-ChildItem -LiteralPath $dir -Filter *.csproj -ErrorAction SilentlyContinue) { return $true }
        $dir = Split-Path $dir -Parent
    }
    return $false
}

# ---------------------------------------------------------------- Rhino must be closed
$rhinos = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Rhino*' -and $_.ProcessName -notlike 'RhinoCode*' })
if ($rhinos.Count -gt 0 -and -not $ListOnly) {
    Write-Note ('Rhino is running (' + (($rhinos | ForEach-Object { $_.ProcessName + ' ' + $_.Id }) -join ', ') + '). Close it first: it writes its plug-in registrations back when it exits.') 'Yellow'
    exit 2
}

# ---------------------------------------------------------------- find
$found = New-Object System.Collections.ArrayList   # items: Kind, What, Path, Remove (bool), Note
function Add-Item($kind, $what, $path, $remove, $note) {
    [void]$found.Add([pscustomobject]@{ Kind = $kind; What = $what; Path = $path; Remove = $remove; Note = $note })
}

$zipFolders = @{}
if (Test-Path $RhinoKey) {
    foreach ($version in Get-ChildItem $RhinoKey -ErrorAction SilentlyContinue) {
        $key = Join-Path $version.PSPath ('Plug-Ins\' + $PlugInId)
        if (-not (Test-Path $key)) { continue }
        $file = $null
        try { $file = (Get-ItemProperty (Join-Path $key 'PlugIn') -ErrorAction Stop).FileName } catch { }
        $label = 'Rhino ' + $version.PSChildName + ' registration'
        if ($file -and (Test-Under $file $Packages)) {
            Add-Item 'registry' $label $key ([bool]$IncludePackage) ('the Package Manager''s package: ' + $file)
            continue
        }
        $note = if ($file) { $file } else { '(no file name)' }
        if ($file -and -not (Test-Path -LiteralPath $file)) { $note += '  (the file is gone)' }
        Add-Item 'registry' $label $key $true $note
        if ($file -and (Test-Path -LiteralPath $file)) {
            $folder = Split-Path $file -Parent
            if (Test-BuildFolder $folder) {
                Add-Item 'info' 'Build folder' $folder $false 'a development build: its files stay; only its registration is removed'
            } else {
                $zipFolders[$folder] = $true
            }
        }
    }
}
foreach ($root in 'HKLM:\SOFTWARE\McNeel\Rhinoceros', 'HKLM:\SOFTWARE\WOW6432Node\McNeel\Rhinoceros') {
    if (-not (Test-Path $root)) { continue }
    foreach ($version in Get-ChildItem $root -ErrorAction SilentlyContinue) {
        $key = Join-Path $version.PSPath ('Plug-Ins\' + $PlugInId)
        if (Test-Path $key) {
            Add-Item 'info' ('Rhino ' + $version.PSChildName + ' registration for all users') $key $false 'needs an administrator: remove it in Rhino''s PlugInManager'
        }
    }
}

if (Test-Path -LiteralPath $Libraries) {
    foreach ($link in Get-ChildItem -LiteralPath $Libraries -Filter *.ghlink -File -ErrorAction SilentlyContinue) {
        $targets = @(Get-Content -LiteralPath $link.FullName -ErrorAction SilentlyContinue | Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } | ForEach-Object { $_.Trim() })
        $ours = $link.BaseName -match '^(miniVivisection|ScriptEditorWatchViz)$'
        foreach ($target in $targets) {
            foreach ($gha in $GhaNames) {
                if (Test-Path -LiteralPath (Join-Path $target $gha)) { $ours = $true }
            }
        }
        if ($ours) { Add-Item 'file' 'Grasshopper link' $link.FullName $true ('to ' + ($targets -join '; ')) }
    }
    # By name, not -Include: Windows PowerShell 5.1 ignores -Include with -LiteralPath and
    # would list every file in Libraries -- other plug-ins' too.
    foreach ($gha in Get-ChildItem -LiteralPath $Libraries -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $GhaNames -contains $_.Name }) {
        Add-Item 'file' 'Grasshopper component copy' $gha.FullName $true 'a copy of miniVivisection''s Grasshopper part in Libraries'
    }
}

foreach ($folder in $zipFolders.Keys) {
    if ($DeleteFiles -and ((Test-Under $SelfFolder $folder) -or ([IO.Path]::GetFullPath($SelfFolder).TrimEnd('\') -ieq [IO.Path]::GetFullPath($folder).TrimEnd('\')))) {
        Add-Item 'info' 'Zip copy' $folder $false 'this tool runs from it: delete the folder by hand afterwards'
    } elseif ($DeleteFiles) {
        Add-Item 'folder' 'Zip copy' $folder $true 'its files'
    } else {
        Add-Item 'info' 'Zip copy' $folder $false 'its files stay (-DeleteFiles moves them to the backup too)'
    }
}

$yak = $null
foreach ($candidate in @('C:\Program Files\Rhino 8\System\Yak.exe', 'C:\Program Files\Rhino 9 WIP\System\Yak.exe', 'C:\Program Files\Rhino 9\System\Yak.exe')) {
    if (-not $yak -and (Test-Path -LiteralPath $candidate)) { $yak = $candidate }
}
if (Test-Path -LiteralPath $Packages) {
    foreach ($package in Get-ChildItem -LiteralPath $Packages -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'miniVivisection' } | Where-Object { Test-Path -LiteralPath $_ }) {
        $versions = (Get-ChildItem -LiteralPath $package -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', '
        if ($IncludePackage) {
            Add-Item 'package' 'Package Manager package' $package $true ('version(s) ' + $versions + '; uninstalled with Yak.exe')
        } else {
            Add-Item 'info' 'Package Manager package' $package $false ('version(s) ' + $versions + '; kept (-IncludePackage removes it too)')
        }
    }
}

if ($ResetSettings) {
    foreach ($version in Get-ChildItem (Join-Path $AppData 'McNeel\Rhinoceros') -Directory -ErrorAction SilentlyContinue) {
        $plugins = Join-Path $version.FullName 'Plug-ins'
        foreach ($settings in Get-ChildItem -LiteralPath $plugins -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ('*(' + $PlugInId + ')') }) {
            Add-Item 'folder' ('Settings, Rhino ' + $version.Name) $settings.FullName $true $settings.Name
        }
    }
    if (Test-Path $RhinoKey) {
        foreach ($version in Get-ChildItem $RhinoKey -ErrorAction SilentlyContinue) {
            $key = Join-Path $version.PSPath ('Global Options\Plug-ins\' + $PlugInId)
            if (Test-Path $key) { Add-Item 'registry' ('Rhino ' + $version.PSChildName + ' load option') $key $true 'LoadProtection' }
        }
    }
}

# ---------------------------------------------------------------- show
Write-Note ''
Write-Note 'miniVivisection: an existing installation' 'Cyan'
if ($found.Count -eq 0) {
    Write-Note '  Nothing found: install the package as is -- drag the miniVivisection .yak onto Rhino 8''s window, then restart Rhino.' 'Green'
    exit 0
}
$toRemove = @($found | Where-Object { $_.Remove })
foreach ($item in $found) {
    $mark = if ($item.Remove) { '  remove ' } else { '  keep   ' }
    $color = if ($item.Remove) { 'White' } else { 'DarkGray' }
    Write-Note ($mark + $item.What + ': ' + ($item.Path -replace '^Microsoft\.PowerShell\.Core\\Registry::', '')) $color
    if ($item.Note) { Write-Note ('           ' + $item.Note) 'DarkGray' }
}
if ($toRemove.Count -eq 0) {
    Write-Note '  Nothing to remove.' 'Green'
    exit 0
}
if ($ListOnly) { exit 0 }
if (-not $Yes) {
    $answer = Read-Host ('Remove these ' + $toRemove.Count + ' item(s)? Everything goes to a backup folder first. [y/N]')
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Note 'Nothing changed.'; exit 1 }
}

# ---------------------------------------------------------------- remove (into the backup)
$backup = Join-Path ([IO.Path]::GetTempPath()) ('miniVivisection_removed_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$log = New-Object System.Collections.ArrayList
$failed = 0
$n = 0
foreach ($item in $toRemove) {
    $n++
    try {
        switch ($item.Kind) {
            'registry' {
                $native = ($item.Path -replace '^Microsoft\.PowerShell\.Core\\Registry::', '') -replace '^HKCU:', 'HKEY_CURRENT_USER'
                $reg = Join-Path $backup ('{0:00}_registry.reg' -f $n)
                & reg.exe export $native $reg /y | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'reg export failed' }
                Remove-Item -LiteralPath $item.Path -Recurse -Force
                [void]$log.Add('registry ' + $native + '  ->  ' + $reg + '  (restore: reg import "' + $reg + '")')
            }
            { $_ -eq 'file' -or $_ -eq 'folder' } {
                $target = Join-Path $backup ('{0:00}_{1}' -f $n, (Split-Path $item.Path -Leaf))
                Move-Item -LiteralPath $item.Path -Destination $target -Force
                [void]$log.Add($item.Kind + ' ' + $item.Path + '  ->  ' + $target)
            }
            'package' {
                if (-not $yak) { throw 'no Yak.exe found (Rhino 8\System)' }
                & $yak uninstall miniVivisection | Out-Host
                if ($LASTEXITCODE -ne 0) { throw 'Yak.exe uninstall failed' }
                [void]$log.Add('package miniVivisection uninstalled with ' + $yak + ' (install it again with the Package Manager)')
            }
        }
        Write-Note ('  removed ' + $item.What) 'Green'
    } catch {
        $failed++
        Write-Note ('  FAILED  ' + $item.What + ': ' + $_.Exception.Message) 'Red'
        [void]$log.Add('FAILED ' + $item.What + ' ' + $item.Path + ': ' + $_.Exception.Message)
    }
}
$log | Set-Content -LiteralPath (Join-Path $backup 'removed.txt') -Encoding UTF8
Write-Note ''
Write-Note ('Backup: ' + $backup + '  (removed.txt says what came from where)') 'Cyan'
if ($failed -gt 0) {
    Write-Note ($failed.ToString() + ' item(s) could not be removed (see above).') 'Red'
    exit 3
}
Write-Note 'Done. Now drag the miniVivisection .yak onto Rhino 8''s window (or install it with the Package Manager), then restart Rhino.' 'Green'
exit 0
