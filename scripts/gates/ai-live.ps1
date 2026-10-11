<#
.SYNOPSIS
  Live local-model gate. Runs the synthetic scenario set (src/Kantar.Infrastructure/Assistance/
  LiveModelCheck.cs) against a real, already-running local model server through the product's own
  prompt builder and response parser.

  This gate is opt-in and never downloads, installs or starts a model, and never calls a remote or
  paid endpoint. Configure an existing local server with:
      $env:KANTAR_AI_LOCAL_BASE_URL = 'http://127.0.0.1:11434'    # loopback only
      $env:KANTAR_AI_LOCAL_MODEL    = '<model name already present on that server>'

  Exit codes: 0 scenarios passed, 1 a scenario failed, 4 NOT RUN (nothing configured or the server is
  not reachable). "Not run" is reported as such; the deterministic demo provider's tests do not stand
  in for it.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')

Set-Location -LiteralPath (Get-RepoRoot)

if (-not $env:KANTAR_AI_LOCAL_BASE_URL -or -not $env:KANTAR_AI_LOCAL_MODEL) {
    Write-Output 'No local model endpoint is configured (KANTAR_AI_LOCAL_BASE_URL / KANTAR_AI_LOCAL_MODEL).'
    Write-Output 'GATE-NOTE: NOT RUN - no local model configured; live model behaviour is unverified'
    exit 4
}

$env:Ai__Provider = 'LocalModel'
$env:Ai__LocalModel__BaseUrl = $env:KANTAR_AI_LOCAL_BASE_URL
$env:Ai__LocalModel__Model = $env:KANTAR_AI_LOCAL_MODEL
try {
    $lines = Invoke-Native { dotnet run --project src/Kantar.Api -c Release --no-build -- --ai-live }
    $exit = $LASTEXITCODE
    $lines | ForEach-Object { Write-Output $_ }
    $passed = @($lines | Where-Object { $_ -cmatch '^SCENARIO .* PASS' }).Count
    $failedCount = @($lines | Where-Object { $_ -cmatch '^SCENARIO .* FAIL' }).Count
    if ($exit -eq 4) {
        Write-Output 'GATE-NOTE: NOT RUN - the configured local model server did not answer; live model behaviour is unverified'
        exit 4
    }
    Write-Output ("GATE-NOTE: live model {0}: scenarios passed {1}, failed {2} (synthetic set; not a benchmark)" -f $env:KANTAR_AI_LOCAL_MODEL, $passed, $failedCount)
    if ($exit -ne 0 -or $failedCount -gt 0 -or $passed -eq 0) { exit 1 }
    exit 0
}
finally {
    Remove-Item Env:\Ai__Provider, Env:\Ai__LocalModel__BaseUrl, Env:\Ai__LocalModel__Model -ErrorAction SilentlyContinue
}
