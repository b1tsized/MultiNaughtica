<#
.SYNOPSIS
    One-click installer of MultiNaughtica for a friend (double-click Install.cmd, which starts this script).

.DESCRIPTION
    What it does, in order (each step says what it is doing in plain words):
      1. Looks for the two free Microsoft runtimes the program needs (.NET Runtime 10 and ASP.NET Core Runtime 10, 64 bit) and installs whichever is missing or too old:
         winget first, else the official installer from Microsoft's own download address (checked against the checksum Microsoft publishes).
      2. Copies the program into %LOCALAPPDATA%\MultiNaughtica (or the folder you pick), removes the "downloaded from the internet" mark from the files and makes a desktop and a Start menu
         shortcut. Running it again updates in place; the worlds and settings (in %APPDATA%\Nitrox) are never touched; a different MultiNaughtica build is only replaced after asking.
      3. Finds Subnautica (Steam registry, Steam library folders, Epic, common folders) and tells the launcher where it is (HKCU\SOFTWARE\Nitrox\PreferredGamePath).
      4. Asks for the server address the host gave you and puts it in the game's server list (Enter skips this).

    Testing without touching anything real:  -TestRoot <folder>  installs into that folder, puts the shortcuts and the two settings (game path, server list) there as files, and never
    installs a runtime.  -DryRun  only reports what would be done.  Both can be combined.

.PARAMETER PayloadDir      The folder with the program (default: the folder this script is in, the root of the unzipped zip).
.PARAMETER InstallDir      Where to install (default %LOCALAPPDATA%\MultiNaughtica). Asked for unless -Yes is given.
.PARAMETER TestRoot        Test mode, see above.
.PARAMETER DryRun          Only report.
.PARAMETER Yes             Take the default for every question (never replaces a different MultiNaughtica build without -Force).
.PARAMETER Force           Replace a different MultiNaughtica build without asking.
.PARAMETER GamePath        Use this Subnautica folder instead of searching.
.PARAMETER ServerAddress   The address the host gave you (address or address:port); skips the question.
.PARAMETER ServerName      Name of the server entry in the list (default "Co-op host").
.PARAMETER NoServer        Do not ask for a server address.
.PARAMETER SteamRoot       (tests) Use this folder as the Steam folder and ignore the real Steam.
.PARAMETER DotnetRoot      (tests) Look for the runtimes in this folder instead of C:\Program Files\dotnet.
.PARAMETER SkipRuntimes    Do not check the runtimes at all.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir = '',
    [string]$InstallDir = '',
    [string]$TestRoot = '',
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$Force,
    [string]$GamePath = '',
    [string]$ServerAddress = '',
    [string]$ServerName = 'Co-op host',
    [switch]$NoServer,
    [string]$SteamRoot = '',
    [string]$DotnetRoot = '',
    [switch]$SkipRuntimes
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Simulate = [bool]$DryRun
$script:LogFile = Join-Path $env:TEMP 'MultiNaughtica-install.log'
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:ProductName = 'MultiNaughtica'
$script:InstallerFiles = @('Install.cmd', 'install.ps1')   # not copied into the install folder (a new zip brings its own)
$script:ManifestName = 'installed-files.txt'
$script:Steps = 5

# ------------------------------------------------------------------------------------------------ output

function Write-Log([string]$text) {
    try { Add-Content -LiteralPath $script:LogFile -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text) -Encoding UTF8 } catch { }
}

