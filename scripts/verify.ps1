<#
.SYNOPSIS
  Runs Kantar's verification gates one at a time, with a memory check before each and a durable
  checkpoint after each.

.DESCRIPTION
  Gates (in order): scripts, restore, build, unit, integration, web, migrations, e2e, smoke, scans, ai-live

  -Gate <names>   Run only these gates (in the fixed order above). Default: all.
  -Force          Run a gate again even when a passing checkpoint exists for the current source.
  -List           Show the gates, their memory thresholds and the checkpoints for the current source.

  Checkpoints are keyed by the source fingerprint (scripts/source-fingerprint.ps1). A gate that already
  passed on the current fingerprint is not run again; it is reported as "pass (earlier invocation)" with
  the time and invocation id of the run that produced it. Results from separate invocations are never
  presented as one run: the summary lists, per gate, when and in which invocation it ran.

  Before every gate the free memory is measured. If it is below the gate's threshold the script waits
  (-WaitForMemorySeconds), and if memory does not recover it stops with a checkpoint and exit code 3.
  It never frees memory by stopping other processes or containers.

  Exit codes: 0 all requested gates passed (or were not runnable by design, see ai-live),
              1 a gate failed or the source changed while running,
              3 stopped at a memory checkpoint; run the same command again later.

  Thresholds can be overridden per gate: KANTAR_VERIFY_MIN_FREE_MB_<GATE> (e.g. ..._INTEGRATION=1500).
