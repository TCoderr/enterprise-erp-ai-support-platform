<#
.SYNOPSIS
  Browser gate: starts its OWN stack (project kantar-e2e, port 8963, directory .local-e2e), seeds it,
  runs the Playwright suite with one worker against it and removes the stack again.
  It never touches the development stack (project kantar, .local) or any other product's containers.
#>
param([switch] $KeepStack)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')

$root = Get-RepoRoot
$project = 'kantar-e2e'
$localDir = '.local-e2e'
$port = 8963
$stack = Join-Path $root 'scripts\local-stack.ps1'
$web = Join-Path $root 'src\web'
$docker = Find-Docker

function Remove-OwnStack {
    if (Test-OwnedLocalDir -Root $root -LocalDir (Join-Path $root $localDir) -Project $project) {
        try { & $stack reset -Project $project -LocalDir $localDir -Port $port -ConfirmReset $project | ForEach-Object { Write-Output $_ } }
        catch { Write-Output "warning: cleanup of $project failed: $($_.Exception.Message)" }
    }
}

$neighboursBefore = @(Invoke-Native { & $docker ps --format '{{.Names}}' } | Where-Object { $_ -cnotmatch '^kantar-e2e-' } | Sort-Object)
$exitCode = 1
try {
    Remove-OwnStack     # a previous interrupted run of this same gate
    if (-not (Test-PortFree $port)) { Write-Output "GATE-NOTE: port $port is in use; the e2e stack was not started"; exit 1 }

    & $stack init -Project $project -LocalDir $localDir -Port $port | ForEach-Object { Write-Output $_ }
    & $stack up -Project $project -LocalDir $localDir -Port $port -RelaxRateLimits -MinFreeMb 1200 -WaitForMemorySeconds 120 | ForEach-Object { Write-Output $_ }
    & $stack seed -Project $project -LocalDir $localDir -Port $port | ForEach-Object { Write-Output $_ }

    # "up" built the image from the working tree a moment ago; which image the API container really
    # runs, and when it was built, is recorded so the browser result can be tied to it.
    $apiContainer = (Invoke-Native { & $docker ps --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=api' --format '{{.Names}}' }) | Select-Object -First 1
    $imageId = ((Invoke-Native { & $docker inspect $apiContainer --format '{{.Image}}' }) -join '').Trim()
    $imageCreated = ((Invoke-Native { & $docker image inspect $imageId --format '{{.Created}}' }) -join '').Trim()
    $image = ($imageId -creplace '^sha256:', '').Substring(0, 12) + " built $imageCreated"
    Write-Output "api container image: $imageId created $imageCreated"

    # The browser is the heaviest part; check again now that the stack is up.
    $memory = Wait-ForMemory -RequiredMb 700 -WaitSeconds 120
    Write-Output ("free memory before browser: {0} MB" -f $memory.FreeMb)
    if (-not $memory.Ok) {
        Write-Output 'GATE-NOTE: not enough memory to start the browser'
        $exitCode = 3
    }
    else {

        $env:KANTAR_E2E_BASE_URL = "https://localhost:$port"
        $env:KANTAR_E2E_SEED_DIR = Join-Path $root "$localDir\seed"
        $env:KANTAR_E2E_ARTIFACT_DIR = Join-Path $root 'artifacts\verification\screenshots'

        # A result file left by an earlier run must never be counted as this run's result.
        $results = Join-Path $web 'test-results\e2e-results.json'
        if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Force }

        Push-Location -LiteralPath $web
        try {
            $lines = Invoke-Native { npm run --silent e2e }
            $exit = $LASTEXITCODE
            $lines | ForEach-Object { Write-Output $_ }
        }
        finally { Pop-Location }

        $note = 'no result file'
        $skipped = 0; $expected = 0; $unexpected = 0; $flaky = 0
        if (Test-Path -LiteralPath $results) {
            $stats = (Get-Content -LiteralPath $results -Raw | ConvertFrom-Json).stats
            $expected = [int] $stats.expected; $unexpected = [int] $stats.unexpected; $skipped = [int] $stats.skipped; $flaky = [int] $stats.flaky
            $note = "browser tests: passed $expected, failed $unexpected, skipped $skipped, flaky $flaky (1 worker; image $image)"
        }
        if ($exit -eq 0 -and $expected -gt 0 -and $unexpected -eq 0 -and $skipped -eq 0 -and $flaky -eq 0) { $exitCode = 0 }
        Write-Output "GATE-NOTE: $note"
    }
}
finally {
    if (-not $KeepStack) { Remove-OwnStack }
    $neighboursAfter = @(Invoke-Native { & $docker ps --format '{{.Names}}' } | Where-Object { $_ -cnotmatch '^kantar-e2e-' } | Sort-Object)
    if (($neighboursBefore -join ',') -cne ($neighboursAfter -join ',')) {
        # Reported, not failed: the user may have started or stopped their own containers meanwhile.
        Write-Output 'note: the set of other running containers changed while this gate ran; this gate only manages kantar-e2e-*'
    }
}
exit $exitCode
