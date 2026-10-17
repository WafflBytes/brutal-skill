# BRUTAL skill for Claude Code

A verified reference for writing C# games and tools on the
[BRUTAL framework](https://github.com/ahwootech/BRUTAL) (Ahwoo / RocketWerkz,
`Ahwoo.BRUTAL.*` NuGet packages, `Brutal.*` namespaces).

BRUTAL isn't in model training data and plausible API names are often wrong
(`EMotionType` is really `MotionType`). This skill gives Claude the rules, a
trap index, native export gaps and recipes that were compiled **and run**, plus
tools to check new claims.

Unofficial and community-made. See [NOTICE.md](NOTICE.md).

## Skill effectiveness

Same task (build a Breakout game ready to share), same model, with and without the skill. One run each, so treat it as indicative:

| Run | Time |
|---|---|
| With the skill | 9 min |
| Without | 15 min |

## What's here

| Path | What it is |
|---|---|
| `.claude/skills/brutal/` | The skill: `SKILL.md` plus one reference file per library area |
| `Sandbox/` | Verification tools (`verify.ps1` runs them all, then lints the skill) |
| `Templates/brutal-sdl/` | 2D starter game template (`dotnet new brutal-sdl`) |
| `Templates/brutal-3d/` | 3D starter game template: OpenGL + Jolt (`dotnet new brutal-3d`) |
| `CLAUDE.md`, `AGENTS.md` | Project instructions for Claude Code and other agents |

Verified against `Ahwoo.BRUTAL.*` 2026.10.x on .NET 10, Windows x64, with the
BRUTAL source at commit `a25cdf8`. Linux and macOS are untested. A newer BRUTAL
clone still works, since the skill tells Claude to confirm every API in the
source, but its recipes and tables were only run against that commit, so treat
them as unverified on newer source (`Sandbox/lint-skill.ps1` warns about this).

## Install

Requires [Claude Code](https://claude.com/claude-code), the .NET 10 SDK and
[ripgrep](https://github.com/BurntSushi/ripgrep) (`rg`).

### Option A: work inside this repo (simplest)

```bash
git clone https://github.com/WafflBytes/brutal-skill
cd brutal-skill
git clone https://github.com/ahwootech/BRUTAL BRUTAL
claude
```

The skill loads from `.claude/skills/`.

### Option B: use it from any project

Clone as above, then link the skill into your user skills folder and tell it
where the repo is.

PowerShell:
```powershell
New-Item -ItemType Junction -Path "$HOME\.claude\skills\brutal" -Target "<clone>\.claude\skills\brutal"
setx BRUTAL_HOME "<clone>"
```

macOS / Linux:
```bash
ln -s <clone>/.claude/skills/brutal ~/.claude/skills/brutal
export BRUTAL_HOME=<clone>
```

## Starter template

```powershell
dotnet new install <clone>/Templates/brutal-sdl     # once per machine
dotnet new install <clone>/Templates/brutal-3d
dotnet new brutal-sdl -n MyGame                      # 2D, or: dotnet new brutal-3d -n MyGame
```

A folder-installed template is tied to its path. If you move or re-clone the
repo, run `dotnet new uninstall <old path>` and install it again from the new
location. The same goes for the `~/.claude/skills/brutal` link in Option B.

## Verify

```powershell
powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1
```

## Updating

The skill checks once per session whether a newer version has been published.
If there is one, Claude tells you and offers to update; it never updates on its
own. To update by hand, run `git pull --ff-only` in your clone.

The check needs a git clone and network access, and is skipped silently
otherwise. Anyone who cloned before the check existed needs one manual
`git pull` to get it.

`BRUTAL/` is a separate clone, so `git pull` doesn't change it. Update it
with `git -C BRUTAL pull` when you want newer framework source.

## Licence

MIT for the content of this repo (see [LICENSE](LICENSE)). BRUTAL itself is
under the BRUTAL License v1.0; see [NOTICE.md](NOTICE.md).
