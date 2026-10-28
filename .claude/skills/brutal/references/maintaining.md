# Maintaining the skill: recording what you find

Read this only when you have something to record: a new trap or bug, a wrong name, a recipe you ran, or a package upgrade. Building a game never needs it.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`.

## Where the files are

The skill lives in BRUTAL home under `.claude/skills/brutal/`. If you're working in a game folder, edit it there (see "BRUTAL home" in `SKILL.md` to locate it). If the skill is installed read-only, tell the user what you found instead.

## Recording something new

- Put the detail in the reference file for that library: a recipe, a new `T#`/`B#` row in its Traps table, or a marker change to [RUN].
- Give a new trap or bug the next free ID (kept at the top of `references/traps.md`), add its one-line row to the index there, and bump the "next free IDs" note.
- Add a dated line to `VERIFICATION-LOG.md` in BRUTAL home: what you ran, on what, what it found, and what was not run.
- If it's a runnable recipe, add it to `Sandbox/RecipeCheck` so `Sandbox/verify.ps1` keeps checking it.
- Keep `SKILL.md` lean: it loads every time the skill triggers. Detail goes in a reference file, history goes in `VERIFICATION-LOG.md`.
- Run `Sandbox/lint-skill.ps1`: every ID has exactly one entry and one index row, every referenced file exists, and `SKILL.md` stays under its size budget.

## After a package upgrade

1. `Sandbox/check-upgrades.ps1` (add `-Apply` to bump), then `Sandbox/verify.ps1`. Its SampleCheck stage also runs any new upstream sample; one that hangs fails until it's classified in `SampleCheck.ps1`.
2. Regenerate the export-gap list (FMOD is left out; it ships no native binaries):
   `dotnet run -c Release --project Sandbox/ExportAudit -- --detail`, then rewrite `references/export-gaps-detail.txt` from its output (keep the header, the summary table and every `missing:` line except FMOD's), and update the counts in `export-gaps.md`.
3. Update the "Verified against" line in `SKILL.md` and add a line to `VERIFICATION-LOG.md`.

## Reporting upstream

Questions ("how do I…?") go to the [BRUTAL forums](https://forums.ahwoo.com/forums/brutal/). Bugs only go to [GitHub issues](https://github.com/ahwootech/BRUTAL/issues), with package versions, OS/GPU and a minimal repro. **Search the issues first**: Jolt's export gaps are already [#13](https://github.com/ahwootech/BRUTAL/issues/13). Upstream is per-package `RocketWerkz/brutal.<name>`; `ahwootech/BRUTAL` is the public mirror.
