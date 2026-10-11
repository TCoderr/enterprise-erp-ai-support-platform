<#
.SYNOPSIS
  Runs the Kantar local stack (SQL Server 2022 Developer + API + worker) with Docker Compose.

.DESCRIPTION
  Commands
    init    Create private files under the local directory (passwords, connection strings, CSRF key,
            HTTPS certificate export, demo user passwords). Repeatable: existing files are kept.
    up      Build and start the stack and wait until it is healthy. Refuses to start when free
            memory is below the threshold or the port is taken.
    seed    Load demo data through the application's own commands. Repeatable.
    down    Stop and remove the containers. Volumes (the database) are KEPT.
    status  Show container state and the health endpoints.
    logs    Show recent container logs.
    reset   Destructive: remove containers AND this stack's volumes and private files. Requires
            -ConfirmReset <project> and only touches volumes labelled dev.kantar.stack=true.

  Nothing here prints a password. Private files live under the local directory, which is git-ignored.

.EXAMPLE
  ./scripts/local-stack.ps1 init
  ./scripts/local-stack.ps1 up
  ./scripts/local-stack.ps1 seed
  ./scripts/local-stack.ps1 status
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('init', 'up', 'seed', 'down', 'status', 'logs', 'reset')]
    [string] $Command,
    [string] $Project = 'kantar',
    [string] $LocalDir = '.local',
    [int] $Port = 8943,
    [int] $MinFreeMb = 1200,
    [int] $WaitForMemorySeconds = 0,
    [string] $ConfirmReset = '',
    [string] $AiProvider = 'Demo',
    [switch] $NoBuild,
    [switch] $RelaxRateLimits
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\common.ps1')

$root = Get-RepoRoot
if (-not (Test-ProjectName $Project)) { throw "Project name must match ^kantar(-[a-z0-9]+)?`$ ; got '$Project'. Nothing was changed." }
if ($LocalDir -cnotmatch '^\.local(-[a-z0-9]+)?$') { throw "Local directory must be .local or .local-<suffix> directly under the repository; got '$LocalDir'. Nothing was changed." }
$local = Join-Path $root $LocalDir
$docker = Find-Docker

$demoUsers = @('satis1', 'satis2', 'satis.mudur', 'satinalma1', 'satinalma.onay', 'depo1', 'depo.sorumlu', 'depo.izmir', 'destek1', 'destek.mudur', 'admin')

function Set-ComposeEnvironment {
    $env:KANTAR_LOCAL_DIR = $local
    $env:KANTAR_HTTPS_PORT = "$Port"
    $env:KANTAR_AI_PROVIDER = $AiProvider
    if ($RelaxRateLimits) {
        # For automated browser runs only: many logins from one address in a short time.
        $env:KANTAR_RATE_ANONYMOUS = '100000'
        $env:KANTAR_RATE_USER = '100000'
        $env:KANTAR_RATE_ASSIST = '100000'
    }
}

function Invoke-Compose([string[]] $Arguments) {
    Set-ComposeEnvironment
    $lines = Invoke-Native { & $docker compose -p $Project -f (Join-Path $root 'compose.yaml') --project-directory $root @Arguments }
    $exit = $LASTEXITCODE
    $lines | ForEach-Object { Write-Output (Protect-LogLine $_) }
    if ($exit -ne 0) { throw "docker compose $($Arguments -join ' ') failed with exit code $exit." }
}

function Assert-Initialised {
    if (-not (Test-OwnedLocalDir -Root $root -LocalDir $local -Project $Project)) {
        throw "No initialised local directory for project '$Project' at $LocalDir. Run: ./scripts/local-stack.ps1 init -Project $Project -LocalDir $LocalDir"
    }
}

