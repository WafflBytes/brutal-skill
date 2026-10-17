# End-to-end check of a starter template (default brutal-sdl; -Template brutal-3d for the 3D one): dotnet new -> build -> --selftest -> --snapshot -> (--smoke) ->
# single-file publish -> run the published exe from an empty folder (selftest, snapshot, crash dialog + log). Uses a throwaway template hive, so nothing
# is installed for the user. Exit code = number of failed steps.
#   powershell -ExecutionPolicy Bypass -File Sandbox/check-template.ps1 [-Template brutal-sdl|brutal-3d] [-NoWindow] [-NoDialog] [-Keep]
# -NoWindow skips --smoke (needs a display). -NoDialog skips the two message-box checks (need an interactive desktop;
# CI) but still checks error.log. Skipped steps print SKIP. -Keep leaves the generated project in the temp folder for inspection.
param([ValidateSet('brutal-sdl', 'brutal-3d')][string]$Template = 'brutal-sdl', [switch]$NoWindow, [switch]$NoDialog, [switch]$Keep)

# Per template: its sourceName, the project name to generate, and what its outputs must contain.
$cfg = @{
    'brutal-sdl' = @{ Source = 'BrutalGame'; Name = 'DotChaser'; Shots = 6; Smoke = 4; SnapshotNeedsDisplay = $false
        Notices = 'Sdl, Sdl.Ttf, Sdl.Mixer, Stb'; MinNotices = 6 }
    'brutal-3d'  = @{ Source = 'BrutalGame3D'; Name = 'Marble3D'; Shots = 11; Smoke = 5; SnapshotNeedsDisplay = $true # hidden GL window
        Notices = 'Jolt, OpenGl, Sdl, Sdl.Ttf, Sdl.Mixer, Stb'; MinNotices = 8 }
}[$Template]

$ErrorActionPreference = 'Continue'
$templateDir = Resolve-Path (Join-Path $PSScriptRoot "..\Templates\$Template")
$projName = $cfg.Name
$work = Join-Path ([IO.Path]::GetTempPath()) ("brutal-template-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$hive = Join-Path $work 'hive'
$proj = Join-Path $work $projName
$failed = 0

function Step($name, [scriptblock]$body) {
    Write-Host "-- $name"
    $out = & $body 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host "   $_" }
    if ($code -ne 0) { Write-Host "   FAILED (exit $code)"; $script:failed++ }
    return $code -eq 0
}

