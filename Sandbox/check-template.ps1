# End-to-end check of Templates/brutal-sdl: dotnet new -> build -> --selftest -> --snapshot -> (--smoke) ->
# single-file publish -> run the published exe from an empty folder (selftest, snapshot, crash dialog + log). Uses a throwaway template hive, so nothing
# is installed for the user. Exit code = number of failed steps.
#   powershell -ExecutionPolicy Bypass -File Sandbox/check-template.ps1 [-NoWindow] [-Keep]
# -NoWindow skips --smoke (needs a display). -Keep leaves the generated project in the temp folder for inspection.
param([switch]$NoWindow, [switch]$Keep)

$ErrorActionPreference = 'Continue'
$template = Resolve-Path (Join-Path $PSScriptRoot '..\Templates\brutal-sdl')
$work = Join-Path ([IO.Path]::GetTempPath()) ("brutal-template-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$hive = Join-Path $work 'hive'
$proj = Join-Path $work 'DotChaser'
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
    if (-not (Step 'install template (temp hive)' { dotnet new install $template --debug:custom-hive $hive | Select-Object -Last 2 })) { exit $failed }
    if (-not (Step 'dotnet new brutal-sdl -n DotChaser' { dotnet new brutal-sdl -n DotChaser -o $proj --debug:custom-hive $hive })) { exit $failed }

    # sourceName must have been replaced everywhere.
    $leftover = Get-ChildItem $proj -Recurse -File | Select-String -Pattern 'BrutalGame' -SimpleMatch
    if ($leftover) { Write-Host "   'BrutalGame' left in: $($leftover | ForEach-Object { "$($_.Path):$($_.LineNumber)" })"; $failed++ }
    if (-not (Test-Path (Join-Path $proj 'DotChaser.csproj'))) { Write-Host '   DotChaser.csproj missing'; $failed++ }

    Push-Location $proj
    try {
        if (Step 'build (warnings count as failures)' {
                $b = dotnet build -c Release -nologo -warnaserror 2>&1
                $b | Select-String -Pattern ' error |warning ' | Select-Object -First 10
                $b | Select-Object -Last 3 })
        {
            Step '--selftest' { dotnet run -c Release --no-build -- --selftest } | Out-Null
            Step '--snapshot' { dotnet run -c Release --no-build -- --snapshot snapshots } | Out-Null
            $pngs = @(Get-ChildItem snapshots -Filter *.png -ErrorAction SilentlyContinue).Count
            if ($pngs -lt 6) { Write-Host "   expected 6 PNGs, found $pngs"; $failed++ }
            if (-not $NoWindow) { Step '--smoke 4' { dotnet run -c Release --no-build -- --smoke 4 } | Out-Null }
        }

        if (Step 'publish single-file' { dotnet publish -c Release -o publish -nologo | Select-Object -Last 2 }) {
            $files = @(Get-ChildItem publish -File | ForEach-Object Name)
            Write-Host "   publish/: $($files -join ', ')"
            foreach ($required in 'DotChaser.exe', 'NOTICE.txt', 'OFL-AtkinsonHyperlegible.txt', 'README.txt') {
                if ($files -notcontains $required) { Write-Host "   publish must contain $required"; $failed++ }
            }
            $notices = @(Get-ChildItem publish\licenses -Filter *.txt -ErrorAction SilentlyContinue | ForEach-Object Name)
            Write-Host "   publish/licenses/: $($notices -join ', ')"
            if ($notices.Count -lt 6 -or -not ($notices -match 'DOTNET-RUNTIME')) { Write-Host '   expected notices for Sdl, Sdl.Ttf, Sdl.Mixer, Stb and the .NET runtime in publish/licenses/'; $failed++ }
            $size = [math]::Round((Get-Item publish\DotChaser.exe).Length / 1MB, 1)
            Write-Host "   DotChaser.exe: $size MB"

            # Run the shipped files from an empty folder, as a player would (recipe 7.14). WinExe has no console,
            # so wait for the process and read its exit code; snapshots prove the native DLLs self-extracted.
            $empty = Join-Path $work 'empty'
            New-Item -ItemType Directory -Force $empty | Out-Null
            Copy-Item publish\* $empty
            $p = Start-Process -FilePath (Join-Path $empty 'DotChaser.exe') -ArgumentList '--selftest' -WorkingDirectory $empty -Wait -PassThru
            Write-Host "-- published exe --selftest from an empty folder: exit $($p.ExitCode)"
            if ($p.ExitCode -ne 0) { $failed++ }
            $p = Start-Process -FilePath (Join-Path $empty 'DotChaser.exe') -ArgumentList '--snapshot', 'shots' -WorkingDirectory $empty -Wait -PassThru
            $shots = @(Get-ChildItem (Join-Path $empty 'shots') -Filter *.png -ErrorAction SilentlyContinue).Count
            Write-Host "-- published exe --snapshot from an empty folder: exit $($p.ExitCode), $shots PNGs"
            if ($p.ExitCode -ne 0 -or $shots -lt 6) { $failed++ }

            # Crash reporting: a GUI exe has no console, so errors must reach the player as a dialog plus a log.
            $crashDir = Join-Path $env:LOCALAPPDATA 'DotChaser'
            Remove-Item $crashDir -Recurse -Force -ErrorAction SilentlyContinue
            $p = Start-Process -FilePath (Join-Path $empty 'DotChaser.exe') -ArgumentList '--crash-test' -WorkingDirectory $empty -PassThru
            Start-Sleep -Seconds 3
            $dialog = -not $p.HasExited -and ((Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle -eq 'DotChaser stopped')
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $logged = (Test-Path (Join-Path $crashDir 'error.log')) -and ((Get-Content (Join-Path $crashDir 'error.log') -Raw) -match 'crash test')
            Write-Host "-- published exe --crash-test: dialog shown $dialog, error.log written $logged"
            if (-not ($dialog -and $logged)) { $failed++ }
            Remove-Item $crashDir -Recurse -Force -ErrorAction SilentlyContinue

            # VC++ runtime check (trap T41): the dialog must not depend on SDL, which can't load without the runtime.
            $p = Start-Process -FilePath (Join-Path $empty 'DotChaser.exe') -ArgumentList '--vcruntime-test' -WorkingDirectory $empty -PassThru
            Start-Sleep -Seconds 3
            $vcDialog = -not $p.HasExited -and ((Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle -eq "DotChaser can't start")
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Write-Host "-- published exe --vcruntime-test: missing-runtime dialog shown $vcDialog"
            if (-not $vcDialog) { $failed++ }
        }
    }
    finally { Pop-Location }
}
finally {
    if ($Keep) { Write-Host "Kept: $proj" }
    else { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host $(if ($failed -eq 0) { 'Template check: OK' } else { "Template check: $failed step(s) failed" })
exit $failed
