[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Alias('ProjectPath')]
    [string]$TargetPath,
    [string]$OutputPath = 'inspect.xml',
    [ValidateSet('INFO', 'HINT', 'SUGGESTION', 'WARNING', 'ERROR')]
    [string]$Severity = 'SUGGESTION',
    [string]$SettingsPath,
    [string]$Include,
    [string]$Exclude,
    [string[]]$Project,
    [string]$InspectCodeExe,
    [switch]$NoBuild,
    [switch]$NoInstall,
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

function ConvertTo-RelativePath {
    param([Parameter(Mandatory = $true)][string]$FullPath)

    $rootPath = [System.IO.Path]::GetFullPath($root)
    if (-not $rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rootPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $rootUri = [System.Uri]::new($rootPath)
    $pathUri = [System.Uri]::new([System.IO.Path]::GetFullPath($FullPath))
    $relativePath = $rootUri.MakeRelativeUri($pathUri).ToString()
    $relativePath = [System.Uri]::UnescapeDataString($relativePath)
    $relativePath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)

    if ([string]::IsNullOrEmpty($relativePath)) {
        return '.'
    }

    return $relativePath
}

function Resolve-RepoPathInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$MustExist
    )

    $fullPath = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $root $Path))
    }

    if ($MustExist -and -not (Test-Path -LiteralPath $fullPath)) {
        throw "Path does not exist: $Path"
    }

    [pscustomobject]@{
        Full     = $fullPath
        Relative = ConvertTo-RelativePath -FullPath $fullPath
    }
}

function Get-DefaultTargetPath {
    $solutions = @(Get-ChildItem -LiteralPath $root -Filter '*.sln' -File)
    if ($solutions.Count -eq 1) {
        return (Resolve-RepoPathInfo -Path $solutions[0].FullName -MustExist).Relative
    }

    if ($solutions.Count -gt 1) {
        $solutionList = ($solutions | ForEach-Object { $_.Name }) -join ', '
        throw "Multiple solution files found ($solutionList). Pass -TargetPath explicitly."
    }

    $projects = @(
        Get-ChildItem -LiteralPath $root -Filter '*.csproj' -File -Recurse |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    )

    if ($projects.Count -eq 1) {
        return (Resolve-RepoPathInfo -Path $projects[0].FullName -MustExist).Relative
    }

    if ($projects.Count -gt 1) {
        $projectList = ($projects | ForEach-Object { (Resolve-RepoPathInfo -Path $_.FullName -MustExist).Relative }) -join ', '
        throw "No solution file found and multiple project files found ($projectList). Pass -TargetPath explicitly."
    }

    throw 'No solution or project file found. Pass -TargetPath explicitly.'
}

function Resolve-InspectRunner {
    if ($InspectCodeExe) {
        $resolved = Resolve-Path -LiteralPath $InspectCodeExe
        return [pscustomobject]@{
            Exe  = $resolved.Path
            Args = @()
            Name = 'inspectcode.exe'
        }
    }

    $inspectCode = Get-Command inspectcode.exe -ErrorAction SilentlyContinue
    if ($inspectCode) {
        return [pscustomobject]@{
            Exe  = $inspectCode.Source
            Args = @()
            Name = 'inspectcode.exe'
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
            throw 'Could not find inspectcode.exe or jb. Install JetBrains ReSharper Command Line Tools, or rerun without -NoInstall to install JetBrains.ReSharper.GlobalTools.'
        }

        dotnet tool install -g JetBrains.ReSharper.GlobalTools
        $jbPath = Join-Path $env:USERPROFILE '.dotnet\tools\jb.exe'
        $jb = [pscustomobject]@{ Source = $jbPath }
    }

    return [pscustomobject]@{
        Exe  = $jb.Source
        Args = @('inspectcode')
        Name = 'jb inspectcode'
    }
}

$targetInfo = Resolve-RepoPathInfo -Path $(if ([string]::IsNullOrWhiteSpace($TargetPath)) { Get-DefaultTargetPath } else { $TargetPath }) -MustExist
$outputInfo = Resolve-RepoPathInfo -Path $OutputPath
$settingsInfo = if ($SettingsPath) { Resolve-RepoPathInfo -Path $SettingsPath -MustExist } else { $null }
$runner = Resolve-InspectRunner

$arguments = @(
    $runner.Args
    '-f=Xml'
    "--output=$($outputInfo.Relative)"
    "--severity=$Severity"
    '--no-updates'
)

if ($settingsInfo) {
    $arguments += "--settings=$($settingsInfo.Relative)"
}

if ($Include) {
    $arguments += "--include=$Include"
}

if ($Exclude) {
    $arguments += "--exclude=$Exclude"
}

foreach ($projectFilter in $Project) {
    $arguments += "--project=$projectFilter"
}

if ($NoBuild) {
    $arguments += '--no-build'
}

$arguments += $targetInfo.Relative

Write-Host "Running $($runner.Name) on $($targetInfo.Relative)"
Write-Host "Output: $($outputInfo.Relative)"
Write-Host "Severity: $Severity"
if ($settingsInfo) {
    Write-Host "Settings: $($settingsInfo.Relative)"
}
if ($Include) {
    Write-Host "Include: $Include"
}
if ($Exclude) {
    Write-Host "Exclude: $Exclude"
}
foreach ($projectFilter in $Project) {
    Write-Host "Project: $projectFilter"
}

if (-not $PSCmdlet.ShouldProcess($targetInfo.Relative, "Run $($runner.Name)")) {
    return
}

if (Test-Path -LiteralPath $outputInfo.Full -PathType Leaf) {
    Remove-Item -LiteralPath $outputInfo.Full -Force
}

$pushedLocation = $false
try {
    Push-Location -LiteralPath $root
    $pushedLocation = $true
    & $runner.Exe @arguments
    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
}
finally {
    if ($pushedLocation) {
        Pop-Location
    }
}

if ($exitCode -ne 0) {
    throw "InspectCode failed with exit code $exitCode."
}

[xml]$report = Get-Content -LiteralPath $outputInfo.Full
$types = @{}
foreach ($type in $report.Report.IssueTypes.IssueType) {
    $types[$type.Id] = $type
}

$report.SelectNodes('//Issues/Project/Issue') |
    ForEach-Object {
        [pscustomobject]@{
            Project  = $_.ParentNode.Name
            Category = $types[$_.TypeId].CategoryId
            Severity = if ($_.Severity) { $_.Severity } else { $types[$_.TypeId].Severity }
            TypeId   = $_.TypeId
            File     = $_.File
            Line     = $_.Line
            Message  = $_.Message
        }
    } |
    Sort-Object Category, Severity, TypeId |
    Group-Object Category |
    ForEach-Object {
        "`n=== $($_.Name) ($($_.Count)) ==="
        $_.Group |
            Format-Table Severity, Project, TypeId, File, Line, Message |
            Out-String -Width 32766
    }

if (-not $NoPause) {
    Read-Host 'Press Enter to exit'
}
