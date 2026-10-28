# Builds and runs upstream's samples (BRUTAL/samples/Hello and Demo) against the package versions the skill is verified
# against, so every package they touch gets at least one run. Exit code = number of failed samples.
#   powershell -ExecutionPolicy Bypass -File Sandbox/SampleCheck.ps1 [-NoWindow] [-NoVulkan] [-NoAudio] [-Pinned] [-Only HelloJolt,DemoVulkan]
# The samples are copied to a temp folder (outside Sandbox/, so Directory.Build.props doesn't apply) and each
# `#:package` line is rewritten to the version in ExportAudit/ExportAudit.csproj. -Pinned keeps upstream's versions.
# Windowed samples run for -Seconds, get a screenshot (Windows), then a close request; they pass if the window drew
# something, closed cleanly and printed no exception. -NoWindow/-NoVulkan/-NoAudio skip those kinds (CI).
param([switch]$NoWindow, [switch]$NoVulkan, [switch]$NoAudio, [switch]$Pinned, [string[]]$Only, [int]$Seconds = 6)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$samples = Join-Path $root 'BRUTAL/samples'
if (-not (Test-Path $samples)) { Write-Host "No $samples (clone BRUTAL first)"; exit 1 }
$onWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'

# How to run each sample. Anything not listed runs as 'headless'; a new sample that hangs fails until it's classified.
#   headless: exit 0 within 120 s.        gpu: headless but needs a Vulkan driver.     audio: needs an output device.
#   window: screenshot + close (MinColours = distinct colours the window must show).   gpuwindow: both.
#   nofmod: FMOD natives aren't bundled, so DllNotFoundException for 'fmod' is the expected result without them.
#   skip: needs a person or a real terminal; the recipe named covers it.
$kinds = @{
    HelloVulkan = 'gpu'; HelloVulkanAbstractions = 'gpu'; HelloVulkanVma = 'gpu'
    HelloOpenAl = 'audio'; HelloSdlMixer = 'audio'
    HelloGlfw = 'window'; HelloImGui = 'window'; HelloOpenGl = 'window'; HelloSdl = 'window'
    DemoBox2D = 'window'; DemoGltfViewer = 'window'; DemoImGui = 'window'; DemoJolt = 'window'
    DemoRenderDoc = 'window'; DemoSdl = 'window'; DemoTexturedQuad = 'window'
    DemoVulkan = 'gpuwindow'
    HelloFmod = 'nofmod'
    HelloNativeFileDialog = 'skip: opens a dialog that waits for a person (recipe 7.41 covers it)'
    HelloNotCurses = 'skip: needs a real terminal (recipe 7.43 covers it)'
}
$minColours = @{ HelloGlfw = 1 }   # an empty window shows only its clear colour; everything else must draw

# Package versions the skill is verified against.
$versions = @{}
$audit = Get-Content (Join-Path $PSScriptRoot 'ExportAudit/ExportAudit.csproj') -Raw
foreach ($m in [regex]::Matches($audit, 'Include="Ahwoo\.BRUTAL\.([A-Za-z0-9.]+)"\s+Version="\[?([0-9.]+)\]?"')) {
    if ($m.Groups[1].Value -notlike '*.Native') { $versions[$m.Groups[1].Value.ToLowerInvariant()] = $m.Groups[2].Value }
}

$work = Join-Path ([IO.Path]::GetTempPath()) 'brutal-samplecheck'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$shots = Join-Path $work 'screenshots'
New-Item -ItemType Directory -Force $shots | Out-Null

if ($onWindows) {
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SampleWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
    [void][SampleWin]::SetProcessDPIAware()   # otherwise GetClientRect reports scaled sizes and the capture is cropped
}

