param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Folder,

    [switch]$Apply
)

$projectPath = Join-Path $PSScriptRoot 'extract-top-level-types\TopLevelTypeExtractor.csproj'
$command = @(
    'run'
    '--project'
    $projectPath
    '--'
    $Folder
)

if ($Apply) {
    $command += '--apply'
}

dotnet @command
exit $LASTEXITCODE
