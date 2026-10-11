# Scanner functions used by gates/scan.ps1 and exercised by scripts/tests. ASCII only.

Set-StrictMode -Version 3.0

# Third-party material vendored as-is; it has its own authors and is not scanned for this project's rules.
$script:ThirdPartyPrefixes = @('.claude/skills/', 'src/web/public/licenses/')

function Test-ThirdPartyPath([string] $File) {
    foreach ($prefix in $script:ThirdPartyPrefixes) {
        if ($File.StartsWith($prefix, [System.StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

function Test-BinaryFile([string] $File) {
    return $File -cmatch '\.(png|jpg|jpeg|gif|ico|woff2?|ttf|otf|pfx|dll|exe|zip|gz|pdf)$'
}

# A value is a placeholder when it is obviously not a credential: template markers, variable
# references, masks, or the words used by this repository's own examples.
function Test-PlaceholderValue([string] $Value) {
    if ($Value.Length -lt 8) { return $true }
    if ($Value -cmatch '^[<{\[$%*]') { return $true }
    if ($Value -cmatch '^\*+$') { return $true }
    if ($Value -cmatch '(?i)(placeholder|changeme|example|generated|your[-_]|redacted|\.\.\.|xxxx)') { return $true }
    return $false
}

function Test-CodeFile([string] $File) {
    return $File -cmatch '\.(cs|ts|tsx|js|mjs|cjs|ps1|psm1|sh)$'
}

# True when position $Index of $Line lies inside a quoted string literal: an odd number of unescaped
# quote characters of one kind stands before it. An apostrophe in a comment makes this answer "inside",
# which errs towards reporting.
function Test-InsideStringLiteral([string] $Line, [int] $Index) {
    $prefix = $Line.Substring(0, $Index)
    $double = [regex]::Matches($prefix, '(?<!\\)"').Count
    $single = [regex]::Matches($prefix, "(?<!\\)'").Count
    return (($double % 2) -eq 1) -or (($single % 2) -eq 1)
}

function Find-SecretFindings([string] $Root, [string[]] $Files) {
    $rules = @(
        @{ Name = 'connection-string-password'; Pattern = '(?i)(?:password|pwd)\s*=\s*([^;"''\s]+)'; Value = 1 },
        @{ Name = 'sa-password-assignment';     Pattern = '(?i)MSSQL_SA_PASSWORD\s*[=:]\s*["'']?([^"''\s]+)'; Value = 1 },
        @{ Name = 'private-key-block';          Pattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'; Value = 0 },
        @{ Name = 'aws-access-key';             Pattern = '\bAKIA[0-9A-Z]{16}\b'; Value = 0 },
        @{ Name = 'github-token';               Pattern = '\bgh[pousr]_[A-Za-z0-9]{36,}\b'; Value = 0 },
        @{ Name = 'api-key-style';              Pattern = '\bsk-[A-Za-z0-9_-]{24,}\b'; Value = 0 },
        @{ Name = 'bearer-literal';             Pattern = '(?i)authorization["'']?\s*[:=]\s*["'']?bearer\s+([A-Za-z0-9._~+/-]{20,})'; Value = 1 }
    )

    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($file in $Files) {
        if (-not $file -or (Test-BinaryFile $file) -or (Test-ThirdPartyPath $file)) { continue }
        if ($file -cmatch '(^|/)package-lock\.json$' -or $file -cmatch '(^|/)packages\.lock\.json$') { continue }   # integrity hashes, no credentials
        $path = Join-Path $Root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $path).Length -gt 2MB) { continue }

        $lineNumber = 0
        foreach ($line in [System.IO.File]::ReadLines($path)) {
            $lineNumber++
            foreach ($rule in $rules) {
                $match = [regex]::Match($line, $rule.Pattern)
                if (-not $match.Success) { continue }
                if ($rule.Value -gt 0) {
                    $value = $match.Groups[$rule.Value].Value
                    if (Test-PlaceholderValue $value) { continue }
                    # Code that reads or composes a password is not a stored password.
                    if ($value -cmatch '^[\$\{\(]' -or $value -cmatch '[\(\)\{\}\$]') { continue }
                    # In source code, a match outside a string literal is an assignment between
                    # identifiers, not a stored value. A connection string written into the code sits
                    # inside a literal and is still reported.
                    if ($rule.Name -ceq 'connection-string-password' -and (Test-CodeFile $file) -and -not (Test-InsideStringLiteral -Line $line -Index $match.Index)) { continue }
                }
                # Automated-test fixtures declare their throwaway password on a line that says so.
                if ($line -cmatch 'kantar:allow-test-secret') { continue }
                $findings.Add([pscustomobject]@{ File = $file; Line = $lineNumber; Rule = $rule.Name })
            }
        }
    }
    return $findings
}

# Authorship metadata must name TCoderr, and nothing may attribute the work to an assistant or tool.
# Mentions of products as subject matter (e.g. "OpenAI-compatible endpoint") are not attributions.
function Find-AuthorshipFindings([string] $Root, [string[]] $Files) {
    $rules = @(
        @{ Name = 'co-author-trailer';   Pattern = '(?i)co-authored-by\s*:' },
        @{ Name = 'generated-by-tool';   Pattern = '(?i)generated (?:with|by) \[?(?:claude|chatgpt|copilot|codex|gemini|an? (?:ai|llm))' },
        @{ Name = 'assistant-signature'; Pattern = '(?i)(?:written|authored|created|built) by (?:claude|chatgpt|copilot|codex|anthropic|openai)' },
        @{ Name = 'robot-emoji-credit';  Pattern = '🤖' }
    )

    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($file in $Files) {
        if (-not $file -or (Test-BinaryFile $file) -or (Test-ThirdPartyPath $file)) { continue }
        if ($file -ceq 'scripts/lib/scan-lib.ps1' -or $file -ceq 'scripts/tests/scan.tests.ps1') { continue }   # the rules and their test samples
        $path = Join-Path $Root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $path).Length -gt 2MB) { continue }

        $lineNumber = 0
        foreach ($line in [System.IO.File]::ReadLines($path)) {
            $lineNumber++
            foreach ($rule in $rules) {
                if ([regex]::IsMatch($line, $rule.Pattern)) {
                    $findings.Add([pscustomobject]@{ File = $file; Line = $lineNumber; Rule = $rule.Name })
                }
            }
            if ($file -cmatch '\.(props|csproj)$' -and $line -cmatch '<(Authors|Company)>([^<]*)<' -and $Matches[2] -cne 'TCoderr') {
                $findings.Add([pscustomobject]@{ File = $file; Line = $lineNumber; Rule = 'author-metadata-not-tcoderr' })
            }
            if ($file -cmatch '(^|/)package\.json$' -and $line -cmatch '"author"\s*:\s*"([^"]*)"' -and $Matches[1] -cne 'TCoderr') {
                $findings.Add([pscustomobject]@{ File = $file; Line = $lineNumber; Rule = 'author-metadata-not-tcoderr' })
            }
        }
    }
    return $findings
}

# License expression of every package in the lock files, read from the local package cache's nuspec.
function Get-NuGetLicenseInventory([string] $Root) {
    $packages = @{}
    foreach ($lock in Get-ChildItem -LiteralPath $Root -Recurse -Filter 'packages.lock.json' -File | Where-Object { $_.FullName -cnotmatch '\\(bin|obj|node_modules)\\' }) {
        $json = Get-Content -LiteralPath $lock.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $json.dependencies.PSObject.Properties) {
            foreach ($package in $framework.Value.PSObject.Properties) {
                if ($package.Value.type -ceq 'Project') { continue }
                $packages["$($package.Name)|$($package.Value.resolved)"] = $true
            }
        }
    }

    $cache = $env:NUGET_PACKAGES
    if (-not $cache) { $cache = Join-Path $env:USERPROFILE '.nuget\packages' }
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($key in $packages.Keys) {
        $id, $version = $key.Split('|')
        $license = 'UNKNOWN'
        $nuspec = Join-Path $cache (Join-Path $id.ToLowerInvariant() (Join-Path $version "$($id.ToLowerInvariant()).nuspec"))
        if (Test-Path -LiteralPath $nuspec) {
            $text = [System.IO.File]::ReadAllText($nuspec)
            $expression = [regex]::Match($text, '<license type="expression">([^<]+)</license>')
            $url = [regex]::Match($text, '<licenseUrl>([^<]+)</licenseUrl>')
            $file = [regex]::Match($text, '<license type="file">([^<]+)</license>')
            if ($expression.Success) { $license = $expression.Groups[1].Value }
            elseif ($file.Success) { $license = "file:$($file.Groups[1].Value)" }
            elseif ($url.Success) { $license = "url:$($url.Groups[1].Value)" }
        }
        $result.Add([pscustomobject]@{ Id = $id; Version = $version; License = $license })
    }
    return $result
}

# Licenses of the runtime (non-dev) npm packages as recorded in the lockfile.
function Get-NpmLicenseInventory([string] $WebRoot) {
    $result = New-Object System.Collections.Generic.List[object]
    $lockPath = Join-Path $WebRoot 'package-lock.json'
    if (-not (Test-Path -LiteralPath $lockPath)) { return $result }
    # The lockfile's root package has the empty string as its key, which Windows PowerShell's
    # ConvertFrom-Json rejects; it is renamed before parsing (it is skipped below anyway).
    $text = [System.IO.File]::ReadAllText($lockPath) -creplace '(?m)^(\s*)"":\s*\{', '$1"_root": {'
    $lock = $text | ConvertFrom-Json
    foreach ($entry in $lock.packages.PSObject.Properties) {
        if (-not $entry.Name -or $entry.Name -cnotmatch '^node_modules/') { continue }
        $isDev = $false
        if ($entry.Value.PSObject.Properties.Name -ccontains 'dev') { $isDev = [bool] $entry.Value.dev }
        if ($isDev) { continue }
        $license = 'UNKNOWN'
        if ($entry.Value.PSObject.Properties.Name -ccontains 'license') { $license = [string] $entry.Value.license }
        $result.Add([pscustomobject]@{ Name = ($entry.Name -creplace '^.*node_modules/', ''); Version = $entry.Value.version; License = $license })
    }
    return $result
}
