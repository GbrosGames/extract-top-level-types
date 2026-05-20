---
name: extract-top-level-types
description: Deterministic workflow for splitting top-level C# types into separate files by folder, with optional Unity `unicli` import and compile verification and optional git commits. Use when a user wants one-type-per-file cleanup or says things like "Split top-level C# types into separate files", "Run extract-top-level-types on Assets/Scripts", "Clean up one-type-per-file in this Unity project", "potrzebuję zrobić porządek w projekcie i wynieść klasy do osobnych plików", "1 klasa per 1 plik", or "1 klasa = 1 plik".
---

# Extract Top Level Types

## Overview

Use this skill to run the bundled `extract-top-level-types` tool in a consistent, user-guided way. Always ask the required questions in English before making changes.

## Bundled Tool Paths

Use the bundled copies from this skill folder:

- `scripts/extract-top-level-types.ps1`
- `scripts/extract-top-level-types-unicli.ps1`
- `scripts/extract-top-level-types/TopLevelTypeExtractor.csproj`
- `scripts/extract-top-level-types/Program.cs`

Do not depend on a machine-specific external installation when this skill is available.

## Required Questions

Ask these questions one at a time in English. Do not skip them. Do not start editing until you have all required answers.

1. Ask scope first:
   - `Which folders should I process? You can list specific folders or say all eligible folders.`
2. If the answer is multiple folders or `all eligible folders`, ask:
   - `If there are multiple folders to process, do you want one uninterrupted batch or should I stop after each folder so you can decide whether to continue?`
3. Ask dry-run preference:
   - `Do you want a dry run first, or should I apply changes after planning?`
4. Ask Unity verification preference:
   - `Do you want me to use unicli for asset import and compile verification?`
5. Ask commit preference:
   - `Do you want me to create git commits? By default I will not commit anything.`
6. If the user wants commits, ask:
   - `Should I commit per folder or once for the whole run?`
7. If the user wants commits, ask for the commit message convention and propose the default:
   - `What commit message convention should I use? Default: <FolderName> - extract top-level types`

## Execution Rules

Start by checking the current git working tree when the target project is inside a git repository. Mention unexpected dirty state before continuing. Do not revert unrelated user changes.

When the user chose specific folders:

- run the bundled extractor directly for non-Unity or when `unicli` is not requested
- run the bundled Unity wrapper when the user requested `unicli`

When the user chose `all eligible folders`:

- run a dry run on the broadest requested root
- collect the changed folders from `PLAN` lines
- process only folders that actually contain planned changes
- preserve the order reported by the tool unless the user asked for a different order

If the user asked to stop after each folder, pause after each completed folder and ask whether to continue.

If the user asked for `dry run first`, do not apply changes until they approve the plan.

If the user asked for `unicli`, use `scripts/extract-top-level-types-unicli.ps1` so Unity asset import and compile are handled through the wrapper.

If the user did not ask for `unicli`, use `scripts/extract-top-level-types.ps1` and do not claim Unity verification happened.

## Commit Rules

Default is no commits.

If the user asked for commits:

- commit only after the requested folder or batch is successfully applied
- use the user-approved message convention
- if the user accepted the default, use `<FolderName> - extract top-level types`
- do not auto-commit anything that only had a dry run

## Output Discipline

Keep updates short and operational:

- what folder you are planning or applying
- whether you are in dry-run or apply mode
- whether `unicli` verification was requested
- whether the run produced changes, skips, or compile errors

When reporting results, include:

- folders processed
- whether apply happened
- whether `unicli` verification happened
- whether commits were created