switch ($Command) {
    'init' {
        if ((Test-Path -LiteralPath $local) -and -not (Test-OwnedLocalDir -Root $root -LocalDir $local -Project $Project)) {
            throw "$LocalDir exists but was not created for project '$Project'. It is left untouched."
        }
        $secrets = Join-Path $local 'secrets'
        foreach ($dir in @($local, $secrets, (Join-Path $local 'seed'), (Join-Path $local 'certs'))) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        [void] (Set-PrivateFile (Join-Path $local 'manifest.json') (@{ product = 'kantar'; project = $Project; createdUtc = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json))

        $saFile = Join-Path $secrets 'sa_password'
        [void] (Set-PrivateFile $saFile (New-Password))
        [void] (Set-PrivateFile (Join-Path $secrets 'migrator_password') (New-Password))
        [void] (Set-PrivateFile (Join-Path $secrets 'app_password') (New-Password))
        $sa = [System.IO.File]::ReadAllText($saFile)
        $migrator = [System.IO.File]::ReadAllText((Join-Path $secrets 'migrator_password'))
        $app = [System.IO.File]::ReadAllText((Join-Path $secrets 'app_password'))

        # The server certificate is SQL Server's own self-signed one on the private compose network;
        # traffic is encrypted, and the certificate is trusted explicitly for that reason only.
        $common = 'Server=sqlserver,1433;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15'
        [void] (Set-PrivateFile (Join-Path $local 'sqlserver.env') "MSSQL_SA_PASSWORD=$sa`n")
        [void] (Set-PrivateFile (Join-Path $secrets 'bootstrap_connection') "$common;Database=master;User Id=sa;Password=$sa;Application Name=kantar-bootstrap")
        [void] (Set-PrivateFile (Join-Path $secrets 'migrator_connection') "$common;Database=kantar;User Id=kantar_migrator;Password=$migrator;Application Name=kantar-migrator")
        [void] (Set-PrivateFile (Join-Path $secrets 'app_connection') "$common;Database=kantar;User Id=kantar_app;Password=$app;Application Name=kantar-app")
        [void] (Set-PrivateFile (Join-Path $secrets 'csrf_key') ([Convert]::ToBase64String((New-RandomBytes 32))))
        [void] (Set-PrivateFile (Join-Path $secrets 'https_certificate_password') (New-Secret -Bytes 24))

        foreach ($user in $demoUsers) {
            [void] (Set-PrivateFile (Join-Path $local "seed\Seed__Passwords__$user") (New-Password))
        }

        $pfx = Join-Path $local 'certs\kantar-dev.pfx'
        if (-not (Test-Path -LiteralPath $pfx)) {
            # Exports the existing .NET development certificate; the certificate store is not modified.
            $certificatePassword = [System.IO.File]::ReadAllText((Join-Path $secrets 'https_certificate_password'))
            $lines = Invoke-Native { & dotnet dev-certs https --export-path $pfx --password $certificatePassword --format Pfx }
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pfx)) {
                throw 'Could not export the .NET HTTPS development certificate. Create one with "dotnet dev-certs https" and run init again.'
            }
        }

        Write-Output "Initialised $LocalDir for project '$Project'."
        Write-Output "Demo passwords: $LocalDir\seed\Seed__Passwords__<user>  (one file per user; not printed)"
    }

    'up' {
        Assert-Initialised
        $memory = Wait-ForMemory -RequiredMb $MinFreeMb -WaitSeconds $WaitForMemorySeconds
        Write-Output ("Free memory: {0} MB (required: {1} MB)" -f $memory.FreeMb, $memory.RequiredMb)
        if (-not $memory.Ok) {
            throw "Not enough free memory to start the stack safely. Close applications or retry later; nothing was started and no other process was touched."
        }
        $running = @(Invoke-Native { & $docker ps --filter "label=com.docker.compose.project=$Project" --filter 'label=com.docker.compose.service=api' --format '{{.Names}}' })
        if ($running.Count -eq 0 -and -not (Test-PortFree $Port)) {
            throw "Port $Port is already in use by something else. Choose another with -Port. Nothing was started."
        }
        $arguments = @('up', '-d', '--wait')
        if (-not $NoBuild) { $arguments += '--build' }
        Invoke-Compose $arguments
        Write-Output "Kantar is running at https://localhost:$Port  (self-signed development certificate)"
    }

    'seed' {
        Assert-Initialised
        Invoke-Compose @('--profile', 'seed', 'run', '--rm', 'seed')
        Write-Output 'Seed finished.'
    }

    'down' {
        Assert-Initialised
        # No -v: the database volume is kept. Use "reset" to delete data.
        Invoke-Compose @('--profile', 'seed', 'down', '--remove-orphans')
        Write-Output 'Containers removed. Data volume kept.'
    }

    'status' {
        Assert-Initialised
        Invoke-Compose @('ps', '--all')
        foreach ($path in @('/health/live', '/health/ready')) {
            $lines = Invoke-Native { & curl.exe -k -s -o NUL -w '%{http_code}' --max-time 5 "https://localhost:$Port$path" }
            Write-Output ("{0} -> HTTP {1}" -f $path, ($lines -join ''))
        }
        Write-Output ("Free memory: {0} MB" -f (Get-FreeMemoryMb))
    }

    'logs' {
        Assert-Initialised
        Invoke-Compose @('logs', '--tail', '200', '--no-color')
    }

    'reset' {
        if ($ConfirmReset -cne $Project) {
            throw "Destructive: pass -ConfirmReset $Project to delete this stack's containers, database volume and private files. Nothing was deleted."
        }
        Assert-Initialised
        $volumes = @(Invoke-Native { & $docker volume ls --filter "label=com.docker.compose.project=$Project" --format '{{.Name}}' }) | Where-Object { $_ }
        foreach ($volume in $volumes) {
            # --format {{json .Labels}}: Windows PowerShell drops inner double quotes in native arguments,
            # so a template that needs quotes cannot be used here.
            $labels = (Invoke-Native { & $docker volume inspect $volume --format '{{json .Labels}}' }) -join '' | ConvertFrom-Json
            $owned = $null
            if ($labels) { $owned = $labels.'dev.kantar.stack' }
            if ($owned -cne 'true') {
                throw "Volume '$volume' does not carry the label dev.kantar.stack=true; it is not Kantar's. Nothing was deleted."
            }
        }
        Invoke-Compose @('--profile', 'seed', 'down', '-v', '--remove-orphans')
        Remove-Item -LiteralPath $local -Recurse -Force
        Write-Output "Stack '$Project' removed with its volume and $LocalDir."
    }
}
