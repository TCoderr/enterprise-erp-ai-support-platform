<#
.SYNOPSIS
  Deterministic tests for the verification scripts themselves (no Docker, no network, no database).
  Each test runs under both tr-TR and en-US, because script behaviour must not depend on the culture.

  Usage:  powershell -NoProfile -File scripts/tests/run.ps1
  Exit code 0 when every test passed, 1 otherwise.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')
. (Join-Path $PSScriptRoot '..\lib\verify-lib.ps1')
. (Join-Path $PSScriptRoot '..\lib\scan-lib.ps1')

$script:passed = 0
$script:failed = 0
$script:tests = New-Object System.Collections.Generic.List[object]

function Test([string] $Name, [scriptblock] $Body) { $script:tests.Add([pscustomobject]@{ Name = $Name; Body = $Body }) }

function Assert-True($Condition, [string] $Message) { if (-not $Condition) { throw "assertion failed: $Message" } }
function Assert-Equal($Expected, $Actual, [string] $Message) {
    if ($Expected -cne $Actual) { throw "assertion failed: $Message (expected '$Expected', actual '$Actual')" }
}

function New-TempDir {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("kantar-script-tests-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

# ---------------------------------------------------------------- gate names and thresholds

Test 'gate log names are culture independent (the Turkish I problem)' {
    Assert-Equal 'integration.log' (Get-GateLogName 'INTEGRATION') 'upper-case I'
    Assert-Equal 'ai-live.log' (Get-GateLogName 'AI Live') 'space becomes a dash'
    Assert-Equal 'migrations.log' (Get-GateLogName 'Migrations') 'mixed case'
    Assert-True ((Get-GateLogName 'INTEGRATION') -cnotmatch "[^\x00-\x7F]") 'no non-ASCII character such as dotless i'
}

Test 'memory threshold decision is exact at the boundary' {
    Assert-True (Test-MemorySufficient -FreeMb 1200 -RequiredMb 1200) 'equal is enough'
    Assert-True (-not (Test-MemorySufficient -FreeMb 1199 -RequiredMb 1200)) 'one below is not'
    Assert-True (Test-MemorySufficient -FreeMb 4000 -RequiredMb 1200) 'plenty'
}

Test 'Wait-ForMemory blocks without waiting when memory is low and passes when it is enough' {
    $env:KANTAR_FAKE_FREE_MB = '300'
    try {
        $low = Wait-ForMemory -RequiredMb 1200 -WaitSeconds 0
        Assert-True (-not $low.Ok) 'blocked'
        Assert-Equal 300 $low.FreeMb 'reports the measured value'
        $env:KANTAR_FAKE_FREE_MB = '1500'
        $high = Wait-ForMemory -RequiredMb 1200 -WaitSeconds 0
        Assert-True $high.Ok 'passes'
    }
    finally { Remove-Item Env:\KANTAR_FAKE_FREE_MB -ErrorAction SilentlyContinue }
}

Test 'gate threshold can be overridden per gate and ignores invalid values' {
    try {
        $env:KANTAR_VERIFY_MIN_FREE_MB_AI_LIVE = '2222'
        Assert-Equal 2222 (Get-GateThreshold -Gate 'ai-live' -Default 1500) 'override applies, dash maps to underscore'
        $env:KANTAR_VERIFY_MIN_FREE_MB_INTEGRATION = 'lots'
        Assert-Equal 1200 (Get-GateThreshold -Gate 'integration' -Default 1200) 'invalid override ignored'
        $env:KANTAR_VERIFY_MIN_FREE_MB_INTEGRATION = '-5'
        Assert-Equal 1200 (Get-GateThreshold -Gate 'integration' -Default 1200) 'negative override ignored'
    }
    finally {
        Remove-Item Env:\KANTAR_VERIFY_MIN_FREE_MB_AI_LIVE, Env:\KANTAR_VERIFY_MIN_FREE_MB_INTEGRATION -ErrorAction SilentlyContinue
    }
}

Test 'gate lists are accepted as an array and as one comma-separated string' {
    Assert-Equal 'unit|integration|web' ((Expand-GateList @('unit,integration', ' web ')) -join '|') 'mixed forms'
    Assert-Equal 'unit' ((Expand-GateList @('unit')) -join '|') 'single'
    Assert-Equal 0 @(Expand-GateList $null).Count 'none requested'
    Assert-Equal 0 @(Expand-GateList @(' , ')).Count 'only separators'
}

Test 'build worker reuse is switched off for the verification process tree only' {
    $before = @($env:MSBUILDDISABLENODEREUSE, $env:DOTNET_CLI_USE_MSBUILD_SERVER)
    try {
        Disable-BuildNodeReuse
        Assert-Equal '1' $env:MSBUILDDISABLENODEREUSE 'no idle MSBuild worker is kept'
        Assert-Equal '0' $env:DOTNET_CLI_USE_MSBUILD_SERVER 'no MSBuild server is started'
        Assert-True ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('MSBUILDDISABLENODEREUSE', 'User'))) 'the user environment is not changed'
    }
    finally {
        $env:MSBUILDDISABLENODEREUSE = $before[0]
        $env:DOTNET_CLI_USE_MSBUILD_SERVER = $before[1]
    }
}

