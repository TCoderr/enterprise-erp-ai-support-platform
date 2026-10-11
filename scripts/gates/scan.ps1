<#
.SYNOPSIS
  Static scans: secrets, authorship metadata, known-vulnerable packages, license inventory, shipped
  font licenses.

  What this is: pattern and advisory-database checks at the time of the run.
  What this is not: a security audit. "No finding" means these checks found nothing, not that the
  product is free of vulnerabilities.

  The secret scanner is checked against itself on every run: it must flag a planted sample (positive
  control) and must not flag documented placeholders (false-positive control).
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')
. (Join-Path $PSScriptRoot '..\lib\scan-lib.ps1')

$root = Get-RepoRoot
Set-Location -LiteralPath $root
Disable-BuildNodeReuse   # "dotnet list package" restores through MSBuild
$artifactDir = Join-Path $root 'artifacts\verification'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$failed = $false
$notes = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- scanner self-check
$probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kantar-scan-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
try {
    # Built at run time so that this script does not itself contain a secret-shaped literal.
    $planted = 'Server=db;User Id=app;Pass' + 'word=' + 'Zx9' + 'qLm42' + 'TrewQ;'
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'positive.txt'), "connection: $planted`n-----BEGIN RSA PRIVATE" + " KEY-----`n")
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'negative.txt'), "Password=<generated-by-local-stack-init>`nMSSQL_SA_PASSWORD=changeme-placeholder`nPassword=`${PASSWORD}`npassword: '***'`n")
    $positive = @(Find-SecretFindings -Root $probeDir -Files @('positive.txt'))
    $negative = @(Find-SecretFindings -Root $probeDir -Files @('negative.txt'))
    Write-Output ("secret scanner self-check: positive control findings {0} (need >= 2), false-positive control findings {1} (need 0)" -f $positive.Count, $negative.Count)
    if ($positive.Count -lt 2 -or $negative.Count -ne 0) { $failed = $true; $notes.Add('secret scanner self-check failed') }
}
finally { Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- secrets in the source tree
$files = @(& git -c core.quotepath=off ls-files --cached --others --exclude-standard)
$findings = @(Find-SecretFindings -Root $root -Files $files)
Write-Output ("secret scan: {0} files, {1} findings" -f $files.Count, $findings.Count)
foreach ($finding in $findings) { Write-Output ("  {0}:{1} [{2}]" -f $finding.File, $finding.Line, $finding.Rule) }   # the matched text is never printed
if ($findings.Count -gt 0) { $failed = $true; $notes.Add("secret findings: $($findings.Count)") }

# The private directories are ignored by directory patterns (".local/"). Git only applies such a
# pattern to a path it knows to be a directory, and these directories do not exist between runs, so
# the question asked is the one that matters: would a file inside be ignored?
$ignored = [ordered]@{ '.local' = '.local/secrets/probe'; '.local-e2e' = '.local-e2e/secrets/probe'; '.local-smoke' = '.local-smoke/secrets/probe'; '.env' = '.env' }
foreach ($name in $ignored.Keys) {
    $probe = $ignored[$name]
    $check = Invoke-Native { git check-ignore -q $probe }
    if ($LASTEXITCODE -ne 0) { $failed = $true; $notes.Add("$name is not git-ignored"); Write-Output "NOT IGNORED: $name" }
}
Write-Output ("git-ignore check: {0} private paths" -f $ignored.Count)

# ---------------------------------------------------------------- authorship
$authorship = @(Find-AuthorshipFindings -Root $root -Files $files)
Write-Output ("authorship scan: {0} findings" -f $authorship.Count)
foreach ($finding in $authorship) { Write-Output ("  {0}:{1} [{2}]" -f $finding.File, $finding.Line, $finding.Rule) }
if ($authorship.Count -gt 0) { $failed = $true; $notes.Add("authorship findings: $($authorship.Count)") }

# ---------------------------------------------------------------- vulnerable packages (advisory databases, at run time)
Write-Output '--- dotnet list package --vulnerable --include-transitive'
$lines = Invoke-Native { dotnet list Kantar.slnx package --vulnerable --include-transitive }
$dotnetExit = $LASTEXITCODE
$lines | ForEach-Object { Write-Output $_ }
$dotnetVulnerable = @($lines | Where-Object { $_ -cmatch 'has the following vulnerable packages' }).Count
if ($dotnetExit -ne 0) { $failed = $true; $notes.Add('dotnet vulnerability query failed (network?)') }
elseif ($dotnetVulnerable -gt 0) { $failed = $true; $notes.Add("NuGet: $dotnetVulnerable project(s) with known-vulnerable packages") }

$web = Join-Path $root 'src\web'
$npmNote = 'npm audit: not run (no src/web/package-lock.json)'
if (Test-Path -LiteralPath (Join-Path $web 'package-lock.json')) {
    Write-Output '--- npm audit --omit=dev --audit-level=high'
    Push-Location -LiteralPath $web
    try {
        $lines = Invoke-Native { npm audit --omit=dev --audit-level=high }
        $npmExit = $LASTEXITCODE
        $lines | Select-Object -Last 15 | ForEach-Object { Write-Output $_ }
    }
    finally { Pop-Location }
    $npmNote = 'npm audit (runtime deps, high+): none'
    if ($npmExit -ne 0) { $failed = $true; $npmNote = 'npm audit reported high/critical advisories or could not run'; $notes.Add($npmNote) }
}

# ---------------------------------------------------------------- license inventory
$nuget = @(Get-NuGetLicenseInventory -Root $root)
$nuget | Sort-Object Id | ForEach-Object { "{0} {1} : {2}" -f $_.Id, $_.Version, $_.License } | Set-Content -LiteralPath (Join-Path $artifactDir 'licenses-nuget.txt') -Encoding utf8
$unknownNuGet = @($nuget | Where-Object { $_.License -ceq 'UNKNOWN' })
Write-Output ("NuGet license inventory: {0} packages, {1} without a declared license expression (listed in artifacts/verification/licenses-nuget.txt)" -f $nuget.Count, $unknownNuGet.Count)

$npm = @(Get-NpmLicenseInventory -WebRoot $web)
$npm | Sort-Object Name | ForEach-Object { "{0} {1} : {2}" -f $_.Name, $_.Version, $_.License } | Set-Content -LiteralPath (Join-Path $artifactDir 'licenses-npm-runtime.txt') -Encoding utf8
$copyleft = @(($nuget | ForEach-Object { $_.License }) + ($npm | ForEach-Object { $_.License }) | Where-Object { $_ -cmatch '(^|[^L])GPL|AGPL|SSPL' })
Write-Output ("npm runtime license inventory: {0} packages" -f $npm.Count)
if ($copyleft.Count -gt 0) { $failed = $true; $notes.Add("strong-copyleft license found: $((@($copyleft) | Sort-Object -Unique) -join ', ')") }

# ---------------------------------------------------------------- shipped fonts carry their license
$fontLicenses = @(Get-ChildItem -LiteralPath (Join-Path $web 'public\licenses') -Filter '*OFL*' -ErrorAction SilentlyContinue)
Write-Output ("font license files shipped with the UI: {0}" -f $fontLicenses.Count)
if ((Test-Path -LiteralPath $web) -and $fontLicenses.Count -eq 0) { $failed = $true; $notes.Add('no OFL font license file under src/web/public/licenses') }

if ($failed) {
    Write-Output ("GATE-NOTE: scan findings: " + ($notes -join '; '))
    exit 1
}
Write-Output ("GATE-NOTE: secrets 0 findings in {0} files (self-check ok), authorship 0, NuGet advisories none, {1}; licenses: {2} NuGet / {3} npm runtime; not a security audit" -f $files.Count, $npmNote, $nuget.Count, $npm.Count)
exit 0