function Say([string]$text, [string]$color = '') {
    if ($color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
    Write-Log $text
}

function Say-Step([int]$number, [string]$text) {
    Say ''
    Say ("Step {0} of {1}: {2}" -f $number, $script:Steps, $text) 'Cyan'
}

function Say-Warn([string]$text) {
    $script:Warnings.Add($text)
    Say ("  ! " + $text) 'Yellow'
}

function Say-Dry([string]$text) {
    Say ("  [dry run] would: " + $text) 'DarkGray'
}

function Ask([string]$question, [string]$default = '') {
    # A question with a default (Enter takes it). With -Yes the default is taken without asking.
    if ($Yes) { return $default }
    $hint = if ($default) { " [$default]" } else { '' }
    $answer = Read-Host ("  " + $question + $hint)
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

function Ask-YesNo([string]$question, [bool]$default) {
    if ($Yes) { return $default }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $answer = (Read-Host ("  " + $question + " " + $hint)).Trim().ToLowerInvariant()
        if ($answer -eq '') { return $default }
        if ($answer -in @('y', 'yes', 'j', 'ja')) { return $true }
        if ($answer -in @('n', 'no', 'nein')) { return $false }
        Say '  Please answer y or n.'
    }
}

# ------------------------------------------------------------------------------------------------ the runtimes

function Get-DotnetRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    if ($DotnetRoot) { $roots.Add($DotnetRoot); return $roots }
    if ($env:DOTNET_ROOT) { $roots.Add($env:DOTNET_ROOT) }
    $roots.Add((Join-Path $env:ProgramFiles 'dotnet'))
    try {
        $location = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction Stop).InstallLocation
        if ($location) { $roots.Add($location) }
    }
    catch { }
    return $roots
}

function Test-Runtime([string]$sharedName) {
    # True when a 10.x version of that shared framework is installed (the program is built for .NET 10; a newer major version does not run it).
    foreach ($root in (Get-DotnetRoots)) {
        $folder = Join-Path $root "shared\$sharedName"
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        $found = Get-ChildItem -LiteralPath $folder -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.\d+\.\d+' }
        # newest by version number (as text 10.0.5 would come after 10.0.13)
        if ($found) { return ($found | Sort-Object { ConvertTo-Version $_.Name } | Select-Object -Last 1).Name }
    }
    return $null
}

function ConvertTo-Version([string]$text) {
    return [version]($text -replace '-.*$', '')
}

function Test-RuntimeEnough([string]$sharedName, [string]$minimum) {
    $have = Test-Runtime $sharedName
    if (-not $have) { return $false }
    return (-not $minimum) -or ((ConvertTo-Version $have) -ge (ConvertTo-Version $minimum))
}

function Get-NetVersionForAspNet([string]$aspnetVersion) {
    # ASP.NET Core 10.0.N only runs on .NET 10.0.N or newer, and the program takes the newest ASP.NET Core it finds; its own settings file says which .NET it needs
    foreach ($root in (Get-DotnetRoots)) {
        $file = Join-Path $root "shared\Microsoft.AspNetCore.App\$aspnetVersion\Microsoft.AspNetCore.App.runtimeconfig.json"
        if (-not (Test-Path -LiteralPath $file)) { continue }
        try {
            $need = [string](Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).runtimeOptions.framework.version
            if ($need) { return $need }
        }
        catch { Write-Log "could not read ${file}: $($_.Exception.Message)" }
    }
    return $aspnetVersion   # Microsoft releases the two with the same number
}

function Install-RuntimeWithWinget([string]$packageId) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { return $false }
    Say "  Installing $packageId with winget (Windows may ask you to allow it)..."
    try {
        & $winget.Source install --id $packageId --exact --silent --accept-package-agreements --accept-source-agreements --source winget 2>&1 | ForEach-Object { Write-Log ([string]$_) }
        return ($LASTEXITCODE -eq 0)
    }
    catch { Write-Log "winget failed: $($_.Exception.Message)"; return $false }
}

