<#
.SYNOPSIS
  Prints a fingerprint of the source that verification results belong to.

.DESCRIPTION
  SHA-256 over "<relative path>:<file SHA-256>" lines of every file git would track (tracked plus
  untracked-but-not-ignored), sorted ordinally. A commit is not required.

  Excluded from the fingerprint, because they are records ABOUT a source state rather than part of it:
    docs/**, artifacts/**, design/**, README.md, THIRD-PARTY-NOTICES.md, PRODUCT.md, DESIGN.md
  Everything else (src, tests, scripts, db, compose, Dockerfile, CI, lock files) is included.

  Output: "source-fingerprint sha256:<64 hex> files=<n>"
#>
[CmdletBinding()]
param([string] $Root)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }

Push-Location -LiteralPath $Root
try {
    if (-not (Test-Path -LiteralPath (Join-Path $Root '.git'))) { throw 'A .git directory is required (git init is enough; no commit is needed).' }
    $files = @(& git -c core.quotepath=off ls-files --cached --others --exclude-standard) |
        Where-Object { $_ -and $_ -cnotmatch '^(docs|artifacts|design)/' -and $_ -cnotmatch '^(README|THIRD-PARTY-NOTICES|PRODUCT|DESIGN)\.md$' }
    $files = [string[]] @($files | Sort-Object -Unique)
    [Array]::Sort($files, [System.StringComparer]::Ordinal)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = New-Object System.Text.StringBuilder
        $count = 0
        foreach ($file in $files) {
            $path = Join-Path $Root $file
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
            $hash = [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))).Replace('-', '').ToLowerInvariant()
            [void] $builder.Append($file).Append(':').Append($hash).Append("`n")
            $count++
        }
        $fingerprint = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($builder.ToString()))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }

    Write-Output ("source-fingerprint sha256:{0} files={1}" -f $fingerprint, $count)
}
finally { Pop-Location }