# ---------------------------------------------------------------- checkpoints

Test 'checkpoints round-trip and are separated by source fingerprint' {
    $dir = New-TempDir
    try {
        $path = Join-Path $dir 'checkpoints.json'
        $store = Read-Checkpoints -Path $path
        Assert-Equal 0 $store.Count 'missing file is an empty store'
        Set-Checkpoint -Store $store -Fingerprint 'fp-a' -Gate 'unit' -Value @{ result = 'pass'; exit = 0; counts = 'total 3'; startedUtc = '2026-01-01T00:00:00Z'; seconds = 1.5; invocation = 'inv-1'; log = 'x' }
        Set-Checkpoint -Store $store -Fingerprint 'fp-a' -Gate 'integration' -Value @{ result = 'blocked-by-memory'; exit = 3; counts = 'blocked'; startedUtc = '2026-01-01T00:01:00Z'; seconds = 0; invocation = 'inv-1'; log = '' }
        Write-Checkpoints -Path $path -Store $store

        $loaded = Read-Checkpoints -Path $path
        $unit = Get-Checkpoint -Store $loaded -Fingerprint 'fp-a' -Gate 'unit'
        Assert-Equal 'pass' $unit.result 'result kept'
        Assert-Equal 'inv-1' $unit.invocation 'invocation kept'
        Assert-Equal 'blocked-by-memory' (Get-Checkpoint -Store $loaded -Fingerprint 'fp-a' -Gate 'integration').result 'blocked state kept'
        Assert-True ($null -eq (Get-Checkpoint -Store $loaded -Fingerprint 'fp-b' -Gate 'unit')) 'another source has no checkpoint: a pass is never carried over to changed source'
        Assert-True ($null -eq (Get-Checkpoint -Store $loaded -Fingerprint 'fp-a' -Gate 'web')) 'unknown gate'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'a corrupt checkpoint file is treated as empty, not as passed' {
    $dir = New-TempDir
    try {
        $path = Join-Path $dir 'checkpoints.json'
        [System.IO.File]::WriteAllText($path, '{ "fp-a": { "unit": { "result": "pass" ')
        Assert-Equal 0 (Read-Checkpoints -Path $path).Count 'corrupt store is empty'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# ---------------------------------------------------------------- test count parsing

Test 'test summary parsing requires real counts' {
    $ok = Get-TestCounts -Lines @('Test run summary: Passed!', '  total: 21', '  failed: 0', '  succeeded: 21', '  skipped: 0')
    Assert-Equal 21 $ok.Total 'total'
    Assert-Equal 0 $ok.Skipped 'skipped'
    $skipped = Get-TestCounts -Lines @('  total: 10', '  failed: 0', '  succeeded: 8', '  skipped: 2')
    Assert-Equal 2 $skipped.Skipped 'skips are visible so the gate can fail on them'
    $none = Get-TestCounts -Lines @('Build succeeded.', 'nothing else')
    Assert-Equal 0 $none.Total 'no summary means zero tests, which fails a test gate'
    Assert-Equal 'no test summary found' $none.Text 'explicit text'
}

Test 'gate note takes the last GATE-NOTE line' {
    Assert-Equal 'second' (Get-GateNote -Lines @('x', 'GATE-NOTE: first', 'y', 'GATE-NOTE: second')) 'last wins'
    Assert-Equal '' (Get-GateNote -Lines @('no note here')) 'none'
}

# ---------------------------------------------------------------- log masking

Test 'log masking removes credentials and keeps the rest' {
    # The samples are assembled at run time so this file holds no secret-shaped literal of its own.
    $masked = Protect-LogLine ('Server=db;User Id=app;Pass' + 'word=S3cr3t!Value;Encrypt=True')
    Assert-True ($masked -cnotmatch 'S3cr3t') 'password removed'
    Assert-True ($masked -cmatch 'Server=db') 'non-secret part kept'
    Assert-True ((Protect-LogLine ('MSSQL_SA_PASS' + 'WORD=Abc123!xyz')) -cnotmatch 'Abc123') 'sa password removed'
    Assert-True ((Protect-LogLine 'token: abcdef123456') -cnotmatch 'abcdef123456') 'token removed'
    Assert-Equal 'plain line' (Protect-LogLine 'plain line') 'unchanged'
}

# ---------------------------------------------------------------- ownership guards

Test 'project and local directory names outside the Kantar pattern are rejected' {
    Assert-True (Test-ProjectName 'kantar') 'dev'
    Assert-True (Test-ProjectName 'kantar-e2e') 'e2e'
    foreach ($name in @('kulvar', 'loca', 'lst', 'halka', 'KANTAR', 'kantar_e2e', 'kantar-', 'xkantar', '')) {
        Assert-True (-not (Test-ProjectName $name)) "must reject '$name'"
    }
}

Test 'a directory is owned only inside the repository, with the right name and a matching manifest' {
    $rootDir = New-TempDir
    try {
        $owned = Join-Path $rootDir '.local-e2e'
        New-Item -ItemType Directory -Force -Path $owned | Out-Null
        Assert-True (-not (Test-OwnedLocalDir -Root $rootDir -LocalDir $owned -Project 'kantar-e2e')) 'no manifest: not owned'
        [System.IO.File]::WriteAllText((Join-Path $owned 'manifest.json'), '{ "product": "kantar", "project": "kantar-e2e" }')
        Assert-True (Test-OwnedLocalDir -Root $rootDir -LocalDir $owned -Project 'kantar-e2e') 'owned'
        Assert-True (-not (Test-OwnedLocalDir -Root $rootDir -LocalDir $owned -Project 'kantar')) 'manifest of another project'

        $foreign = Join-Path $rootDir '.local'
        New-Item -ItemType Directory -Force -Path $foreign | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $foreign 'manifest.json'), '{ "product": "kulvar", "project": "kantar" }')
        Assert-True (-not (Test-OwnedLocalDir -Root $rootDir -LocalDir $foreign -Project 'kantar')) 'another product'

        $wrongName = Join-Path $rootDir 'data'
        New-Item -ItemType Directory -Force -Path $wrongName | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $wrongName 'manifest.json'), '{ "product": "kantar", "project": "kantar" }')
        Assert-True (-not (Test-OwnedLocalDir -Root $rootDir -LocalDir $wrongName -Project 'kantar')) 'name not .local*'

        $nested = Join-Path $rootDir 'sub\.local'
        New-Item -ItemType Directory -Force -Path $nested | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $nested 'manifest.json'), '{ "product": "kantar", "project": "kantar" }')
        Assert-True (-not (Test-OwnedLocalDir -Root $rootDir -LocalDir $nested -Project 'kantar')) 'not directly under the repository'
    }
    finally { Remove-Item -LiteralPath $rootDir -Recurse -Force }
}