#>
[CmdletBinding()]
param(
    [string[]] $Gate,
    [switch] $Force,
    [switch] $List,
    [int] $WaitForMemorySeconds = 180
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\common.ps1')
. (Join-Path $PSScriptRoot 'lib\verify-lib.ps1')

$root = Get-RepoRoot
Set-Location -LiteralPath $root
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Disable-BuildNodeReuse

$artifactDir = Join-Path $root 'artifacts\verification'
$rawDir = Join-Path $artifactDir 'raw'
New-Item -ItemType Directory -Force -Path $rawDir | Out-Null
$checkpointPath = Join-Path $artifactDir 'checkpoints.json'
$fingerprintScript = Join-Path $PSScriptRoot 'source-fingerprint.ps1'

function Get-Fingerprint { return ((& $fingerprintScript) -join '').Trim() }

# name, minimum free MB, command. Each command returns lines; its exit code is $LASTEXITCODE.
$gates = [ordered]@{
    'scripts'     = @{ MinFreeMb = 300;  Tests = $false; Run = { & (Join-Path $PSScriptRoot 'tests\run.ps1') } }
    'restore'     = @{ MinFreeMb = 500;  Tests = $false; Run = { Invoke-Native { dotnet restore Kantar.slnx --locked-mode --disable-build-servers } } }
    'build'       = @{ MinFreeMb = 700;  Tests = $false; Run = { Invoke-Native { dotnet build Kantar.slnx -c Release --no-restore --disable-build-servers -m:2 -v:minimal } } }
    'unit'        = @{ MinFreeMb = 700;  Tests = $true;  Run = { Invoke-Native { dotnet test --project tests/Kantar.UnitTests -c Release --no-build } } }
    'integration' = @{ MinFreeMb = 1200; Tests = $true;  Run = { Invoke-Native { dotnet test --project tests/Kantar.IntegrationTests -c Release --no-build } } }
    'web'         = @{ MinFreeMb = 700;  Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\web.ps1') } }
    'migrations'  = @{ MinFreeMb = 1200; Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\migrations.ps1') } }
    'e2e'         = @{ MinFreeMb = 1500; Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\e2e.ps1') } }
    'smoke'       = @{ MinFreeMb = 1200; Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\stack-smoke.ps1') } }
    'scans'       = @{ MinFreeMb = 500;  Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\scan.ps1') } }
    'ai-live'     = @{ MinFreeMb = 1500; Tests = $false; Run = { & (Join-Path $PSScriptRoot 'gates\ai-live.ps1') } }
}

$requested = @($gates.Keys)
$Gate = Expand-GateList $Gate
if ($Gate) {
    $unknown = @($Gate | Where-Object { $gates.Keys -cnotcontains $_ })
    if ($unknown.Count -gt 0) { throw "Unknown gate(s): $($unknown -join ', '). Known: $($gates.Keys -join ', ')" }
    $requested = @($gates.Keys | Where-Object { $Gate -ccontains $_ })
}

$fingerprint = Get-Fingerprint
$store = Read-Checkpoints -Path $checkpointPath

if ($List) {
    Write-Output $fingerprint
    foreach ($name in $gates.Keys) {
        $checkpoint = Get-Checkpoint -Store $store -Fingerprint $fingerprint -Gate $name
        $state = 'no checkpoint for this source'
        if ($checkpoint) { $state = "{0} at {1} (invocation {2})" -f $checkpoint.result, $checkpoint.startedUtc, $checkpoint.invocation }
        Write-Output ("{0,-12} min free {1,5} MB   {2}" -f $name, (Get-GateThreshold -Gate $name -Default $gates[$name].MinFreeMb), $state)
    }
    exit 0
}

$invocation = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + (New-Secret -Bytes 3).ToLowerInvariant()
$rows = New-Object System.Collections.Generic.List[object]
$failed = $false
$blocked = $false

foreach ($name in $requested) {
    $definition = $gates[$name]
    $existing = Get-Checkpoint -Store $store -Fingerprint $fingerprint -Gate $name
    if ($existing -and $existing.result -ceq 'pass' -and -not $Force) {
        $rows.Add([pscustomobject]@{ Gate = $name; Result = 'pass (earlier invocation)'; Counts = $existing.counts; When = $existing.startedUtc; Invocation = $existing.invocation; Seconds = $existing.seconds })
        continue
    }

    $required = Get-GateThreshold -Gate $name -Default $definition.MinFreeMb
    $memory = Wait-ForMemory -RequiredMb $required -WaitSeconds $WaitForMemorySeconds
    Write-Output ("[{0}] free memory {1} MB, required {2} MB" -f $name, $memory.FreeMb, $required)
    if (-not $memory.Ok) {
        $blocked = $true
        $note = "blocked: free memory $($memory.FreeMb) MB is below $required MB"
        $rows.Add([pscustomobject]@{ Gate = $name; Result = 'blocked-by-memory'; Counts = $note; When = (Get-Date).ToUniversalTime().ToString('o'); Invocation = $invocation; Seconds = 0 })
        Set-Checkpoint -Store $store -Fingerprint $fingerprint -Gate $name -Value @{ result = 'blocked-by-memory'; exit = 3; counts = $note; startedUtc = (Get-Date).ToUniversalTime().ToString('o'); seconds = 0; invocation = $invocation; log = '' }
        Write-Checkpoints -Path $checkpointPath -Store $store
        Write-Output "[$name] $note. Stopping at this checkpoint; nothing else was started."
        break
    }

    $started = Get-Date
    $logName = Get-GateLogName $name
    $logPath = Join-Path $rawDir $logName
    $lines = @(& $definition.Run)
    $exit = $LASTEXITCODE
    if ($null -eq $exit) { $exit = 0 }
    $lines | ForEach-Object { Protect-LogLine $_ } | Where-Object { $_ -cnotmatch 'testcontainers\.org' } | Set-Content -LiteralPath $logPath -Encoding utf8
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    $counts = ''
    $result = 'fail'
    if ($definition.Tests) {
        $summary = Get-TestCounts -Lines $lines
        $counts = $summary.Text
        # A test gate passes only when tests ran, none failed and none was skipped.
        if ($exit -eq 0 -and $summary.Total -gt 0 -and $summary.Failed -eq 0 -and $summary.Skipped -eq 0) { $result = 'pass' }
    }
    else {
        $counts = Get-GateNote -Lines $lines
        if ($exit -eq 0) { $result = 'pass' }
        if ($exit -eq 4) { $result = 'not-run' }      # the gate cannot run in this environment (e.g. no local model)
        if ($exit -eq 3) { $result = 'blocked-by-memory' }
    }

    if ($result -ceq 'fail') { $failed = $true }
    if ($result -ceq 'blocked-by-memory') { $blocked = $true }
    $rows.Add([pscustomobject]@{ Gate = $name; Result = $result; Counts = $counts; When = $started.ToUniversalTime().ToString('o'); Invocation = $invocation; Seconds = $seconds })
    Set-Checkpoint -Store $store -Fingerprint $fingerprint -Gate $name -Value @{ result = $result; exit = $exit; counts = $counts; startedUtc = $started.ToUniversalTime().ToString('o'); seconds = $seconds; invocation = $invocation; log = "artifacts/verification/raw/$logName" }
    Write-Checkpoints -Path $checkpointPath -Store $store
    Write-Output ("[{0}] {1} (exit {2}, {3} s) {4}" -f $name, $result, $exit, $seconds, $counts)

    if ($result -ceq 'fail') {
        $lines | Select-Object -Last 25 | ForEach-Object { Write-Output ('    ' + (Protect-LogLine $_)) }
        break
    }
    if ($result -ceq 'blocked-by-memory') { break }
}

$after = Get-Fingerprint
$sourceChanged = $after -cne $fingerprint
if ($sourceChanged) { $failed = $true }

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("Kantar verification summary")
$summaryLines.Add("generated (UTC): " + (Get-Date).ToUniversalTime().ToString('o'))
$summaryLines.Add("this invocation: $invocation")
$summaryLines.Add($fingerprint)
$summaryLines.Add("")
$summaryLines.Add("Rows marked 'earlier invocation' were NOT run now; they passed on this same source in the invocation named in the row.")
$summaryLines.Add("")
$summaryLines.Add(("{0,-12} {1,-26} {2,-22} {3,8}  {4}" -f 'gate', 'result', 'invocation', 'seconds', 'detail'))
foreach ($row in $rows) {
    $summaryLines.Add(("{0,-12} {1,-26} {2,-22} {3,8}  {4}" -f $row.Gate, $row.Result, $row.Invocation, $row.Seconds, $row.Counts))
}
$notRequested = @($gates.Keys | Where-Object { $requested -cnotcontains $_ })
if ($notRequested.Count -gt 0) { $summaryLines.Add(""); $summaryLines.Add("not requested in this invocation: " + ($notRequested -join ', ')) }
$notReached = @($requested | Where-Object { ($rows | ForEach-Object { $_.Gate }) -cnotcontains $_ })
if ($notReached.Count -gt 0) { $summaryLines.Add("requested but not reached: " + ($notReached -join ', ')) }
if ($sourceChanged) { $summaryLines.Add(""); $summaryLines.Add("THE SOURCE CHANGED DURING THIS RUN. The results above do not describe one source state."); $summaryLines.Add("after: $after") }

$summaryLines | Set-Content -LiteralPath (Join-Path $artifactDir 'verify-summary.txt') -Encoding utf8
$summaryLines | ForEach-Object { Write-Output $_ }

if ($failed) { exit 1 }
if ($blocked) { exit 3 }
exit 0
