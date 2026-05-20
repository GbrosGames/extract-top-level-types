# extract-top-level-types

Deterministic Roslyn-based extractor for splitting C# files so each top-level type ends up in its own file.

This repository is also a Codex skill. The repository root is the skill root, so the recommended installation method is to clone it directly into your `skills` directory and update it later with `git pull`.

## Install as a Codex Skill

Recommended layout:

- `~/.codex/skills/extract-top-level-types` on macOS/Linux
- `%USERPROFILE%\.codex\skills\extract-top-level-types` on Windows

Example:

```powershell
git clone https://github.com/GbrosGames/extract-top-level-types.git $env:USERPROFILE\.codex\skills\extract-top-level-types
```

After cloning there, Codex can discover the skill directly because these files are at repository root:

- `SKILL.md`
- `agents/openai.yaml`
- `scripts/`

To update later:

```powershell
git -C $env:USERPROFILE\.codex\skills\extract-top-level-types pull
```

## Layout

- `SKILL.md`
  - skill instructions and workflow
- `agents/openai.yaml`
  - UI metadata for the skill
- `scripts/extract-top-level-types.ps1`
  - thin wrapper around `dotnet run`
- `scripts/extract-top-level-types-unicli.ps1`
  - optional Unity wrapper for `unicli` import and compile flow
- `scripts/extract-top-level-types/Program.cs`
  - extraction logic
- `scripts/extract-top-level-types/TopLevelTypeExtractor.csproj`
  - tool project

## What It Does

For a given folder:

- scans all `.cs` files recursively
- leaves files with `0` or `1` top-level declaration unchanged
- splits files with multiple top-level declarations into separate files
- keeps nested types inside their owner
- groups non-generic and generic variants with the same base name into one output

Example:

- `SomeClass`
- `SomeClass<T>`

Both stay together in:

- `SomeClass.cs`

## Naming Rules

- output file name is the top-level type name without generic arity
- `DiagnosticJobName<T>` becomes `DiagnosticJobName.cs`
- nested types do not get extracted on their own

## Safety Rules

The tool skips unsupported files instead of guessing:

- `partial` top-level declarations
- parse-error files
- assembly/module attribute files
- batch output collisions

It preserves:

- namespace or global namespace shape
- attributes
- generic constraints
- preprocessor directives inside members and file-level wrappers such as `#if UNITY_EDITOR`

## Prerequisites

- .NET 8 SDK
- PowerShell 7+

Optional for Unity workflow:

- `unicli`
- Unity Editor open with the target project loaded

## Usage

From the repository root:

Dry run:

```powershell
pwsh .\scripts\extract-top-level-types.ps1 C:\src\MyProject\Assets\Scripts\Diagnostics
```

Apply:

```powershell
pwsh .\scripts\extract-top-level-types.ps1 C:\src\MyProject\Assets\Scripts\Diagnostics -Apply
```

Direct `dotnet` invocation:

```powershell
dotnet run --project .\scripts\extract-top-level-types\TopLevelTypeExtractor.csproj -- C:\src\MyProject\Assets\Scripts\Diagnostics
dotnet run --project .\scripts\extract-top-level-types\TopLevelTypeExtractor.csproj -- C:\src\MyProject\Assets\Scripts\Diagnostics --apply
```

You can also run it from inside the target repository with relative paths:

```powershell
pwsh C:\tools\extract-top-level-types\scripts\extract-top-level-types.ps1 .\Assets\Scripts\Diagnostics
pwsh C:\tools\extract-top-level-types\scripts\extract-top-level-types.ps1 .\Assets\Scripts\Diagnostics -Apply
```

## Unity Wrapper

`scripts/extract-top-level-types-unicli.ps1` is optional. It keeps the extractor generic and adds Unity-specific orchestration:

1. runs the extractor on one folder
2. imports new and modified `.cs` assets through `unicli`
3. reimports the folder when files were deleted
4. runs `unicli exec Compile --json`
5. retries compile once with `UnityEditor.SyncVS.SyncSolution()` when deleted files leave stale project entries behind

Usage from the Unity project root:

```powershell
pwsh C:\tools\extract-top-level-types\scripts\extract-top-level-types-unicli.ps1 Assets/Scripts/Diagnostics
pwsh C:\tools\extract-top-level-types\scripts\extract-top-level-types-unicli.ps1 Assets/Scripts/Diagnostics -Apply
```

Usage from anywhere with an explicit project root:

```powershell
pwsh .\scripts\extract-top-level-types-unicli.ps1 Assets/Scripts/Diagnostics -ProjectRoot C:\src\MyUnityProject
pwsh .\scripts\extract-top-level-types-unicli.ps1 Assets/Scripts/Diagnostics -ProjectRoot C:\src\MyUnityProject -Apply
```

The wrapper has no hardcoded machine-specific project paths.

If the target project is a git repository, the wrapper refuses to run `-Apply` on a folder that already has uncommitted changes.

## Output

Dry-run output:

- `PLAN ...`
  - file will be rewritten or split
- `SKIP ...`
  - file was intentionally not touched
- `SUMMARY ...`
  - final counts

Apply output:

- `WRITE ...`
  - file written
- `DELETE ...`
  - source file removed because all declarations moved elsewhere

## Why Git Commit Is Not Built In

Commiting is intentionally left outside the tool:

- import and compile steps are project-specific
- Unity projects need `.meta` generation before a clean commit
- different repositories may want different commit granularity
- some repositories should not auto-commit from a refactor helper

## Known Limits

- no semantic analysis across projects or solutions
- no automatic handling of `partial` top-level declarations
- no automatic git workflow
- the Unity wrapper depends on `unicli` and a running Unity Editor

## Recommended Use

- run folder by folder
- prefer dry-run first
- compile after each applied folder in Unity projects
- commit folder by folder in the target repository