Test 'private files are written once and never overwritten' {
    $dir = New-TempDir
    try {
        $file = Join-Path $dir 'secrets\value'
        Assert-True (Set-PrivateFile $file 'first') 'created'
        Assert-True (-not (Set-PrivateFile $file 'second')) 'second write refused'
        Assert-Equal 'first' ([System.IO.File]::ReadAllText($file)) 'content kept'
        $bytes = [System.IO.File]::ReadAllBytes($file)
        Assert-True ($bytes[0] -ne 0xEF) 'no byte order mark'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'generated passwords meet the policy and differ' {
    $first = New-Password
    $second = New-Password
    Assert-True ($first -cne $second) 'random'
    Assert-True ($first.Length -ge 20) 'long enough'
    Assert-True ($first -cmatch '[a-z]' -and $first -cmatch '[A-Z]' -and $first -cmatch '[0-9]' -and $first -cmatch '[^a-zA-Z0-9]') 'four character classes'
    Assert-True ($first -cnotmatch '[;"''\s=]') 'safe inside a connection string and an env file'
}

# ---------------------------------------------------------------- scanners

Test 'secret scanner flags real-looking secrets (positive controls)' {
    $dir = New-TempDir
    try {
        $value = 'Qw7' + 'rTy88' + 'ZxCvB'
        [System.IO.File]::WriteAllText((Join-Path $dir 'a.config'), "ConnectionStrings: Server=x;User Id=y;Pass" + "word=$value;")
        [System.IO.File]::WriteAllText((Join-Path $dir 'b.env'), ('MSSQL_SA_PASS' + "WORD=$value"))
        [System.IO.File]::WriteAllText((Join-Path $dir 'c.pem'), ('-----BEGIN PRIVATE' + ' KEY-----'))
        [System.IO.File]::WriteAllText((Join-Path $dir 'd.txt'), ('key AKIA' + 'ABCDEFGHIJKLMNOP'))
        $findings = @(Find-SecretFindings -Root $dir -Files @('a.config', 'b.env', 'c.pem', 'd.txt'))
        Assert-True ($findings.Count -ge 4) "expected at least 4 findings, got $($findings.Count)"
        Assert-True (@($findings | Where-Object { $_.Rule -ceq 'private-key-block' }).Count -eq 1) 'private key rule'
        Assert-True (@($findings | Where-Object { $_.PSObject.Properties.Name -ccontains 'Value' }).Count -eq 0) 'findings never carry the matched value'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'secret scanner does not flag placeholders, code or lock files (false-positive controls)' {
    $dir = New-TempDir
    try {
        [System.IO.File]::WriteAllText((Join-Path $dir '.env.example'), "MSSQL_SA_PASSWORD=<generated-by-local-stack-init>`nPassword=changeme-placeholder`n")
        [System.IO.File]::WriteAllText((Join-Path $dir 'code.ps1'), 'Password=$sa;Application Name=x' + "`n" + '$x = "Password=$($secret)"' + "`n")
        [System.IO.File]::WriteAllText((Join-Path $dir 'code.cs'), 'public const string Password = "Not-A-Connection-String";' + "`n" + 'var s = $"Password={password}";' + "`n")
        [System.IO.File]::WriteAllText((Join-Path $dir 'package-lock.json'), ('{ "integrity": "sha512-pass' + 'word=AAAABBBBCCCCDDDD" }'))
        $findings = @(Find-SecretFindings -Root $dir -Files @('.env.example', 'code.ps1', 'code.cs', 'package-lock.json'))
        Assert-Equal 0 $findings.Count 'no finding expected'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'secret scanner tells an assignment between identifiers from a connection string written into code' {
    $dir = New-TempDir
    try {
        $value = 'Qw7' + 'rTy88' + 'ZxCvB'
        # Assignments, object initialisers and anonymous objects: variables, not stored values.
        # "@@" stands for the keyword, put in at run time so this file holds no sample of its own.
        $word = 'Pass' + 'word'
        $assignments = @(
            'private string _sa@@ = string.Empty;',
            '_sa@@ = sa@@;',
            'DataSource = $"{_host},{_port}", UserID = user, @@ = secretValue,',
            'await client.PostAsync("/api/auth/login", new { userName = user.UserName, @@ = World.@@ });',
            'MustChange@@ = mustChange@@,'
        ) | ForEach-Object { $_.Replace('@@', $word) }
        [System.IO.File]::WriteAllText((Join-Path $dir 'Assignments.cs'), ($assignments -join "`n") + "`n")
        $fromAssignments = @(Find-SecretFindings -Root $dir -Files @('Assignments.cs'))
        Assert-Equal 0 $fromAssignments.Count 'identifier assignments are not findings'

        # The same shapes with a stored value inside a string literal are findings, in every code language.
        [System.IO.File]::WriteAllText((Join-Path $dir 'Literal.cs'), ('const string c = "Server=x;User ID=y;Pass' + "word=$value;Encrypt=True`";`n"))
        [System.IO.File]::WriteAllText((Join-Path $dir 'literal.ts'), ("const c = 'Server=x;User Id=y;Pass" + "word=$value';`n"))
        [System.IO.File]::WriteAllText((Join-Path $dir 'literal.ps1'), ('$c = "Server=x;User Id=y;Pass' + "word=$value;`"`n"))
        $literal = @(Find-SecretFindings -Root $dir -Files @('Literal.cs', 'literal.ts', 'literal.ps1'))
        Assert-Equal 3 $literal.Count 'a connection string inside a literal is a finding in each file'

        # Outside source code nothing changed: a bare assignment in a configuration file is a finding.
        [System.IO.File]::WriteAllText((Join-Path $dir 'app.config'), ('Pass' + "word = $value`n"))
        $fromConfiguration = @(Find-SecretFindings -Root $dir -Files @('app.config'))
        Assert-Equal 1 $fromConfiguration.Count 'configuration files keep the strict rule'

        # A fixture line that declares itself is skipped; the same line without the marker is not.
        [System.IO.File]::WriteAllText((Join-Path $dir 'Marked.cs'), ('var c = "Pass' + "word=$value;`";   // kantar:allow-test-secret`n" + 'var d = "Pass' + "word=$value;`";`n"))
        $marked = @(Find-SecretFindings -Root $dir -Files @('Marked.cs'))
        Assert-Equal 1 $marked.Count 'only the unmarked line is reported'
        Assert-Equal 2 $marked[0].Line 'and it is the second line'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'string-literal detection counts unescaped quotes before the position' {
    Assert-True (-not (Test-InsideStringLiteral -Line 'Name = value,' -Index 0)) 'no quote before'
    Assert-True (Test-InsideStringLiteral -Line 'var c = "Server=x;Name=abc";' -Index 18) 'inside double quotes'
    Assert-True (-not (Test-InsideStringLiteral -Line 'Call("a", new { name = p })' -Index 16)) 'after a closed literal'
    Assert-True (Test-InsideStringLiteral -Line "x = 'Name=abc'" -Index 5) 'inside single quotes'
    Assert-True (-not (Test-InsideStringLiteral -Line 'x = "a\"b"; Name = c' -Index 12)) 'an escaped quote does not open a literal'
    Assert-True (Test-CodeFile 'tests/A.cs') 'C#'
    Assert-True (Test-CodeFile 'scripts/a.ps1') 'PowerShell'
    Assert-True (-not (Test-CodeFile 'compose.yaml')) 'configuration is not code'
    Assert-True (-not (Test-CodeFile '.env.example')) 'env file is not code'
}

Test 'authorship scanner flags tool attribution and wrong author metadata, not subject-matter mentions' {
    $dir = New-TempDir
    try {
        [System.IO.File]::WriteAllText((Join-Path $dir 'bad.md'), ('Co-Authored' + '-By: Someone <a@b.c>' + "`n" + 'Generated' + ' with Claude Code' + "`n"))
        [System.IO.File]::WriteAllText((Join-Path $dir 'Directory.Build.props'), '<Project><PropertyGroup><Authors>Somebody Else</Authors></PropertyGroup></Project>')
        [System.IO.File]::WriteAllText((Join-Path $dir 'ok.md'), "The adapter talks to an OpenAI-compatible local endpoint such as Ollama.`nAuthor: TCoderr`n")
        [System.IO.File]::WriteAllText((Join-Path $dir 'ok.props'), '<Project><PropertyGroup><Authors>TCoderr</Authors><Company>TCoderr</Company></PropertyGroup></Project>')
        $bad = @(Find-AuthorshipFindings -Root $dir -Files @('bad.md', 'Directory.Build.props'))
        Assert-True ($bad.Count -ge 3) "expected at least 3 findings, got $($bad.Count)"
        $ok = @(Find-AuthorshipFindings -Root $dir -Files @('ok.md', 'ok.props'))
        Assert-Equal 0 $ok.Count 'no finding expected'
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Test 'third-party skill files are outside the authorship and secret rules' {
    Assert-True (Test-ThirdPartyPath '.claude/skills/impeccable/SKILL.md') 'vendored skill'
    Assert-True (-not (Test-ThirdPartyPath 'src/Kantar.Api/Program.cs')) 'own source'
}

# ---------------------------------------------------------------- run under both cultures

$original = [System.Threading.Thread]::CurrentThread.CurrentCulture
foreach ($cultureName in @('tr-TR', 'en-US')) {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo($cultureName)
    [System.Threading.Thread]::CurrentThread.CurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo($cultureName)
    foreach ($test in $script:tests) {
        try {
            & $test.Body
            $script:passed++
        }
        catch {
            $script:failed++
            Write-Output ("FAIL [{0}] {1}: {2}" -f $cultureName, $test.Name, $_.Exception.Message)
        }
    }
}
[System.Threading.Thread]::CurrentThread.CurrentCulture = $original

Write-Output ("script tests: total {0}, passed {1}, failed {2}, skipped 0 ({3} tests x 2 cultures)" -f ($script:passed + $script:failed), $script:passed, $script:failed, $script:tests.Count)
Write-Output ("GATE-NOTE: script tests: total {0}, passed {1}, failed {2}, skipped 0" -f ($script:passed + $script:failed), $script:passed, $script:failed)
if ($script:failed -gt 0) { exit 1 }
exit 0