function Get-OfficialRuntimeInstaller([string]$kind) {
    # Microsoft publishes the file names, download addresses and SHA512 checksums of every release at this address (their own download host, nowhere else is used)
    # (this PowerShell can default to TLS versions that the host refuses on Windows 10)
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 60
    $release = $metadata.releases | Select-Object -First 1
    $section = if ($kind -eq 'aspnet') { $release.'aspnetcore-runtime' } else { $release.runtime }
    $wanted = if ($kind -eq 'aspnet') { 'aspnetcore-runtime-win-x64.exe' } else { 'dotnet-runtime-win-x64.exe' }
    $file = $section.files | Where-Object { $_.name -eq $wanted } | Select-Object -First 1
    if (-not $file) { throw "Microsoft's release list has no $wanted" }
    if ($file.url -notmatch '^https://(builds\.dotnet\.microsoft\.com|download\.visualstudio\.microsoft\.com|dotnetcli\.azureedge\.net)/') { throw "Unexpected download address in Microsoft's release list: $($file.url)" }
    return [pscustomobject]@{ Name = $wanted; Url = $file.url; Sha512 = $file.hash.ToLowerInvariant() }
}

function Install-RuntimeFromMicrosoft([string]$kind, [string]$label) {
    # Downloads the installer from Microsoft into a folder of its own, checks it against the checksum Microsoft publishes and runs it; an installer that does
    # not match is never run. It runs elevated, so the file stays open from the check until the installer has ended, shared for reading only: no other
    # program can change, rename or delete it in between and have its own file run as administrator. The folder is removed afterwards, whatever happened.
    Say "  Downloading the official $label installer from Microsoft..."
    $info = Get-OfficialRuntimeInstaller $kind
    $folder = Join-Path $env:TEMP ("MultiNaughtica-runtime-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $stream = $null
    try {
        $target = Join-Path $folder $info.Name
        Invoke-WebRequest -Uri $info.Url -OutFile $target -UseBasicParsing -TimeoutSec 600
        $stream = [System.IO.File]::Open($target, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $sha512 = [System.Security.Cryptography.SHA512]::Create()
        try { $hash = -join ($sha512.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
        finally { $sha512.Dispose() }
        if ($hash -ne $info.Sha512) { throw "The downloaded $label installer does not match the checksum Microsoft publishes. It was NOT run." }
        Say '  Checksum matches the one Microsoft publishes. Installing (Windows will ask for permission)...'
        $process = Start-Process -FilePath $target -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait -PassThru
        if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) { throw "The $label installer ended with code $($process.ExitCode)." }
    }
    finally {
        if ($stream) { $stream.Dispose() }
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-Runtime([string]$sharedName, [string]$label, [string]$wingetId, [string]$kind, [string]$minimum = '') {
    # $minimum: the oldest version that will do ('' = any 10.x)
    $have = Test-Runtime $sharedName
    if (Test-RuntimeEnough $sharedName $minimum) {
        if (-not $minimum) { Say "  $label $have is installed." 'Green' }
        return
    }
    if ($have) { Say "  $label $have is too old: the program needs $minimum or newer." 'Yellow' }
    else { Say "  $label 10 is missing." 'Yellow' }
    if ($script:Simulate -or $TestRoot) {
        Say-Dry "install $label 10 (winget id $wingetId, else the official installer from Microsoft)"
        return
    }
    $ok = Install-RuntimeWithWinget $wingetId
    if ($ok -and (Test-RuntimeEnough $sharedName $minimum)) { Say "  $label installed." 'Green'; return }
    if (-not $ok) { Say '  winget is not available or did not finish; using the official installer from Microsoft instead.' }
    Install-RuntimeFromMicrosoft $kind $label
    if (-not (Test-RuntimeEnough $sharedName $minimum)) { throw "$label 10 is still missing or too old after installing it. Restart the PC and run Install.cmd again." }
    Say "  $label installed." 'Green'
}

# ------------------------------------------------------------------------------------------------ Subnautica

function Test-SubnauticaFolder([string]$folder) {
    if ([string]::IsNullOrWhiteSpace($folder)) { return $false }
    # the first game (Subnautica.exe), not Below Zero (SubnauticaZero.exe)
    return (Test-Path -LiteralPath (Join-Path $folder 'Subnautica.exe')) -and (Test-Path -LiteralPath (Join-Path $folder 'Subnautica_Data')) -and -not (Test-Path -LiteralPath (Join-Path $folder 'SubnauticaZero.exe'))
}

function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    if ($SteamRoot) { $roots.Add($SteamRoot) }
    else {
        foreach ($entry in @(@('HKCU:\Software\Valve\Steam', 'SteamPath'), @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'), @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath'))) {
            try { $value = (Get-ItemProperty -LiteralPath $entry[0] -Name $entry[1] -ErrorAction Stop).($entry[1]); if ($value) { $roots.Add(($value -replace '/', '\')) } } catch { }
        }
        $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam'))
        $roots.Add((Join-Path $env:ProgramFiles 'Steam'))
    }
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
        if (-not $libraries.Contains($root)) { $libraries.Add($root) }
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($line in (Get-Content -LiteralPath $vdf -ErrorAction SilentlyContinue)) {
                if ($line -match '^\s*"path"\s+"(?<p>[^"]+)"') {
                    $path = ($Matches['p'] -replace '\\\\', '\')
                    if (-not $libraries.Contains($path)) { $libraries.Add($path) }
                }
            }
        }
    }
    return $libraries
}

function Find-Subnautica {
    $candidates = New-Object System.Collections.Generic.List[string]
    # Steam: every library folder; the folder name is normally "Subnautica" but the app manifest (app 264710) says for sure
    foreach ($library in (Get-SteamLibraries)) {
        $manifest = Join-Path $library 'steamapps\appmanifest_264710.acf'
        if (Test-Path -LiteralPath $manifest) {
            $text = Get-Content -LiteralPath $manifest -Raw -ErrorAction SilentlyContinue
            if ($text -and $text -match '"installdir"\s+"(?<d>[^"]+)"') { $candidates.Add((Join-Path $library "steamapps\common\$($Matches['d'])")) }
        }
        $candidates.Add((Join-Path $library 'steamapps\common\Subnautica'))
    }
    if (-not $SteamRoot) {
        # Epic Games Store
        $epic = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
        if (Test-Path -LiteralPath $epic) {
            foreach ($item in (Get-ChildItem -LiteralPath $epic -Filter '*.item' -ErrorAction SilentlyContinue)) {
                try {
                    $json = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
                    if ($json.DisplayName -eq 'Subnautica' -and $json.InstallLocation) { $candidates.Add($json.InstallLocation) }
                }
                catch { }
            }
        }
        # the usual places on every fixed drive
        foreach ($drive in [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady }) {
            $letter = $drive.Name
            foreach ($relative in @('SteamLibrary\steamapps\common\Subnautica', 'Steam\steamapps\common\Subnautica', 'Program Files (x86)\Steam\steamapps\common\Subnautica', 'Program Files\Steam\steamapps\common\Subnautica',
                                    'Games\Subnautica', 'Program Files\Epic Games\Subnautica', 'Epic Games\Subnautica', 'GOG Games\Subnautica', 'Program Files (x86)\GOG Galaxy\Games\Subnautica')) {
                $candidates.Add((Join-Path $letter $relative))
            }
        }
    }
    foreach ($candidate in $candidates) {
        if (Test-SubnauticaFolder $candidate) { return (Resolve-Path -LiteralPath $candidate).Path.TrimEnd('\') }
    }
    return $null
}

function Get-StoredGamePath {
    if ($TestRoot) {
        $file = Join-Path $TestRoot 'Settings\registry.txt'
        if (Test-Path -LiteralPath $file) { foreach ($line in (Get-Content -LiteralPath $file)) { if ($line -like 'PreferredGamePath=*') { return $line.Substring('PreferredGamePath='.Length) } } }
        return ''
    }
    try { return [string](Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Nitrox' -Name PreferredGamePath -ErrorAction Stop).PreferredGamePath } catch { return '' }
}

function Set-StoredGamePath([string]$path) {
    if ($TestRoot) {
        $dir = Join-Path $TestRoot 'Settings'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'registry.txt') -Value "PreferredGamePath=$path" -Encoding UTF8
        return
    }
    if (-not (Test-Path -LiteralPath 'HKCU:\SOFTWARE\Nitrox')) { New-Item -Path 'HKCU:\SOFTWARE\Nitrox' -Force | Out-Null }
    Set-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Nitrox' -Name PreferredGamePath -Value $path -Type String
}

# ------------------------------------------------------------------------------------------------ the server list

function Get-ServersFile {
    if ($TestRoot) { return (Join-Path $TestRoot 'Settings\servers') }
    return (Join-Path $env:APPDATA 'Nitrox\servers')
}

function ConvertTo-ServerEntry([string]$text, [string]$name) {
    # "host", "host:port" -> @{ Name; Address; Port } or $null when it is not an address
    $text = $text.Trim()
    if ($text -notmatch '^(?<host>[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?)(:(?<port>\d{1,5}))?$') { return $null }
    $port = 11000
    if ($Matches['port']) { $port = [int]$Matches['port'] }
    if ($port -lt 1 -or $port -gt 65535) { return $null }
    $cleanName = ($name -replace '[|\r\n]', ' ').Trim()
    if (-not $cleanName) { $cleanName = 'Co-op host' }
    return [pscustomobject]@{ Name = $cleanName; Address = $Matches['host']; Port = $port }
}

function Add-ServerEntry($entry) {
    $file = Get-ServersFile
    $lines = @()
    if (Test-Path -LiteralPath $file) { $lines = @(Get-Content -LiteralPath $file) }
    else { $lines = @('Your server|127.0.0.1|11000') }   # the game creates this entry when it makes the list itself; a list that the installer starts keeps it
    foreach ($line in $lines) {
        $parts = $line.Split('|')
        if ($parts.Length -ge 2 -and $parts[1].Trim() -eq $entry.Address -and ($parts.Length -lt 3 -or $parts[2].Trim() -eq [string]$entry.Port)) { return $false }
    }
    $dir = Split-Path -Parent $file
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $newLine = "{0}|{1}|{2}" -f $entry.Name, $entry.Address, $entry.Port
    $content = (($lines + $newLine) -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($file, $content, (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

# ------------------------------------------------------------------------------------------------ files and shortcuts

function Get-PayloadFiles([string]$root) {
    $prefix = $root.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($prefix.Length)
        if ($script:InstallerFiles -contains $relative) { continue }
        [pscustomobject]@{ Relative = $relative; Full = $file.FullName }
    }
}

function Read-VersionLine([string]$folder) {
    $file = Join-Path $folder 'VERSION.txt'
    if (-not (Test-Path -LiteralPath $file)) { return '' }
    return ((Get-Content -LiteralPath $file -TotalCount 1) | Out-String).Trim()
}

function Get-ShortcutFolders {
    if ($TestRoot) { return [pscustomobject]@{ Desktop = (Join-Path $TestRoot 'Shortcuts\Desktop'); StartMenu = (Join-Path $TestRoot 'Shortcuts\StartMenu') } }
    return [pscustomobject]@{ Desktop = [Environment]::GetFolderPath('Desktop'); StartMenu = [Environment]::GetFolderPath('Programs') }
}

function New-Shortcut([string]$linkPath, [string]$target, [string]$workingDirectory, [string]$description) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $linkPath) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $target
    $link.WorkingDirectory = $workingDirectory
    $link.IconLocation = "$target,0"
    $link.Description = $description
    $link.Save()
}

function Test-ProgramRunning([string]$folder) {
    $running = @()
    foreach ($process in Get-Process -Name 'Nitrox.Launcher', 'Nitrox.Server.Subnautica' -ErrorAction SilentlyContinue) {
        try { if ($process.MainModule.FileName -like ($folder.TrimEnd('\') + '\*')) { $running += $process.ProcessName } } catch { }
    }
    return $running
}

# ================================================================================================ main

# (tests load the functions above without running the installer)
if ($env:NITROXCOOP_INSTALLER_LIBRARY -eq '1') { return }

try {
    Write-Log '---- install started'
    Say ''
    Say "=== $script:ProductName installer ===" 'Cyan'
    if ($TestRoot) { Say "TEST MODE: everything goes into $TestRoot; no shortcut, no setting and no runtime of this PC is touched." 'Yellow' }
    if ($DryRun) { Say 'DRY RUN: nothing will be changed, this only says what would happen.' 'Yellow' }

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This needs a 64-bit Windows.' }
    # ($PSScriptRoot is empty as a parameter default in Windows PowerShell 5.1)
    if (-not $PayloadDir) { $PayloadDir = $PSScriptRoot }
    if (-not (Test-Path -LiteralPath $PayloadDir)) { throw "The program folder $PayloadDir does not exist." }
    $PayloadDir = (Resolve-Path -LiteralPath $PayloadDir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDir 'Nitrox.Launcher.exe'))) {
        throw "Nitrox.Launcher.exe is not next to this installer ($PayloadDir). Unzip the whole zip first, then double-click Install.cmd in the unzipped folder."
    }
    if ($PayloadDir -like "$env:TEMP*" -or $PayloadDir -like '*.zip*') { Say-Warn 'It looks like you started this from inside the zip. If something fails, unzip the whole zip into a normal folder first.' }

    $newVersion = Read-VersionLine $PayloadDir
    if ($newVersion) { Say "Build to install: $newVersion" }

    # ---------------------------------------------------------------------------- 1 runtimes
    Say-Step 1 'checking the free Microsoft runtimes the program needs'
    if ($SkipRuntimes) { Say '  skipped (-SkipRuntimes).' }
    else {
        Ensure-Runtime 'Microsoft.NETCore.App' '.NET Runtime' 'Microsoft.DotNet.Runtime.10' 'runtime'
        Ensure-Runtime 'Microsoft.AspNetCore.App' 'ASP.NET Core Runtime' 'Microsoft.DotNet.AspNetCore.10' 'aspnet'
        $aspnet = Test-Runtime 'Microsoft.AspNetCore.App'
        if ($aspnet) { Ensure-Runtime 'Microsoft.NETCore.App' '.NET Runtime' 'Microsoft.DotNet.Runtime.10' 'runtime' (Get-NetVersionForAspNet $aspnet) }
    }

    # ---------------------------------------------------------------------------- 2 where to
    Say-Step 2 'installing the program'
    if (-not $InstallDir) {
        $InstallDir = if ($TestRoot) { Join-Path $TestRoot 'MultiNaughtica' } else { Join-Path $env:LOCALAPPDATA 'MultiNaughtica' }
        if (-not $TestRoot) { $InstallDir = Ask 'Install into this folder? Press Enter, or type another folder' $InstallDir }
    }
    $InstallDir = [System.IO.Path]::GetFullPath($InstallDir.Trim('"'))
    if ($InstallDir.StartsWith($PayloadDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $InstallDir.TrimEnd('\') -eq $PayloadDir.TrimEnd('\')) {
        throw 'Do not install into the unzipped folder itself. Choose another folder (the default is fine).'
    }
    Say "  Folder: $InstallDir"

    $manifestPath = Join-Path $InstallDir $script:ManifestName
    $oldVersion = Read-VersionLine $InstallDir
    $isUpdate = (Test-Path -LiteralPath $manifestPath)
    if ($isUpdate) {
        if ($oldVersion -and $newVersion -and $oldVersion -ne $newVersion) {
            Say-Warn "This PC already has a different build:  $oldVersion"
            Say "  You are installing:                         $newVersion"
            Say '  Everybody who plays together must use the same build. Your worlds and settings are kept either way.'
            if (-not $Force) {
                if ($Yes -or -not (Ask-YesNo 'Replace the installed build with this one?' $false)) { throw 'Nothing was changed: the installed build was kept. (Use -Force to replace it without asking.)' }
            }
        }
        else { Say '  The same build (or an unnamed one) is installed already: updating in place.' }
    }

    $running = @(Test-ProgramRunning $InstallDir)
    if ($running.Count -gt 0) { throw "Close the program first ($($running -join ', ') is running from $InstallDir), then run the installer again." }

    $files = @(Get-PayloadFiles $PayloadDir)
    Say "  $($files.Count) files to copy."
    if ($script:Simulate) { Say-Dry "copy $($files.Count) files to $InstallDir$(if ($isUpdate) { ', remove the files of the old build that the new one no longer has' })" }
    else {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        $oldFiles = @()
        if ($isUpdate) { $oldFiles = @(Get-Content -LiteralPath $manifestPath | Where-Object { $_ }) }
        $done = 0
        foreach ($file in $files) {
            $destination = Join-Path $InstallDir $file.Relative
            $directory = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
            Copy-Item -LiteralPath $file.Full -Destination $destination -Force
            $done++
            if ($done % 100 -eq 0) { Say "  ... $done of $($files.Count)" }
        }
        # files that belonged to the old build and are not in the new one: one by one, never a whole folder
        $newRelative = @{}
        foreach ($file in $files) { $newRelative[$file.Relative] = $true }
        $removed = 0
        foreach ($old in $oldFiles) {
            if (-not $newRelative.ContainsKey($old)) {
                $oldFull = Join-Path $InstallDir $old
                if (Test-Path -LiteralPath $oldFull -PathType Leaf) { Remove-Item -LiteralPath $oldFull -Force; $removed++ }
            }
        }
        if ($removed -gt 0) { Say "  Removed $removed file(s) of the old build that the new one does not have." }
        # the manifest says which files are ours (the uninstaller and the next update only touch these)
        Set-Content -LiteralPath $manifestPath -Value ($files | ForEach-Object { $_.Relative }) -Encoding UTF8
        # the Windows "downloaded from the internet" mark on the files (otherwise Windows asks about every file)
        Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
        Say '  Copied; the "downloaded from the internet" mark is removed from the files.' 'Green'
    }

    # ---------------------------------------------------------------------------- 3 shortcuts
    Say-Step 3 'making the shortcuts'
    $shortcuts = Get-ShortcutFolders
    $launcher = Join-Path $InstallDir 'Nitrox.Launcher.exe'
    $desktopLink = Join-Path $shortcuts.Desktop "$script:ProductName.lnk"
    $menuLink = Join-Path $shortcuts.StartMenu "$script:ProductName.lnk"
    $uninstallLink = Join-Path $shortcuts.StartMenu "Uninstall $script:ProductName.lnk"
    if ($script:Simulate) { Say-Dry "make the shortcuts '$script:ProductName' on the desktop and in the Start menu (and 'Uninstall $script:ProductName' in the Start menu)" }
    else {
        New-Shortcut $desktopLink $launcher $InstallDir 'MultiNaughtica, a Nitrox-based multiplayer mod for Subnautica'
        New-Shortcut $menuLink $launcher $InstallDir 'MultiNaughtica, a Nitrox-based multiplayer mod for Subnautica'
        if (Test-Path -LiteralPath (Join-Path $InstallDir 'Uninstall.cmd')) { New-Shortcut $uninstallLink (Join-Path $InstallDir 'Uninstall.cmd') $InstallDir "Remove $script:ProductName from this PC" }
        Say "  Desktop:    $desktopLink" 'Green'
        Say "  Start menu: $menuLink" 'Green'
    }

    # ---------------------------------------------------------------------------- 4 Subnautica
    Say-Step 4 'finding Subnautica'
    $game = $null
    if ($GamePath) {
        if (Test-SubnauticaFolder $GamePath) { $game = (Resolve-Path -LiteralPath $GamePath).Path.TrimEnd('\') } else { Say-Warn "'$GamePath' is not a Subnautica folder (it must hold Subnautica.exe and Subnautica_Data)." }
    }
    $stored = Get-StoredGamePath
    if (-not $game -and $stored -and (Test-SubnauticaFolder $stored)) { $game = $stored; Say "  The launcher already knows your game: $stored" 'Green' }
    if (-not $game) { $game = Find-Subnautica }
    if (-not $game -and -not $Yes) {
        Say '  Subnautica was not found automatically.' 'Yellow'
        while (-not $game) {
            $typed = Ask 'Type the folder that holds Subnautica.exe (Enter to skip; the launcher will ask you later)' ''
            if (-not $typed) { break }
            $typed = $typed.Trim('"')
            if (Test-SubnauticaFolder $typed) { $game = (Resolve-Path -LiteralPath $typed).Path.TrimEnd('\') } else { Say '  That folder has no Subnautica.exe (and Subnautica Below Zero does not work). Try again, or press Enter to skip.' }
        }
    }
    if ($game) {
        if ($stored -eq $game) { Say "  Game folder: $game (already set)" 'Green' }
        elseif ($script:Simulate) { Say-Dry "tell the launcher your game is in $game" }
        else {
            if ($stored -and (Test-SubnauticaFolder $stored)) { Say "  Found $game, but the launcher is already set to ${stored}, so that one is kept." }
            else { Set-StoredGamePath $game; Say "  Found Subnautica: $game  (the launcher knows it now)" 'Green' }
        }
    }
    else { Say-Warn 'Subnautica not found. The launcher will ask for its folder the first time (Options, Subnautica Installation).' }

    # ---------------------------------------------------------------------------- 5 server
    Say-Step 5 'the server of your host'
    $address = $ServerAddress
    if (-not $address -and -not $NoServer -and -not $Yes) {
        Say '  Your host gave you an address (like 203.0.113.5, or a name). Type it and press Enter, or just press Enter to skip.'
        $address = Ask 'Server address' ''
    }
    if ($address) {
        $entry = ConvertTo-ServerEntry $address $ServerName
        if (-not $entry) { Say-Warn "'$address' does not look like an address (use letters, digits, dots, hyphens, optionally :port). Not added; you can add it in the game's Multiplayer menu." }
        elseif ($script:Simulate) { Say-Dry "add '$($entry.Name)' ($($entry.Address):$($entry.Port)) to the game's server list" }
        else {
            if (Add-ServerEntry $entry) { Say "  Added '$($entry.Name)' ($($entry.Address):$($entry.Port)) to the game's server list." 'Green' }
            else { Say "  $($entry.Address):$($entry.Port) is in the server list already." }
        }
    }
    else { Say '  Skipped. You can add the server in the game later (Multiplayer, Add Server).' }

    # ---------------------------------------------------------------------------- done
    Say ''
    if ($script:Warnings.Count -gt 0) {
        Say 'Things to look at:' 'Yellow'
        foreach ($warning in $script:Warnings) { Say "  - $warning" 'Yellow' }
        Say ''
    }
    if ($script:Simulate) { Say 'Dry run finished: nothing was changed.' 'Yellow' }
    else {
        Say "Done, start $script:ProductName from your desktop." 'Green'
        Say '  (The first time you press Play, the launcher may ask to restart as administrator to get access to the game folder: that is normal, allow it.)'
        try { Copy-Item -LiteralPath $script:LogFile -Destination (Join-Path $InstallDir 'install.log') -Force } catch { }
    }
    exit 0
}
catch {
    Say ''
    Say ("Something went wrong: " + $_.Exception.Message) 'Red'
    Write-Log ($_ | Out-String)
    Say ("Details are in: " + $script:LogFile) 'Red'
    exit 1
}
