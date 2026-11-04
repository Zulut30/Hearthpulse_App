<#
.SYNOPSIS
  Quick local check before a commit: builds only the projects the change touches, runs the tests of the touched areas,
  the pool API and the skill scripts - at Idle priority, so a game or other sessions on the same PC keep the CPU.

.DESCRIPTION
  The change is everything that differs from the merge base with main (commits, staged and unstaged edits, new files).
  Builds are strict like CI (HEARTHPULSE_STRICT: any warning fails) unless -Fast. Tests: a changed test file runs its
  class; a changed source file runs the classes whose name contains the file's name (ProfileUpload.cs ->
  *ProfileUpload*); shared build settings (Directory.Build.*, .editorconfig, the solution) build and test everything.
  Timing tests are left out (they measure wall-clock time, which a busy PC skews); CI runs them. CI stays the full
  check: coverage, the ratchet, Linux and macOS.

.EXAMPLE
  powershell -File tools/dev/check.ps1              # the change against main
  powershell -File tools/dev/check.ps1 -All         # whole solution, every suite
  powershell -File tools/dev/check.ps1 -Fast        # no analyzers, warnings stay warnings: an edit-compile loop, not a final check
  powershell -File tools/dev/check.ps1 -Base HEAD~1 # only the last commit
#>
[CmdletBinding()]
param(
    [string]$Base = '',
    [switch]$All,
    [switch]$Fast,
    [switch]$NoTests,
    # MSBuild nodes; a quarter of the cores leaves room for the game and other sessions.
    [int]$Jobs = [Math]::Max(1, [int]([Environment]::ProcessorCount / 4))
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $root

# Child processes inherit the Idle class; no build servers or reused nodes, which would run at their own priority.
(Get-Process -Id $PID).PriorityClass = 'Idle'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:DOTNET_NOLOGO = 'true'
$env:MSBUILDDISABLENODEREUSE = '1'
if ($Fast) { $env:HEARTHPULSE_FAST = 'true'; $env:HEARTHPULSE_STRICT = '' } else { $env:HEARTHPULSE_STRICT = 'true' }

$coreTests = 'tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj'
$presentationTests = 'tests/Hearthpulse.Presentation.Tests/Hearthpulse.Presentation.Tests.csproj'

if (-not $Base) { $Base = (git merge-base HEAD main).Trim() }
$files = @(git diff --name-only $Base) + @(git ls-files --others --exclude-standard) |
    Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique
if (-not $All -and -not $files) { 'Nothing changed against ' + $Base + '.'; exit 0 }

$build = [System.Collections.Generic.List[string]]::new()  # projects (or the solution) to build
$suites = @{}                                              # test project -> class-name fragments; '*' = all
$scripts = $false; $pool = $false
function Add-Suite([string]$project, [string]$fragment) {
    if (-not $suites.ContainsKey($project)) { $suites[$project] = [System.Collections.Generic.HashSet[string]]::new() }
    [void]$suites[$project].Add($fragment)
}
# 'LocBindingTests.Meta.cs' -> 'LocBindingTests'; 'MainViewModel.Feature.cs' -> 'MainViewModel'.
function Get-Stem([string]$path) { ([IO.Path]::GetFileName($path) -split '\.')[0] }

foreach ($f in $files) {
    $cs = $f -match '\.(cs|xaml|axaml)$'
    switch -Regex ($f) {
        '^(Directory\.Build\.(props|targets)|\.editorconfig|Hearthpulse\.slnx|global\.json|NuGet\.config)$' { $All = $true }
        '^src/Hearthpulse\.(Core|UI)/' { $build.Add($coreTests); if ($cs) { Add-Suite $coreTests (Get-Stem $f) } }
        '^src/Hearthpulse\.(Presentation|App)/' {
            $build.Add($presentationTests); $build.Add($coreTests)
            if ($cs) { Add-Suite $presentationTests (Get-Stem $f); Add-Suite $coreTests (Get-Stem $f) }
        }
        '^src/Hearthpulse\.Desktop/' { $build.Add('src/Hearthpulse.Desktop/Hearthpulse.Desktop.csproj') }
        '^tests/Hearthpulse\.Tests/' {
            $build.Add($coreTests)
            Add-Suite $coreTests $(if ($f -match '\.cs$') { Get-Stem $f } else { '*' })
        }
        '^tests/Hearthpulse\.Presentation\.Tests/' {
            $build.Add($presentationTests)
            Add-Suite $presentationTests $(if ($f -match '\.cs$') { Get-Stem $f } else { '*' })
        }
        '^tools/(Hearthpulse\.\w+)/' { $build.Add("tools/$($Matches[1])/$($Matches[1]).csproj") }
        '^server/pool/' { $pool = $true }
        '^(tools/|\.claude/|docs/)|\.(py|md)$' { $scripts = $true }
    }
}
if ($All) {
    $build = @('Hearthpulse.slnx'); $suites = @{ $coreTests = @('*'); $presentationTests = @('*') }
    $scripts = $true; $pool = $true
}

$failed = [System.Collections.Generic.List[string]]::new()
function Invoke-Step([string]$title, [scriptblock]$command) {
    Write-Host "== $title" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $command | Out-Host
    $code = $LASTEXITCODE
    Write-Host ("   {0:n0} s" -f $watch.Elapsed.TotalSeconds)
    if ($code) { $failed.Add($title) }
    return -not $code
}

$built = @{}
foreach ($project in ($build | Sort-Object -Unique)) {
    $built[$project] = Invoke-Step "build $project" {
        dotnet build $project -c Debug -m:$Jobs --disable-build-servers -nologo -v:q -clp:NoSummary -p:UseSharedCompilation=false
    }
}

if (-not $NoTests) {
    foreach ($project in $suites.Keys) {
        if ($built.ContainsKey('Hearthpulse.slnx')) { $ok = $built['Hearthpulse.slnx'] } else { $ok = $built[$project] }
        if (-not $ok) { continue }
        if ($project -eq $presentationTests -and $IsWindows -eq $false) { continue }  # net10.0-windows
        $fragments = @($suites[$project])
        $filter = if ($fragments -contains '*') { 'Category!=Timing' }
            else { '(' + (($fragments | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')&Category!=Timing' }
        [void](Invoke-Step "test $project [$filter]" {
            dotnet test $project --no-build -c Debug --filter $filter --nologo -v q
        })
    }
}

if ($pool) { [void](Invoke-Step 'pool API' { node --test server/pool/test/validation.test.mjs }) }
if ($scripts) { [void](Invoke-Step 'skill scripts' { python -B -I -m unittest discover -s tools/skill_checks -q }) }

if ($failed.Count) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host 'OK' -ForegroundColor Green
