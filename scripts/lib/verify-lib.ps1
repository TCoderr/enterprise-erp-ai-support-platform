# Pure helpers of verify.ps1, kept apart so scripts/tests can exercise them without running a gate.
# ASCII only (see common.ps1).

Set-StrictMode -Version 3.0

# ---- checkpoint store: { "<fingerprint>": { "<gate>": { result, exit, counts, startedUtc, seconds, invocation, log } } }

function Read-Checkpoints([string] $Path) {
    $store = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $store }
    try { $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $store }
    if ($null -eq $json) { return $store }
    foreach ($fingerprint in $json.PSObject.Properties) {
        $gates = @{}
        foreach ($gate in $fingerprint.Value.PSObject.Properties) {
            $entry = @{}
            foreach ($field in $gate.Value.PSObject.Properties) { $entry[$field.Name] = $field.Value }
            $gates[$gate.Name] = $entry
        }
        $store[$fingerprint.Name] = $gates
    }
    return $store
}

function Write-Checkpoints([string] $Path, [hashtable] $Store) {
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    # Written to a temporary file first so an interrupted run cannot leave a half-written store.
    $temporary = "$Path.tmp"
    ($Store | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-Checkpoint([hashtable] $Store, [string] $Fingerprint, [string] $Gate) {
    if (-not $Store.ContainsKey($Fingerprint)) { return $null }
    $gates = $Store[$Fingerprint]
    if (-not $gates.ContainsKey($Gate)) { return $null }
    return [pscustomobject] $gates[$Gate]
}

function Set-Checkpoint([hashtable] $Store, [string] $Fingerprint, [string] $Gate, [hashtable] $Value) {
    if (-not $Store.ContainsKey($Fingerprint)) { $Store[$Fingerprint] = @{} }
    $Store[$Fingerprint][$Gate] = $Value
}

# "-Gate a,b" arrives as one string "a,b" when the script is started with "powershell -File";
# both forms (array and comma-separated string) are accepted.
function Expand-GateList([string[]] $Gate) {
    if (-not $Gate) { return @() }
    return @($Gate | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# KANTAR_VERIFY_MIN_FREE_MB_<GATE> overrides the default; invalid values are ignored.
function Get-GateThreshold([string] $Gate, [int] $Default) {
    $suffix = ($Gate -creplace '[^a-zA-Z0-9]', '_').ToUpperInvariant()
    $value = [Environment]::GetEnvironmentVariable("KANTAR_VERIFY_MIN_FREE_MB_$suffix")
    $parsed = 0
    if ($value -and [int]::TryParse($value, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref] $parsed) -and $parsed -gt 0) {
        return $parsed
    }
    return $Default
}

# Parses the Microsoft.Testing.Platform summary ("total: 21", "failed: 0", "succeeded: 21", "skipped: 0").
# Returns zeros when no summary is present, which makes a test gate fail rather than pass silently.
function Get-TestCounts([string[]] $Lines) {
    $counts = @{ total = 0; failed = 0; succeeded = 0; skipped = 0 }
    $found = $false
    foreach ($line in $Lines) {
        if ($line -cmatch '^\s*(total|failed|succeeded|skipped):\s*(\d+)\s*$') {
            $counts[$Matches[1]] += [int] $Matches[2]
            $found = $true
        }
    }
    $text = 'no test summary found'
    if ($found) { $text = "total $($counts.total), passed $($counts.succeeded), failed $($counts.failed), skipped $($counts.skipped)" }
    return [pscustomobject]@{ Total = $counts.total; Failed = $counts.failed; Succeeded = $counts.succeeded; Skipped = $counts.skipped; Text = $text }
}

# Gate scripts may print "GATE-NOTE: <text>" lines; the last one becomes the summary detail.
function Get-GateNote([string[]] $Lines) {
    $note = ''
    foreach ($line in $Lines) {
        if ($line -cmatch '^GATE-NOTE:\s*(.*)$') { $note = $Matches[1] }
    }
    return $note
}