New-Item -ItemType Directory -Force $work | Out-Null
try {
    # Template sources stay ASCII-only (except the font licence): Windows PowerShell 5.1 reads UTF-8 without a BOM as
    # ANSI, so a non-ASCII character displays as mojibake, and saving the file through it corrupts it.
    $nonAscii = Get-ChildItem $templateDir -Recurse -File -Include *.cs, *.md, *.txt, *.json, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'OFL.txt' } |
        Where-Object { [IO.File]::ReadAllBytes($_.FullName) | Where-Object { $_ -gt 127 } | Select-Object -First 1 }
    if ($nonAscii) { Write-Host "-- template sources are ASCII-only: FAILED ($(($nonAscii | ForEach-Object Name) -join ', '))"; $failed++ }
    else { Write-Host '-- template sources are ASCII-only' }

    if (-not (Step 'install template (temp hive)' { dotnet new install $templateDir --debug:custom-hive $hive | Select-Object -Last 2 })) { exit $failed }
    if (-not (Step "dotnet new $Template -n $projName" { dotnet new $Template -n $projName -o $proj --debug:custom-hive $hive })) { exit $failed }

    # sourceName must have been replaced everywhere.
    $leftover = Get-ChildItem $proj -Recurse -File | Select-String -Pattern $cfg.Source -SimpleMatch
    if ($leftover) { Write-Host "   '$($cfg.Source)' left in: $($leftover | ForEach-Object { "$($_.Path):$($_.LineNumber)" })"; $failed++ }
    if (-not (Test-Path (Join-Path $proj "$projName.csproj"))) { Write-Host "   $projName.csproj missing"; $failed++ }

    Push-Location $proj
    try {
        if (Step 'build (warnings count as failures)' {
                $b = dotnet build -c Release -nologo -warnaserror 2>&1
                $b | Select-String -Pattern ' error |warning ' | Select-Object -First 10
                $b | Select-Object -Last 3 })
        {
            Step '--selftest' { dotnet run -c Release --no-build -- --selftest } | Out-Null
            if ($NoWindow -and $cfg.SnapshotNeedsDisplay) { Write-Host '-- --snapshot: SKIP (-NoWindow; renders through a hidden GL window)' }
            else {
                Step '--snapshot' { dotnet run -c Release --no-build -- --snapshot snapshots } | Out-Null
                $pngs = @(Get-ChildItem snapshots -Filter *.png -ErrorAction SilentlyContinue).Count
                if ($pngs -lt $cfg.Shots) { Write-Host "   expected $($cfg.Shots) PNGs, found $pngs"; $failed++ }
            }
            $smokeSecs = $cfg.Smoke
            if (-not $NoWindow) { Step "--smoke $smokeSecs" { dotnet run -c Release --no-build -- --smoke $smokeSecs } | Out-Null }
            else { Write-Host "-- --smoke $($smokeSecs): SKIP (-NoWindow)" }
        }

        if (Step 'publish single-file' { dotnet publish -c Release -o publish -nologo | Select-Object -Last 2 }) {
            $files = @(Get-ChildItem publish -File | ForEach-Object Name)
            Write-Host "   publish/: $($files -join ', ')"
            foreach ($required in "$projName.exe", 'NOTICE.txt', 'OFL-AtkinsonHyperlegible.txt', 'README.txt') {
                if ($files -notcontains $required) { Write-Host "   publish must contain $required"; $failed++ }
            }
            $notices = @(Get-ChildItem publish\licenses -Filter *.txt -ErrorAction SilentlyContinue | ForEach-Object Name)
            Write-Host "   publish/licenses/: $($notices -join ', ')"
            if ($notices.Count -lt $cfg.MinNotices -or -not ($notices -match 'DOTNET-RUNTIME')) { Write-Host "   expected notices for $($cfg.Notices) and the .NET runtime in publish/licenses/"; $failed++ }
            $size = [math]::Round((Get-Item "publish\$projName.exe").Length / 1MB, 1)
            Write-Host "   $($projName).exe: $size MB"

            # Run the shipped files from an empty folder, as a player would (recipe 7.14). WinExe has no console,
            # so wait for the process and read its exit code; snapshots prove the native DLLs self-extracted.
            $empty = Join-Path $work 'empty'
            New-Item -ItemType Directory -Force $empty | Out-Null
            Copy-Item publish\* $empty
            $p = Start-Process -FilePath (Join-Path $empty "$projName.exe") -ArgumentList '--selftest' -WorkingDirectory $empty -Wait -PassThru
            Write-Host "-- published exe --selftest from an empty folder: exit $($p.ExitCode)"
            if ($p.ExitCode -ne 0) { $failed++ }
            if ($NoWindow -and $cfg.SnapshotNeedsDisplay) { Write-Host '-- published exe --snapshot: SKIP (-NoWindow)' }
            else {
                $p = Start-Process -FilePath (Join-Path $empty "$projName.exe") -ArgumentList '--snapshot', 'shots' -WorkingDirectory $empty -Wait -PassThru
                $shots = @(Get-ChildItem (Join-Path $empty 'shots') -Filter *.png -ErrorAction SilentlyContinue).Count
                Write-Host "-- published exe --snapshot from an empty folder: exit $($p.ExitCode), $shots PNGs"
                if ($p.ExitCode -ne 0 -or $shots -lt $cfg.Shots) { $failed++ }
            }

            # Crash reporting: a GUI exe has no console, so errors must reach the player as a dialog plus a log.
            $crashDir = Join-Path $env:LOCALAPPDATA $projName
            Remove-Item $crashDir -Recurse -Force -ErrorAction SilentlyContinue
            $p = Start-Process -FilePath (Join-Path $empty "$projName.exe") -ArgumentList '--crash-test' -WorkingDirectory $empty -PassThru
            Start-Sleep -Seconds 3
            $dialog = -not $p.HasExited -and ((Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle -eq "$projName stopped")
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $logged = (Test-Path (Join-Path $crashDir 'error.log')) -and ((Get-Content (Join-Path $crashDir 'error.log') -Raw) -match 'crash test')
            if ($NoDialog) { $dialog = 'SKIP (-NoDialog)' }
            Write-Host "-- published exe --crash-test: dialog shown $dialog, error.log written $logged"
            if (-not (($NoDialog -or $dialog) -and $logged)) { $failed++ }
            Remove-Item $crashDir -Recurse -Force -ErrorAction SilentlyContinue

            # VC++ runtime check (trap T41): the dialog must not depend on SDL, which can't load without the runtime.
            if ($NoDialog) { Write-Host '-- published exe --vcruntime-test: SKIP (-NoDialog)' }
            else {
                $p = Start-Process -FilePath (Join-Path $empty "$projName.exe") -ArgumentList '--vcruntime-test' -WorkingDirectory $empty -PassThru
                Start-Sleep -Seconds 3
                $vcDialog = -not $p.HasExited -and ((Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle -eq "$projName can't start")
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                Write-Host "-- published exe --vcruntime-test: missing-runtime dialog shown $vcDialog"
                if (-not $vcDialog) { $failed++ }
            }
        }
    }
    finally { Pop-Location }
}
finally {
    if ($Keep) { Write-Host "Kept: $proj" }
    else { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host $(if ($failed -eq 0) { "Template check ($Template): OK" } else { "Template check ($Template): $failed step(s) failed" })
exit $failed
