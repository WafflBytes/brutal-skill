# Runs every Sandbox check (plus the skill lint) and prints a one-line verdict per tool. Exit code = number of failed checks.
#   powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1 [-NoWindow] [-NoVulkan]
# -NoWindow: GLFW recipe uses the Null platform and the template skips --smoke (CI / no display). -NoVulkan: skip 7.11 and VulkanCompute.
param([switch]$NoWindow, [switch]$NoVulkan)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$failed = 0
$results = @()

function Report($name, $ok, $note) {
    $script:results += '{0,-4} {1,-14} {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $note
    if (-not $ok) { $script:failed++ }
}

Write-Host '== Build (Release) =='
dotnet build Sandbox.slnx -c Release -nologo -v quiet | Out-Host
Report 'Build' ($LASTEXITCODE -eq 0) 'Sandbox.slnx'
if ($LASTEXITCODE -ne 0) { $results; exit 1 }

Write-Host '== RecipeCheck =='
$recipeArgs = @()
if ($NoWindow) { $recipeArgs += '--no-window' }
if ($NoVulkan) { $recipeArgs += '--no-vulkan' }
dotnet run -c Release --no-build --project RecipeCheck -- @recipeArgs | Out-Host
Report 'RecipeCheck' ($LASTEXITCODE -eq 0) "$LASTEXITCODE recipe(s) failed"

if (-not $NoVulkan) {
    Write-Host '== VulkanCompute =='
    dotnet run -c Release --no-build --project VulkanCompute | Out-Host
    Report 'VulkanCompute' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE (0 = all values correct)"
}

Write-Host '== ExportAudit =='
$audit = dotnet run -c Release --no-build --project ExportAudit
$audit | Out-Host
$total = ($audit | Select-String '^Total:').Line
Report 'ExportAudit' ($LASTEXITCODE -eq 0) "$total Compare with references/export-gaps.md."

Write-Host '== AnalyzerDump =='
$rules = dotnet run -c Release --no-build --project AnalyzerDump
$rules | Out-Host
$ruleCount = ($rules | Select-String '^BRUTALCORE\d{4}').Count
Report 'AnalyzerDump' (($LASTEXITCODE -eq 0) -and ($ruleCount -gt 0)) "$ruleCount rules (references/core-libs.md lists 12)"

Write-Host '== FixedProbe =='
dotnet run -c Release --no-build --project FixedProbe | Out-Host
$fineOk = $LASTEXITCODE -eq 0
$probe = dotnet build FixedProbe -c Release -nologo -p:ProbeErrors=true 2>&1
$ids = @($probe | Select-String -AllMatches 'error (BRUTALCORE\d{4})' | ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$expected = 'BRUTALCORE0009 BRUTALCORE0010'
Report 'FixedProbe' ($fineOk -and (($ids -join ' ') -eq $expected)) "accepted patterns ran; error build gave: $($ids -join ', ')"
dotnet build FixedProbe -c Release -nologo -v quiet | Out-Null   # leave a clean build behind

Write-Host '== Template =='
$tplArgs = @()
if ($NoWindow) { $tplArgs += '-NoWindow' }
$tpl = powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-template.ps1') @tplArgs
$tpl | Out-Host
Report 'Template' ($LASTEXITCODE -eq 0) 'dotnet new brutal-sdl: build, selftest, snapshot, publish, run from empty folder'

Write-Host '== Skill lint =='
$lint = powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'lint-skill.ps1')
$lint | Out-Host
Report 'SkillLint' ($LASTEXITCODE -eq 0) (($lint | Select-Object -Last 1) -replace '^Skill lint: ', '')

Write-Host ''
Write-Host '== Summary =='
$results | ForEach-Object { Write-Host $_ }
exit $failed