# Screenshot of the window's client area; returns the number of distinct colours on a 32x32 grid (-1 = no window).
# PrintWindow with PW_CLIENTONLY | PW_RENDERFULLCONTENT reads the window's own content (GL and Vulkan included), so
# it works while other windows cover it; a background process can't bring its window to the front for a screen copy.
function Capture($proc, $path) {
    $proc.Refresh()
    $h = $proc.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { return -1 }
    $r = New-Object SampleWin+RECT
    [void][SampleWin]::GetClientRect($h, [ref]$r)
    $w = $r.R - $r.L; $hgt = $r.B - $r.T
    if ($w -le 0 -or $hgt -le 0) { return -1 }
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [SampleWin]::PrintWindow($h, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if (-not $ok) { $bmp.Dispose(); return -1 }
    $colours = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($y = 0; $y -lt 32; $y++) { for ($x = 0; $x -lt 32; $x++) {
        [void]$colours.Add($bmp.GetPixel([int](($x + 0.5) * $w / 32), [int](($y + 0.5) * $hgt / 32)).ToArgb())
    } }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $colours.Count
}

$results = @()
$failed = 0
function Report($name, $state, $note) {
    $script:results += '{0,-4} {1,-24} {2}' -f $state, $name, $note
    if ($state -eq 'FAIL') { $script:failed++ }
}

$files = @(Get-ChildItem (Join-Path $samples 'Hello'), (Join-Path $samples 'Demo') -Filter *.cs | Sort-Object Name)
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split "," }); $files = @($files | Where-Object { $Only -contains $_.BaseName }) }   # -File passes "a,b" as one string
foreach ($f in $files) {
    $name = $f.BaseName
    $kind = if ($kinds.ContainsKey($name)) { $kinds[$name] } else { 'headless' }
    if ($kind -like 'skip:*') { Report $name 'SKIP' $kind.Substring(6); continue }
    if ($NoVulkan -and $kind -like 'gpu*') { Report $name 'SKIP' '-NoVulkan'; continue }
    if ($NoWindow -and $kind -like '*window') { Report $name 'SKIP' '-NoWindow'; continue }
    if ($NoAudio -and $kind -eq 'audio') { Report $name 'SKIP' '-NoAudio'; continue }

    # Copy, rewriting #:package versions (unless -Pinned). A package the audit doesn't know is a failure: add it there.
    $unknown = @()
    $lines = foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
        if ($line -match '^#:package Ahwoo\.Brutal\.([A-Za-z0-9.]+)@(\S+)\s*$') {
            $v = $versions[$Matches[1].ToLowerInvariant()]
            if (-not $v) { $unknown += $Matches[1]; $line }
            elseif ($Pinned) { $line }
            else { "#:package Ahwoo.Brutal.$($Matches[1])@$v" }
        }
        else { $line }
    }
    if ($unknown) { Report $name 'FAIL' "package(s) not in ExportAudit.csproj: $($unknown -join ', ')"; continue }
    $dir = Join-Path $work $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $src = Join-Path $dir "$name.cs"
    [IO.File]::WriteAllLines($src, [string[]]$lines)

    Write-Host "== $name ($kind) =="
    $bin = Join-Path $dir 'bin'
    $build = dotnet build $src -o $bin -nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | Out-Host
        Report $name 'FAIL' ("build: " + (($build | Select-String ' error ' | Select-Object -First 1).Line -replace '^.*?error ', ''))
        continue
    }

    $out = Join-Path $dir 'out.txt'; $err = Join-Path $dir 'err.txt'
    $proc = Start-Process dotnet -ArgumentList "`"$(Join-Path $bin "$name.dll")`"" -WorkingDirectory $dir -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $proc.Handle   # keeps ExitCode readable after exit (Windows PowerShell)
    $note = ''
    if ($kind -like '*window') {
        Start-Sleep -Seconds $Seconds
        if ($proc.HasExited) { $state = 'FAIL'; $note = "exited after under $Seconds s (exit $($proc.ExitCode))" }
        else {
            $state = 'PASS'
            if ($onWindows) {
                $png = Join-Path $shots "$name.png"
                $count = Capture $proc $png
                $need = if ($minColours.ContainsKey($name)) { $minColours[$name] } else { 2 }
                if ($count -lt 0) { $state = 'FAIL'; $note = 'no window found' }
                elseif ($count -lt $need) { $state = 'FAIL'; $note = "window shows $count colour(s), expected $need+" }
                else { $note = "ran $Seconds s, $count colours on screen" }
            }
            else { $note = "ran $Seconds s (no screenshot off Windows)" }
            [void]$proc.CloseMainWindow()
            if (-not $proc.WaitForExit(10000)) { $proc.Kill(); $state = 'FAIL'; $note += '; ignored the close request' }
            elseif ($proc.ExitCode -ne 0) { $state = 'FAIL'; $note += "; exit $($proc.ExitCode) after close" }
            else { $note += ', closed cleanly' }
        }
    }
    else {
        if (-not $proc.WaitForExit(120000)) { $proc.Kill(); $state = 'FAIL'; $note = 'still running after 120 s (a new interactive sample? classify it in $kinds)' }
        elseif ($proc.ExitCode -eq 0) { $state = 'PASS'; $note = ((Get-Content $out | Where-Object { $_.Trim() } | Select-Object -Last 1) -replace '\s+', ' ') }
        else { $state = 'FAIL'; $note = "exit $($proc.ExitCode)" }
    }
    $proc.WaitForExit()
    $errText = (Get-Content $err -Raw) + (Get-Content $out -Raw)
    if ($kind -eq 'nofmod' -and $state -eq 'FAIL' -and $errText -match "DllNotFoundException: Unable to load (DLL|shared library) 'fmod'") {
        $state = 'PASS'; $note = "DllNotFoundException for 'fmod' as documented (FMOD natives aren't bundled)"
    }
    elseif ($errText -match 'Unhandled exception\.?\s*(\S+)') { $state = 'FAIL'; $note = "$($Matches[1]) $note" }
    Get-Content $out | Out-Host
    if ((Get-Item $err).Length -gt 0) { Get-Content $err | Out-Host }
    Report $name $state $note
}

Write-Host ''
Write-Host "== Samples ($(if ($Pinned) { "upstream's pinned versions" } else { 'versions from ExportAudit.csproj' })) =="
$results | ForEach-Object { Write-Host $_ }
if ($onWindows -and -not $NoWindow) { Write-Host "Screenshots: $shots" }
exit $failed
