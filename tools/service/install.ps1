<#
.SYNOPSIS
    Installs the GeoConflux API host as a Windows service, so it survives a reboot.

.DESCRIPTION
    The published page is a build. The live system is this host, and a host started from a terminal
    dies with the terminal. This registers it with the service control manager instead: it starts at
    boot without anybody logging in, it restarts if it fails, and it stops cleanly on shutdown.

    It does not create, read, or write any credential. Credentials go in the service's own
    environment block after installation - see docs/operations/running-continuously.md.

    Re-running it against an existing service republishes the binaries and restarts, which is the
    upgrade path. It never touches the database: the service is stopped, the files are replaced, and
    it is started again against whatever was already there.

.PARAMETER Path
    Where the published binaries go. Must not be inside the repository - a publish directory under
    source control is a directory somebody eventually deletes.

.PARAMETER Url
    What the host listens on. Defaults to http://localhost:5266, which is reachable from the machine
    itself and from nowhere else.

.PARAMETER Uninstall
    Stops and removes the service. Leaves the published files and the database alone.

.EXAMPLE
    # From an elevated PowerShell, in the repository root:
    .\tools\service\install.ps1 -Path 'C:\GeoConflux'

.EXAMPLE
    .\tools\service\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $Path = 'C:\GeoConflux',
    [string] $Url = 'http://localhost:5266',
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'GeoConflux'
$DisplayName = 'GeoConflux geopolitical monitoring'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Registering a service needs an elevated PowerShell. Right-click, Run as administrator, and try again.'
    }
}

function Get-ServiceOrNull {
    try { Get-Service -Name $ServiceName -ErrorAction Stop } catch { $null }
}

Assert-Elevated

if ($Uninstall) {
    $existing = Get-ServiceOrNull

    if ($null -eq $existing) {
        Write-Host "$ServiceName is not installed; nothing to remove."
        return
    }

    if ($existing.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    & sc.exe delete $ServiceName | Out-Null
    Write-Host "$ServiceName removed. The published files and the database were left alone."
    return
}

$repository = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $repository 'src\Geopolitics.Api\Geopolitics.Api.csproj'

if (-not (Test-Path $project)) {
    throw "Could not find $project. Run this from a clone of the repository."
}

# A publish directory inside the working tree is a directory somebody eventually deletes with a
# `git clean`, taking the database beside it. Refuse rather than warn.
#
# Compared as text rather than by resolving, because the target may not exist yet. Written without
# the null-conditional operator so this runs on Windows PowerShell 5.1 as well as on 7.
$absolute = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))

if ($absolute.StartsWith($repository.Path, [StringComparison]::OrdinalIgnoreCase)) {
    throw "-Path must be outside the repository. The database lives beside the binaries, and a publish directory under source control does not survive a clean."
}

$existing = Get-ServiceOrNull

if ($null -ne $existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping the running $ServiceName before replacing its files..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Write-Host "Publishing to $Path..."
& dotnet publish $project --configuration Release --output $Path
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$executable = Join-Path $Path 'Geopolitics.Api.exe'
if (-not (Test-Path $executable)) { throw "Published output has no $executable." }

if ($null -eq $existing) {
    Write-Host "Registering $ServiceName..."

    # binPath needs the space after the equals sign: that is sc.exe's argument syntax, not a typo.
    & sc.exe create $ServiceName binPath= "`"$executable`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }

    & sc.exe description $ServiceName "Collects, correlates and publishes open-source geopolitical reporting. Data at $Path." | Out-Null

    # Restart after a minute, three times, with the counter resetting daily. A host that crashes
    # repeatedly should keep trying: the alternative is a machine that looks fine and is collecting
    # nothing, which is the failure this whole sprint exists to make visible.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
}
else {
    Write-Host "$ServiceName already exists; its files have been replaced."
}

# The service account has its own profile, so `dotnet user-secrets` - which lives in the profile of
# whoever typed it - is not readable from here. Configuration for a service goes in its environment
# block, and this sets the two values that are not secret. Add credentials the same way:
#
#   $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\GeoConflux'
#   $env = (Get-ItemProperty $key -Name Environment).Environment
#   Set-ItemProperty $key -Name Environment -Value ($env + 'Providers__Acled__Password=...')
#
$registry = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$environment = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    "ASPNETCORE_URLS=$Url"
)

$current = @()
try { $current = (Get-ItemProperty -Path $registry -Name Environment -ErrorAction Stop).Environment } catch { }

# Existing entries are kept, because they are where an operator put their credentials. Only the two
# this script owns are replaced.
$kept = @($current | Where-Object { $_ -notmatch '^(ASPNETCORE_ENVIRONMENT|ASPNETCORE_URLS)=' })
Set-ItemProperty -Path $registry -Name Environment -Value ($kept + $environment) -Type MultiString

Write-Host "Starting $ServiceName..."
Start-Service -Name $ServiceName
(Get-ServiceOrNull).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))

Write-Host ''
Write-Host "$ServiceName is running and will start at boot."
Write-Host "  Dashboard:  $Url"
Write-Host "  Health:     $Url/health/live"
Write-Host "  Holdings:   $Url/api/operations"
Write-Host "  Files:      $Path"
Write-Host ''
Write-Host 'Credentials are not installed by this script. See docs/operations/running-continuously.md.'
