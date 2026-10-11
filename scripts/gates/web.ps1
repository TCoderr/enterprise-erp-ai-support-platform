<#
.SYNOPSIS
  Web gate: locked install, type check, lint, component tests, production build.
  Prints the tools' output; exit code 0 only if every step passed and no test was skipped.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')

$web = Join-Path (Get-RepoRoot) 'src\web'
if (-not (Test-Path -LiteralPath (Join-Path $web 'package-lock.json'))) {
    Write-Output 'GATE-NOTE: src/web/package-lock.json is missing'
    exit 1
}

Push-Location -LiteralPath $web
try {
    # npm ci only when the installed tree does not match the lockfile; it is the slowest and most memory-hungry step.
    $installedLock = Join-Path $web 'node_modules\.package-lock.json'
    $needsInstall = -not (Test-Path -LiteralPath $installedLock) -or
        ((Get-Item -LiteralPath (Join-Path $web 'package-lock.json')).LastWriteTimeUtc -gt (Get-Item -LiteralPath $installedLock).LastWriteTimeUtc)

    $steps = New-Object System.Collections.Generic.List[object]
    if ($needsInstall) { $steps.Add(@{ Name = 'npm ci'; Run = { npm ci --no-audit --no-fund } }) }
    $steps.Add(@{ Name = 'typecheck'; Run = { npm run --silent typecheck } })
    $steps.Add(@{ Name = 'lint'; Run = { npm run --silent lint } })
    $steps.Add(@{ Name = 'test'; Run = { npm run --silent test } })
    $steps.Add(@{ Name = 'build'; Run = { npm run --silent build } })

    $testNote = 'component tests: no summary found'
    foreach ($step in $steps) {
        Write-Output "--- $($step.Name)"
        $lines = Invoke-Native $step.Run
        $exit = $LASTEXITCODE
        $lines | ForEach-Object { Write-Output $_ }
        if ($step.Name -ceq 'test') {
            $passed = 0; $failedCount = 0; $skipped = 0
            foreach ($line in $lines) {
                $plain = $line -creplace '\x1b\[[0-9;]*m', ''
                if ($plain -cmatch '^\s*Tests\s+(.*)$') {
                    $detail = $Matches[1]
                    if ($detail -cmatch '(\d+)\s+passed') { $passed = [int] $Matches[1] }
                    if ($detail -cmatch '(\d+)\s+failed') { $failedCount = [int] $Matches[1] }
                    if ($detail -cmatch '(\d+)\s+(skipped|todo)') { $skipped = [int] $Matches[1] }
                }
            }
            $testNote = "component tests: passed $passed, failed $failedCount, skipped $skipped"
            if ($exit -eq 0 -and ($passed -eq 0 -or $skipped -gt 0)) {
                Write-Output "GATE-NOTE: $testNote (a run with no tests or with skipped tests does not pass)"
                exit 1
            }
        }
        if ($exit -ne 0) {
            Write-Output "GATE-NOTE: web step '$($step.Name)' failed (exit $exit); $testNote"
            exit 1
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $web 'dist\index.html'))) {
        Write-Output 'GATE-NOTE: build finished but dist/index.html is missing'
        exit 1
    }

    Write-Output "GATE-NOTE: typecheck, lint, build ok; $testNote"
    exit 0
}
finally { Pop-Location }
