<#
.SYNOPSIS
  Migration gate.
    1. The EF model has no changes that are missing from the migrations.
    2. Real application of the migrations on SQL Server (integration tests in the Migration* classes):
       empty database -> latest, and first migration + synthetic data -> latest, with the restricted
       login working afterwards.
  Generating a SQL script is NOT treated as proof; only step 2 applies migrations to a real server.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')
. (Join-Path $PSScriptRoot '..\lib\verify-lib.ps1')

Set-Location -LiteralPath (Get-RepoRoot)

Disable-BuildNodeReuse   # both commands below build

Write-Output '--- dotnet tool restore'
$lines = Invoke-Native { dotnet tool restore }
$lines | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) { Write-Output 'GATE-NOTE: dotnet tool restore failed'; exit 1 }

Write-Output '--- model has no pending changes'
$lines = Invoke-Native { dotnet ef migrations has-pending-model-changes --project src/Kantar.Infrastructure --startup-project src/Kantar.Infrastructure --configuration Release }
$lines | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) { Write-Output 'GATE-NOTE: the EF model has changes that are not in a migration'; exit 1 }

Write-Output '--- apply on a real SQL Server (empty install and upgrade with data)'
$lines = Invoke-Native { dotnet test --project tests/Kantar.IntegrationTests -c Release --filter-class '*MigrationTests' }
$exit = $LASTEXITCODE
$lines | ForEach-Object { Write-Output $_ }
$counts = Get-TestCounts -Lines $lines
if ($exit -ne 0 -or $counts.Total -eq 0 -or $counts.Failed -gt 0 -or $counts.Skipped -gt 0) {
    Write-Output "GATE-NOTE: migration apply tests did not pass ($($counts.Text))"
    exit 1
}

Write-Output "GATE-NOTE: model in sync; real apply tests: $($counts.Text)"
exit 0
