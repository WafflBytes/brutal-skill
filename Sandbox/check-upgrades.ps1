# Checks nuget.org and the BRUTAL GitHub mirror for anything newer than what the skill was verified against.
#   powershell -ExecutionPolicy Bypass -File Sandbox/check-upgrades.ps1          # report only
#   powershell -ExecutionPolicy Bypass -File Sandbox/check-upgrades.ps1 -Apply   # also bump every Ahwoo.BRUTAL.*
#                                                                                  # version in Sandbox/ and Templates/
# After -Apply: run Sandbox/verify.ps1, compare ExportAudit with references/export-gaps.md, re-check traps that
# RecipeCheck guards (T36, T40, T42), then update SKILL.md's "Verified against" line and verification.md.
# Exit code: 0 = everything current, 1 = updates available (or applied), 2 = nuget.org unreachable.
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$projects = @(Get-ChildItem (Join-Path $root 'Sandbox'), (Join-Path $root 'Templates') -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

# Every Ahwoo.BRUTAL.* version referenced anywhere (PackageReference or PackageDownload, plain or [exact]).
$pattern = '(Include="(Ahwoo\.BRUTAL\.[A-Za-z0-9.]+)"\s+Version="\[?)([0-9][0-9A-Za-z.\-]*)(\]?")'
$current = @{}
foreach ($p in $projects) {
    foreach ($m in [regex]::Matches((Get-Content $p.FullName -Raw), $pattern)) {
        $id = $m.Groups[2].Value
        $v = $m.Groups[3].Value
        if (-not $current.ContainsKey($id)) { $current[$id] = New-Object System.Collections.Generic.HashSet[string] }
        [void]$current[$id].Add($v)
    }
}

# Latest stable version of each package from the nuget.org flat container (no API key needed).
$latest = @{}
try {
    foreach ($id in $current.Keys) {
        $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json" -TimeoutSec 20).versions
        $stable = @($versions | Where-Object { $_ -notmatch '-' })
        $latest[$id] = ($stable | Sort-Object { [version]$_ } | Select-Object -Last 1)
    }
}
catch {
    Write-Host "Can't reach nuget.org: $($_.Exception.Message)"
    exit 2
}

$outdated = @()
"{0,-36} {1,-22} {2}" -f 'Package', 'In repo', 'Latest on nuget.org' | Write-Host
foreach ($id in ($current.Keys | Sort-Object)) {
    $inRepo = ($current[$id] | Sort-Object) -join ', '
    $mark = ''
    if ($current[$id].Count -gt 1) { $mark = '  (several versions in the repo)' }
    if (@($current[$id]) -notcontains $latest[$id] -or $current[$id].Count -gt 1) {
        $outdated += $id
        if (-not $mark) { $mark = '  <- newer' }
    }
    "{0,-36} {1,-22} {2}{3}" -f $id, $inRepo, $latest[$id], $mark | Write-Host
}

# BRUTAL source: how far the local clone and the skill's verified commit are behind the mirror.
$brutal = Join-Path $root 'BRUTAL'
$skillMd = Get-Content (Join-Path $root '.claude\skills\brutal\SKILL.md') -Raw
$verified = [regex]::Match($skillMd, 'source at commit `([0-9a-f]{7,40})`').Groups[1].Value
if (Test-Path (Join-Path $brutal '.git')) {
    git -C $brutal fetch --quiet origin 2>$null
    $head = (git -C $brutal rev-parse --short=7 HEAD).Trim()
    $remote = (git -C $brutal rev-parse --short=7 origin/HEAD 2>$null)
    if (-not $remote) { $remote = (git -C $brutal rev-parse --short=7 origin/main).Trim() }
    $behind = (git -C $brutal rev-list --count "$verified..$remote" 2>$null)
    Write-Host ''
    Write-Host "BRUTAL source: verified $verified, local clone $head, mirror $remote ($behind commit(s) after the verified one)"
    if ($behind -and [int]$behind -gt 0) {
        Write-Host "  Review: git -C BRUTAL log --stat $verified..$remote"
        $outdated += 'BRUTAL source'
    }
}
else { Write-Host "`nBRUTAL/ isn't a git clone; source not checked." }

if ($outdated.Count -eq 0) {
    Write-Host "`nEverything is current."
    exit 0
}

if (-not $Apply) {
    Write-Host "`n$($outdated.Count) item(s) can be updated. Re-run with -Apply to bump the package versions, then run Sandbox/verify.ps1."
    exit 1
}

foreach ($p in $projects) {
    $text = [IO.File]::ReadAllText($p.FullName)
    $new = [regex]::Replace($text, $pattern, {
            param($m)
            $id = $m.Groups[2].Value
            if ($latest.ContainsKey($id)) { "$($m.Groups[1].Value)$($latest[$id])$($m.Groups[4].Value)" } else { $m.Value }
        })
    if ($new -ne $text) {
        [IO.File]::WriteAllText($p.FullName, $new, (New-Object System.Text.UTF8Encoding $false)) # keep UTF-8 without BOM
        Write-Host "updated $($p.FullName.Substring($root.Path.Length + 1))"
    }
}
Write-Host "`nVersions bumped (BRUTAL/ source left alone: pull it yourself). Next: Sandbox/verify.ps1, then update SKILL.md and references/verification.md."
exit 1
