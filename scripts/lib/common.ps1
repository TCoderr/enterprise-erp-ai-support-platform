# Shared helpers for the Kantar scripts. ASCII only on purpose: Windows PowerShell 5.1 reads
# BOM-less files in the ANSI code page, and these scripts must behave the same under tr-TR and en-US.
# Dot-source this file; it defines functions and changes no state.

Set-StrictMode -Version 3.0

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}

function Find-Docker {
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $fallback = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw 'docker was not found. Start Docker Desktop and make sure docker is on PATH.'
}

# Free physical memory in MB as the operating system reports it (free + standby that can be reclaimed).
function Get-FreeMemoryMb {
    if ($env:KANTAR_FAKE_FREE_MB) { return [int]$env:KANTAR_FAKE_FREE_MB }   # test seam for scripts/tests only
    if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
        $line = Get-Content /proc/meminfo | Where-Object { $_ -like 'MemAvailable:*' } | Select-Object -First 1
        return [int]([long](($line -split '\s+')[1]) / 1024)
    }
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return [int]($os.FreePhysicalMemory / 1024)
}

# Decides whether a step that needs $RequiredMb may start. Pure: used directly by the tests.
function Test-MemorySufficient([int] $FreeMb, [int] $RequiredMb) {
    return $FreeMb -ge $RequiredMb
}

# Polls until enough memory is free or the wait budget is used up. Never frees memory by itself and
# never touches other processes or containers.
function Wait-ForMemory([int] $RequiredMb, [int] $WaitSeconds = 0, [int] $PollSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ($true) {
        $free = Get-FreeMemoryMb
        if (Test-MemorySufficient -FreeMb $free -RequiredMb $RequiredMb) {
            return [pscustomobject]@{ Ok = $true; FreeMb = $free; RequiredMb = $RequiredMb }
        }
        if ((Get-Date) -ge $deadline) {
            return [pscustomobject]@{ Ok = $false; FreeMb = $free; RequiredMb = $RequiredMb }
        }
        Start-Sleep -Seconds $PollSeconds
    }
}

function New-RandomBytes([int] $Count) {
    $buffer = New-Object byte[] $Count
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return , $buffer
}

function New-Secret([int] $Bytes = 24) {
    return ([Convert]::ToBase64String((New-RandomBytes $Bytes))).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
}

# A password that satisfies both the SQL Server policy and the application's password rule.
function New-Password([string] $Prefix = 'Kt!') {
    return $Prefix + (New-Secret -Bytes 18) + 'aA9'
}

# Writes a private file once. An existing file is never overwritten, so `init` is repeatable and a
# later run cannot silently change a password that the database already uses. Nothing is printed.
function Set-PrivateFile([string] $Path, [string] $Value) {
    if (Test-Path -LiteralPath $Path) { return $false }
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

function Test-PortFree([int] $Port) {
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start(); $listener.Stop(); return $true } catch { return $false }
}

# Log file name for a gate. -creplace and ToLowerInvariant on purpose: under tr-TR a case-insensitive
# [a-z] class and ToLower() treat "I" differently, which would change file names with the culture.
function Get-GateLogName([string] $Gate) {
    $name = ($Gate -creplace '[^a-zA-Z0-9]+', '-').ToLowerInvariant().Trim('-')
    return "$name.log"
}

# Removes values that must not reach a log kept on disk.
function Protect-LogLine([string] $Line) {
    if ($null -eq $Line) { return '' }
    $masked = $Line -creplace '(?i)(password|pwd|secret|token|api[_-]?key)(\s*[=:]\s*)[^;\s"'',]+', '$1$2***'
    $masked = $masked -creplace '(?i)(MSSQL_SA_PASSWORD=)\S+', '$1***'
    return $masked
}

# Runs a native command and returns its combined output as strings. Exit code is left in
# $global:LASTEXITCODE. stderr progress lines (docker, npm) are not turned into PowerShell errors.
function Invoke-Native([scriptblock] $Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        $output = & $Command 2>&1 | ForEach-Object { "$_" }
        return @($output)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# For the current process and its children: dotnet commands that build or restore must not leave idle
# MSBuild worker processes behind for reuse (about 100 MB each, for up to 15 minutes). Called by the
# verification entry points only; it changes nothing outside this process tree.
function Disable-BuildNodeReuse {
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
}

# True only for a stack directory this product created: inside the repository, named .local or
# .local-<suffix>, and carrying a manifest written by `local-stack init` for the same project.
function Test-OwnedLocalDir([string] $Root, [string] $LocalDir, [string] $Project) {
    if (-not (Test-Path -LiteralPath $LocalDir -PathType Container)) { return $false }
    $resolved = (Resolve-Path -LiteralPath $LocalDir).Path
    $parent = Split-Path -Parent $resolved
    $leaf = Split-Path -Leaf $resolved
    if ($parent -cne $Root) { return $false }
    if ($leaf -cnotmatch '^\.local(-[a-z0-9]+)?$') { return $false }
    $manifestPath = Join-Path $resolved 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { return $false }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } catch { return $false }
    return ($manifest.product -ceq 'kantar' -and $manifest.project -ceq $Project)
}

function Test-ProjectName([string] $Project) {
    return $Project -cmatch '^kantar(-[a-z0-9]+)?$'
}
