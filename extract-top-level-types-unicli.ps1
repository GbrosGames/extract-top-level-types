param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Folder,

    [string]$ProjectRoot = (Get-Location).Path,

    [switch]$Apply
)

$toolRoot = $PSScriptRoot
$extractorPath = Join-Path $toolRoot 'extract-top-level-types.ps1'

if (-not (Test-Path -LiteralPath $extractorPath)) {
    Write-Error "Extractor not found: $extractorPath"
    exit 1
}

if (-not (Test-Path -LiteralPath $ProjectRoot)) {
    Write-Error "Project root not found: $ProjectRoot"
    exit 1
}

$resolvedProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

if ([System.IO.Path]::IsPathRooted($Folder)) {
    if (-not (Test-Path -LiteralPath $Folder)) {
        Write-Error "Folder not found: $Folder"
        exit 1
    }

    $resolvedFolder = (Resolve-Path -LiteralPath $Folder).Path
}
else {
    $candidatePath = Join-Path $resolvedProjectRoot $Folder

    if (-not (Test-Path -LiteralPath $candidatePath)) {
        Write-Error "Folder not found: $candidatePath"
        exit 1
    }

    $resolvedFolder = (Resolve-Path -LiteralPath $candidatePath).Path
}

$relativeFolder = [System.IO.Path]::GetRelativePath($resolvedProjectRoot, $resolvedFolder).Replace('\', '/')

if ($relativeFolder.StartsWith('..', [System.StringComparison]::Ordinal)) {
    Write-Error "Folder must be inside the project root: $resolvedFolder"
    exit 1
}

$isUnityAssetFolder =
    $relativeFolder.StartsWith('Assets/', [System.StringComparison]::OrdinalIgnoreCase) -or
    $relativeFolder.StartsWith('Packages/', [System.StringComparison]::OrdinalIgnoreCase)

if (-not $isUnityAssetFolder) {
    Write-Error "Folder must be under Assets/ or Packages/: $relativeFolder"
    exit 1
}

function Get-FolderSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FolderPath
    )

    $snapshot = @{}

    if (-not (Test-Path -LiteralPath $FolderPath)) {
        return $snapshot
    }

    foreach ($file in Get-ChildItem -LiteralPath $FolderPath -Recurse -File -Filter *.cs) {
        $snapshot[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $snapshot
}

function Get-RelativeProjectPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetRelativePath($resolvedProjectRoot, $Path).Replace('\', '/')
}

function Invoke-SyncVSSolution {
    $code = @'
var editorAssembly = typeof(UnityEditor.Editor).Assembly;
var syncVSType = editorAssembly.GetType("UnityEditor.SyncVS");
var method = syncVSType?.GetMethod("SyncSolution", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
method?.Invoke(null, null);
return method != null ? "synced" : "missing";
'@

    unicli eval $code --json
    return $LASTEXITCODE
}

Push-Location $resolvedProjectRoot

try {
    if (-not $Apply) {
        & $extractorPath $relativeFolder
        exit $LASTEXITCODE
    }

    git rev-parse --show-toplevel *> $null
    $hasGitRepo = $LASTEXITCODE -eq 0

    if ($hasGitRepo) {
        $existingFolderChanges = git status --short -- $relativeFolder

        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }

        if (-not [string]::IsNullOrWhiteSpace($existingFolderChanges)) {
            Write-Error "Folder has existing changes. Commit or revert them before running the Unity wrapper: $relativeFolder"
            $existingFolderChanges
            exit 1
        }
    }

    $beforeSnapshot = Get-FolderSnapshot -FolderPath $resolvedFolder

    unicli check

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $extractorPath $relativeFolder -Apply

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $afterSnapshot = Get-FolderSnapshot -FolderPath $resolvedFolder
    $beforePaths = @($beforeSnapshot.Keys)
    $afterPaths = @($afterSnapshot.Keys)

    $newScripts = @($afterPaths | Where-Object { -not $beforeSnapshot.ContainsKey($_) } | Sort-Object -Unique)
    $deletedScripts = @($beforePaths | Where-Object { -not $afterSnapshot.ContainsKey($_) } | Sort-Object -Unique)
    $modifiedScripts = @(
        $afterPaths |
        Where-Object { $beforeSnapshot.ContainsKey($_) -and $beforeSnapshot[$_] -ne $afterSnapshot[$_] } |
        Sort-Object -Unique
    )

    if ($newScripts.Count -eq 0 -and $deletedScripts.Count -eq 0 -and $modifiedScripts.Count -eq 0) {
        Write-Host "No changes produced for $relativeFolder"
        exit 0
    }

    $importScripts = @($newScripts + $modifiedScripts | Sort-Object -Unique)

    foreach ($scriptPath in $importScripts) {
        unicli exec AssetDatabase.Import --path (Get-RelativeProjectPath -Path $scriptPath) --json

        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    if ($deletedScripts.Count -gt 0) {
        unicli exec AssetDatabase.Import --path $relativeFolder --json

        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    unicli exec Compile --json

    if ($LASTEXITCODE -ne 0 -and $deletedScripts.Count -gt 0) {
        $syncExitCode = Invoke-SyncVSSolution

        if ($syncExitCode -ne 0) {
            exit $syncExitCode
        }

        unicli exec Compile --json
    }

    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
