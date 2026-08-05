[CmdletBinding()]
param(
    [switch]$SkipTests
)

# WinMonitor publish script
# Produces two framework-dependent win-x64 flavors under .\dist. Each release is assembled in
# a sibling staging directory first, then swapped into place only after every copy succeeds.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\WinMonitor\WinMonitor.csproj"
$testProject = Join-Path $root "tests\WinMonitor.Tests\WinMonitor.Tests.csproj"
$dist = Join-Path $root "dist"

# Prefer whichever dotnet actually has an SDK (the system-wide install may be runtime-only).
$dotnet = "dotnet"
$localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
$systemHasSdk = $false
try { $systemHasSdk = [bool](& dotnet --list-sdks 2>$null) } catch { }
if (-not $systemHasSdk -and (Test-Path -LiteralPath $localDotnet)) { $dotnet = $localDotnet }

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & robocopy.exe $Source $Destination /MIR /NJH /NJS /NP /NDL /NFL /R:1 /W:1 | Out-Null
    $copyExit = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($copyExit -ge 8) {
        throw "robocopy failed while mirroring '$Source' to '$Destination' (exit $copyExit)."
    }
}

function Replace-ReleaseDirectory {
    param(
        [Parameter(Mandatory)] [string]$Stage,
        [Parameter(Mandatory)] [string]$Target,
        [Parameter(Mandatory)] [string]$Backup
    )

    if (Test-Path -LiteralPath $Backup) {
        throw "Previous publish recovery directory still exists: $Backup"
    }

    $hadTarget = Test-Path -LiteralPath $Target
    if ($hadTarget) {
        Move-Item -LiteralPath $Target -Destination $Backup -ErrorAction Stop
    }

    try {
        Move-Item -LiteralPath $Stage -Destination $Target -ErrorAction Stop
    }
    catch {
        if ($hadTarget -and -not (Test-Path -LiteralPath $Target) -and (Test-Path -LiteralPath $Backup)) {
            Move-Item -LiteralPath $Backup -Destination $Target -ErrorAction Stop
        }
        throw
    }
}

function Restore-ReleaseDirectory {
    param(
        [Parameter(Mandatory)] [string]$Target,
        [Parameter(Mandatory)] [string]$Backup,
        [Parameter(Mandatory)] [string]$Failed
    )

    if (Test-Path -LiteralPath $Target) {
        Move-Item -LiteralPath $Target -Destination $Failed -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $Backup) {
        Move-Item -LiteralPath $Backup -Destination $Target -ErrorAction Stop
    }
}

function Remove-DirectoryBestEffort {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "Could not remove publish staging directory: $Path" }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$runId = [Guid]::NewGuid().ToString("N")
$publishStage = Join-Path ([IO.Path]::GetTempPath()) ("WinMonitor-publish-" + $runId)
$installedStage = Join-Path $dist (".WinMonitor-stage-" + $runId)
$portableStage = Join-Path $dist (".WinMonitor-Portable-stage-" + $runId)
$portableState = Join-Path $dist (".WinMonitor-portable-state-" + $runId)
$outDir = Join-Path $dist "WinMonitor"
$portable = Join-Path $dist "WinMonitor-Portable"
$installedBackup = Join-Path $dist (".WinMonitor-backup-" + $runId)
$portableBackup = Join-Path $dist (".WinMonitor-Portable-backup-" + $runId)
$installedFailed = Join-Path $dist (".WinMonitor-failed-" + $runId)
$portableFailed = Join-Path $dist (".WinMonitor-Portable-failed-" + $runId)
$installedReplaced = $false
$portableReplaced = $false
$publishCommitted = $false

try {
    if (-not $SkipTests) {
        if (-not (Test-Path -LiteralPath $testProject)) {
            throw "Regression harness not found: $testProject. Use -SkipTests only when intentionally bypassing it."
        }
        Write-Host "Running regression harness..." -ForegroundColor Cyan
        & $dotnet run --project $testProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Regression harness failed." }
        $global:LASTEXITCODE = 0
    }

    Write-Host "Publishing WinMonitor (Release)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $publishStage, $portableState | Out-Null
    & $dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishStage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    $global:LASTEXITCODE = 0

    if (-not (Test-Path -LiteralPath (Join-Path $publishStage "WinMonitor.exe"))) {
        throw "Publish output is incomplete: WinMonitor.exe was not produced."
    }

    # Preserve portable user state while replacing only the application payload.
    foreach ($name in @("config.json", "config.json.bak", "config.json.recovered", "config.json.newer-version", "crash.log", "logs")) {
        $source = Join-Path $portable $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $portableState -Recurse -Force -ErrorAction Stop
        }
    }

    Invoke-RobocopyMirror -Source $publishStage -Destination $installedStage
    Invoke-RobocopyMirror -Source $publishStage -Destination $portableStage
    Get-ChildItem -Force -LiteralPath $portableState | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $portableStage -Recurse -Force -ErrorAction Stop
    }
    Set-Content -LiteralPath (Join-Path $portableStage "portable.txt") -Value "This marker makes WinMonitor store config.json and logs next to the exe." -Encoding utf8

    # These renames happen within dist, so a locked/running old release fails before its target
    # is overwritten. If the second flavor cannot swap, restore the first from its backup.
    Replace-ReleaseDirectory -Stage $installedStage -Target $outDir -Backup $installedBackup
    $installedReplaced = $true
    Replace-ReleaseDirectory -Stage $portableStage -Target $portable -Backup $portableBackup
    $portableReplaced = $true
    $publishCommitted = $true

    $sizeMB = [math]::Round((Get-ChildItem -LiteralPath $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "Dist size: $sizeMB MB" -ForegroundColor DarkGray
}
catch {
    $publishError = $_
    if (-not $publishCommitted) {
        try {
            if ($portableReplaced) {
                Restore-ReleaseDirectory -Target $portable -Backup $portableBackup -Failed $portableFailed
            }
            if ($installedReplaced) {
                Restore-ReleaseDirectory -Target $outDir -Backup $installedBackup -Failed $installedFailed
            }
        }
        catch {
            Write-Warning "Release rollback also failed: $($_.Exception.Message)"
        }
    }
    throw $publishError
}
finally {
    Remove-DirectoryBestEffort -Path $publishStage
    Remove-DirectoryBestEffort -Path $installedStage
    Remove-DirectoryBestEffort -Path $portableStage
    Remove-DirectoryBestEffort -Path $portableState

    if ($publishCommitted) {
        Remove-DirectoryBestEffort -Path $installedBackup
        Remove-DirectoryBestEffort -Path $portableBackup
    }
}

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host "  $((Join-Path $dist 'WinMonitor\WinMonitor.exe'))"
Write-Host "  $((Join-Path $dist 'WinMonitor-Portable\WinMonitor.exe'))  (portable)"
Write-Host ""
Write-Host "Run as administrator for full sensor access (CPU / SSD SMART / fans)." -ForegroundColor Yellow
