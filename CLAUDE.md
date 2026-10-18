# Code folder: BRUTAL framework projects

This folder holds the knowledge base and verification tools for building on the BRUTAL C# framework (Ahwoo / RocketWerkz). The verified agent reference is the `brutal` skill in `.claude/skills/brutal/`. Load it before writing, debugging or reviewing any BRUTAL code. It has a slim core (`SKILL.md`: rules, trap index, lookup protocol) and one reference file per library area, read on demand.

## Layout

- `.claude/skills/brutal/`: the skill. `SKILL.md` plus `references/*.md` (and `references/export-gaps-detail.txt`, a search-only list). This is the single source of truth; edit it here. Keep `SKILL.md` lean: it loads on every trigger.
- `VERIFICATION-LOG.md`: the dated history of what was verified. Maintainer-only, kept out of the skill so agents building games never load it.
- `BRUTAL/`: a clone of https://github.com/ahwootech/BRUTAL. It contains the source of every published package, but no project files.
  - Read it to confirm APIs.
  - Don't edit it.
- `Sandbox/`: verification tools (`Sandbox/README.md`). `Sandbox/verify.ps1` builds and runs all of them, then lints the skill:
  - `RecipeCheck` and `VulkanCompute`: run the recipes.
  - `ExportAudit`: native export gaps.
  - `AnalyzerDump`: lists the analyzer rules.
  - `FixedProbe`: shows which `[Fixed]` patterns compile.
  - `check-template.ps1`: instantiates the template and builds, tests, publishes and runs it.
  - `lint-skill.ps1`: checks trap/bug IDs, the index and file links in the skill.
- `Templates/brutal-sdl/` (2D) and `Templates/brutal-3d/` (OpenGL + Jolt): the starter game templates (`dotnet new brutal-sdl`, `dotnet new brutal-3d`). They're real projects: build and run them in place, and keep `Sandbox/check-template.ps1 -Template <name>` passing after changes.

## Working rules for this folder

- New BRUTAL projects target `net10.0` with `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` and reference `Ahwoo.BRUTAL.*` packages from nuget.org.
- Treat "it compiles" as unverified. Run new native calls, or search `references/export-gaps-detail.txt` for them. Use a headless self-test or a snapshot run before calling work done.
- Anything distributable must meet the BRUTAL licence requirements: credits notice, logo before gameplay, `NOTICE.txt` shipped (`references/shipping.md`).
- When you verify something new or find a new trap, update the skill (`.claude/skills/brutal/references/maintaining.md`). When you verify a new recipe, add it to `RecipeCheck` so `verify.ps1` keeps checking it. Then run `Sandbox/verify.ps1`, or at least `Sandbox/lint-skill.ps1`.
