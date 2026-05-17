[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ProjectPath = 'src/VolumeTrayAppWPF.csproj',
    [string]$SettingsPath = 'CleanupCode.DotSettings',
    [string]$Profile = 'CodeStyleIssuesAndLanguageUsage',
    [string]$EditorConfigOverlayPath,
    [string]$CleanupCodeExe,
    [switch]$NoBuild,
    [switch]$NoInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $root $Path)).Path
}

$target = Get-Item -LiteralPath (Resolve-RepoPath $ProjectPath)
$settings = Resolve-RepoPath $SettingsPath

function Resolve-CleanupRunner {
    if ($CleanupCodeExe) {
        $resolved = Resolve-Path -LiteralPath $CleanupCodeExe
        return [pscustomobject]@{
            Exe  = $resolved.Path
            Args = @()
            Name = 'cleanupcode.exe'
        }
    }

    $cleanupCode = Get-Command cleanupcode.exe -ErrorAction SilentlyContinue
    if ($cleanupCode) {
        return [pscustomobject]@{
            Exe  = $cleanupCode.Source
            Args = @()
            Name = 'cleanupcode.exe'
        }
    }

    $jb = Get-Command jb -ErrorAction SilentlyContinue
    if (-not $jb) {
        $jbPath = Join-Path $env:USERPROFILE '.dotnet\tools\jb.exe'
        if (Test-Path -LiteralPath $jbPath -PathType Leaf) {
            $jb = [pscustomobject]@{ Source = $jbPath }
        }
    }

    if (-not $jb) {
        if ($NoInstall) {
            throw 'Could not find cleanupcode.exe or jb. Install JetBrains ReSharper Command Line Tools, or rerun without -NoInstall to install JetBrains.ReSharper.GlobalTools.'
        }

        dotnet tool install -g JetBrains.ReSharper.GlobalTools
        $jbPath = Join-Path $env:USERPROFILE '.dotnet\tools\jb.exe'
        $jb = [pscustomobject]@{ Source = $jbPath }
    }

    return [pscustomobject]@{
        Exe  = $jb.Source
        Args = @('cleanupcode')
        Name = 'jb cleanupcode'
    }
}

$runner = Resolve-CleanupRunner
$targetPath = $target.FullName
$targetDirectory = if ($target.PSIsContainer) { $target.FullName } else { $target.DirectoryName }

$arguments = @(
    $runner.Args
    "--settings=$settings"
    "--profile=$Profile"
    '--no-buildin-settings'
    '--verbosity=INFO'
    '--no-updates'
)

if ($NoBuild) {
    $arguments += '--no-build'
}

$arguments += $targetPath

Write-Host "Running $($runner.Name) on $targetPath"
Write-Host "Settings: $settings"
Write-Host "Profile: $Profile"

if ($EditorConfigOverlayPath) {
    Write-Host "EditorConfig overlay: $(Resolve-RepoPath $EditorConfigOverlayPath)"
}

if (-not $PSCmdlet.ShouldProcess($targetPath, "Run $($runner.Name) with '$Profile'")) {
    return
}

$overlayDestination = Join-Path $targetDirectory '.editorconfig'
$overlayBackup = $null

try {
    if ($EditorConfigOverlayPath) {
        $overlaySource = Resolve-RepoPath $EditorConfigOverlayPath

        if (Test-Path -LiteralPath $overlayDestination -PathType Leaf) {
            $overlayBackup = Join-Path $targetDirectory ".editorconfig.cleanup-backup.$([guid]::NewGuid().ToString('N'))"
            Move-Item -LiteralPath $overlayDestination -Destination $overlayBackup
        }

        Copy-Item -LiteralPath $overlaySource -Destination $overlayDestination
    }

    & $runner.Exe @arguments
    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
}
finally {
    if ($EditorConfigOverlayPath) {
        if (Test-Path -LiteralPath $overlayDestination -PathType Leaf) {
            Remove-Item -LiteralPath $overlayDestination -Force
        }

        if ($overlayBackup -and (Test-Path -LiteralPath $overlayBackup -PathType Leaf)) {
            Move-Item -LiteralPath $overlayBackup -Destination $overlayDestination
        }
    }
}

if ($exitCode -ne 0) {
    throw "CleanupCode failed with exit code $exitCode."
}
