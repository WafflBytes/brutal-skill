# Checks the brutal skill (.claude/skills/brutal) for drift. Exit code = number of problems.
#   powershell -ExecutionPolicy Bypass -File Sandbox/lint-skill.ps1
# - every trap/bug ID (T#, B#) has exactly one full entry (in SKILL.md or one reference file)
# - every ID whose entry is in a reference file has exactly one row in references/traps.md, naming that file
# - every references/*.md named in SKILL.md exists, and every reference file is named in SKILL.md
# - no leftover section signs from the old single-file guide, no control characters; SKILL.md stays small
# - the "next free IDs" note in SKILL.md matches the highest IDs in use
# - BRUTAL/ (when it is a git clone) at a different commit than SKILL.md's "Verified against" line is a warning, not a failure

$ErrorActionPreference = 'Stop'
$skill = Join-Path $PSScriptRoot '..\.claude\skills\brutal'
$skillMd = Join-Path $skill 'SKILL.md'
$refDir = Join-Path $skill 'references'
$problems = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$refFiles = Get-ChildItem $refDir -Filter *.md
$fileKeys = $refFiles | ForEach-Object { $_.BaseName }
$indexFile = 'traps'   # references/traps.md holds the one-line index
$skillLines = Get-Content $skillMd -Encoding UTF8

$entries = @{}   # id -> list of places holding a full entry
$index = @{}     # id -> list of files named by index rows

function Add-To($table, $key, $value) {
    if (-not $table.ContainsKey($key)) { $table[$key] = New-Object System.Collections.Generic.List[string] }
    $table[$key].Add($value)
}

foreach ($line in $skillLines) {
    if ($line -match '^\| ([TB]\d+) \|') { Add-To $entries $Matches[1] 'SKILL' }
}
foreach ($f in $refFiles) {
    foreach ($line in Get-Content $f.FullName -Encoding UTF8) {
        if ($line -notmatch '^\| ([TB]\d+) \|') { continue }
        $id = $Matches[1]
        if ($f.BaseName -eq $indexFile) {
            $cells = $line.Trim('|').Split('|') | ForEach-Object { $_.Trim() }
            if ($cells.Count -eq 3 -and $fileKeys -contains $cells[2]) { Add-To $index $id $cells[2] }
            else { $problems.Add("traps.md row for $id should be: | ID | summary | <reference file name> |") }
        }
        else { Add-To $entries $id $f.BaseName }
    }
}

foreach ($id in $entries.Keys) {
    $places = $entries[$id]
    if ($places.Count -ne 1) { $problems.Add("$id has $($places.Count) full entries: $($places -join ', ')") ; continue }
    $entryFile = $places[0]
    if ($entryFile -eq 'SKILL') {
        if ($index.ContainsKey($id)) { $problems.Add("$id is in SKILL.md in full; drop its row from traps.md") }
    }
    elseif (-not $index.ContainsKey($id)) { $problems.Add("$id (in $entryFile.md) has no row in references/traps.md") }
    elseif ($index[$id].Count -ne 1 -or $index[$id][0] -ne $entryFile) { $problems.Add("$id index row names '$($index[$id] -join ', ')' but the entry is in $entryFile.md") }
}
foreach ($id in $index.Keys) {
    if (-not $entries.ContainsKey($id)) { $problems.Add("$id has an index row but no full entry") }
}

# ID sequences and the next-free note.
foreach ($prefix in 'T', 'B') {
    $nums = @($entries.Keys | Where-Object { $_ -match "^$prefix(\d+)$" } | ForEach-Object { [int]($_.Substring(1)) } | Sort-Object)
    if ($nums.Count -eq 0) { continue }
    $max = $nums[-1]
    $missing = @(1..$max | Where-Object { $nums -notcontains $_ })
    # T-IDs skip none today; report gaps so a deleted entry doesn't go unnoticed.
    if ($missing.Count -gt 0) { $problems.Add("$prefix IDs missing from the sequence: $($missing -join ', ')") }
    $next = "$prefix$($max + 1)"
    if (-not ($skillLines -match "next free IDs:.*\b$next\b")) { $problems.Add("SKILL.md 'next free IDs' should say $next") }
}

# File references.
$skillText = $skillLines -join "`n"
$named = [regex]::Matches($skillText, 'references/([a-z0-9-]+)\.md') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($n in $named) { if ($fileKeys -notcontains $n) { $problems.Add("SKILL.md names references/$n.md, which doesn't exist") } }
foreach ($k in $fileKeys) { if ($named -notcontains $k) { $problems.Add("references/$k.md isn't named in SKILL.md, so it will never be read") } }

# Source commit: a different BRUTAL/ commit is only a warning (users may run newer source); the maintainer should re-verify before a release.
$brutal = Join-Path $PSScriptRoot '..\BRUTAL'
$verified = [regex]::Match($skillText, 'source at commit `([0-9a-f]{7,40})`').Groups[1].Value
if (-not $verified) { $problems.Add("SKILL.md 'Verified against' line doesn't name a BRUTAL/ commit") }
elseif (Test-Path (Join-Path $brutal '.git')) {
    $head = (git -C $brutal rev-parse --short=7 HEAD).Trim()
    if (-not $head.StartsWith($verified) -and -not $verified.StartsWith($head)) {
        $warnings.Add("BRUTAL/ is at $head but the skill was verified against $verified. Recipes and tables are unverified on newer source. Before a release: re-run verify.ps1, review 'git -C BRUTAL log --stat $verified..HEAD', then update SKILL.md")
    }
}

# Leftovers and size.
$all = @($skillMd) + ($refFiles | ForEach-Object { $_.FullName })
foreach ($p in $all) {
    $hits = Select-String -Path $p -Pattern ([char]0x00A7) -Encoding UTF8
    foreach ($h in $hits) { $problems.Add("$(Split-Path $p -Leaf):$($h.LineNumber) has a section sign from the old guide") }
    # Control characters (e.g. a backspace left where a backslash-b was meant) corrupt commands silently.
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($p)) {
        $lineNo++
        if ($line -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') { $problems.Add("$(Split-Path $p -Leaf):$lineNo has a control character") }
    }
}
$size = ([IO.File]::ReadAllText($skillMd) -replace "`r", '').Length   # CRLF checkouts must not count extra
if ($size -gt 20000) { $problems.Add("SKILL.md is $size bytes; keep it under 20000 and move detail into a reference file") }

$refCount = @($refFiles).Count
$warnings | ForEach-Object { Write-Host "Skill lint WARNING: $_" }
if ($problems.Count -eq 0) {
    Write-Host "Skill lint: OK ($($entries.Count) trap/bug IDs, $refCount reference files, SKILL.md $size bytes)"
}
else {
    $problems | ForEach-Object { Write-Host "Skill lint: $_" }
}
exit $problems.Count
