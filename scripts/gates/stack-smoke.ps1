<#
.SYNOPSIS
  Compose smoke gate on its OWN stack (project kantar-smoke, port 8953, directory .local-smoke).

  Checks, in order:
    empty install (bootstrap -> migrate -> api/worker healthy), HTTPS health endpoints,
    seed twice with identical stock afterwards, login over HTTPS, a real order command,
    container hardening (non-root, read-only root, no secrets in environment, loopback-only port),
    API restart keeps the session, database stop -> ready 503 / live 200 / JSON 503 -> start -> recovery,
    down + up keeps data, ERP works with the answer provider disabled,
    reset refuses a volume that is not Kantar's, reset removes only this stack.

  Refuses to run against the development stack (project kantar, .local).
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\common.ps1')

$root = Get-RepoRoot
$project = 'kantar-smoke'
$localDir = '.local-smoke'
$port = 8953
$base = "https://localhost:$port"
$stack = Join-Path $root 'scripts\local-stack.ps1'
$docker = Find-Docker
$local = Join-Path $root $localDir
$jar = Join-Path ([System.IO.Path]::GetTempPath()) ("kantar-smoke-" + [Guid]::NewGuid().ToString('N') + '.cookies')
$checks = New-Object System.Collections.Generic.List[string]
$script:failed = $false

if ($project -ceq 'kantar' -or $localDir -ceq '.local') { throw 'The smoke gate never runs against the development stack.' }

function Add-Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    $state = 'FAIL'
    if ($Ok) { $state = 'ok' } else { $script:failed = $true }
    $line = "{0,-4} {1} {2}" -f $state, $Name, $Detail
    $checks.Add($line)
    Write-Output $line
}

function Invoke-Stack([string] $Command, [hashtable] $Extra = @{}) {
    & $stack $Command -Project $project -LocalDir $localDir -Port $port @Extra | ForEach-Object { Write-Output $_ }
}

function Get-ServiceContainer([string] $Service) {
    return (Invoke-Native { & $docker ps -a --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$Service" --format '{{.Names}}' }) | Select-Object -First 1
}

# curl with a cookie jar; returns @{ Status; Body }. The body file is private to this run and deleted afterwards.
function Invoke-Http([string] $Method, [string] $Path, [string] $Body = $null, [string[]] $Headers = @()) {
    $out = [System.IO.Path]::GetTempFileName()
    $headerFile = $null
    try {
        $arguments = @('-k', '-s', '-o', $out, '-w', '%{http_code}', '--max-time', '20', '-X', $Method, '-b', $jar, '-c', $jar)
        if ($Headers.Count -gt 0) {
            # Headers travel in a file (curl -H @file): Windows PowerShell drops inner double quotes in
            # native arguments, which would turn If-Match: "<etag>" into an invalid precondition, and a
            # file also keeps the CSRF token off the process command line.
            $headerFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllLines($headerFile, $Headers, (New-Object System.Text.UTF8Encoding($false)))
            $arguments += @('-H', "@$headerFile")
        }
        if ($Body) {
            $bodyFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($bodyFile, $Body, (New-Object System.Text.UTF8Encoding($false)))
            $arguments += @('-H', 'Content-Type: application/json', '--data-binary', "@$bodyFile")
        }
        $arguments += "$base$Path"
        $status = (Invoke-Native { & curl.exe @arguments }) -join ''
        $text = [System.IO.File]::ReadAllText($out)
        $json = $null
        if ($text) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }
        return [pscustomobject]@{ Status = [int] $status; Body = $json; Text = $text }
    }
    finally {
        Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
        if ($headerFile) { Remove-Item -LiteralPath $headerFile -Force -ErrorAction SilentlyContinue }
        if ($Body) { Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue }
    }
}

function Wait-ForStatus([string] $Path, [int] $Expected, [int] $Seconds = 90) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $response = Invoke-Http GET $Path
        if ($response.Status -eq $Expected) { return $true }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Connect-As([string] $User) {
    if (Test-Path -LiteralPath $jar) { Remove-Item -LiteralPath $jar -Force }
    $csrf = Invoke-Http GET '/api/auth/csrf'
    $password = [System.IO.File]::ReadAllText((Join-Path $local "seed\Seed__Passwords__$User"))
    $login = Invoke-Http POST '/api/auth/login' (@{ userName = $User; password = $password } | ConvertTo-Json -Compress) @("X-CSRF-Token: $($csrf.Body.token)")
    $script:csrfToken = $null
    if ($login.Status -eq 200) { $script:csrfToken = $login.Body.csrfToken }
    return $login.Status
}

function Remove-OwnStack {
    if (Test-OwnedLocalDir -Root $root -LocalDir $local -Project $project) {
        try { & $stack reset -Project $project -LocalDir $localDir -Port $port -ConfirmReset $project | ForEach-Object { Write-Output $_ } }
        catch { Write-Output "warning: cleanup of $project failed: $($_.Exception.Message)" }
    }
}

$foreignVolume = "kantar-smoke_foreign-guard-probe"
try {
    Remove-OwnStack
    if (-not (Test-PortFree $port)) { Write-Output "GATE-NOTE: port $port is in use; the smoke stack was not started"; exit 1 }

    # ---- empty install ----
    Invoke-Stack 'init'
    Invoke-Stack 'up' @{ RelaxRateLimits = $true; WaitForMemorySeconds = 120 }
    Add-Check 'health/live is 200 over HTTPS' ((Invoke-Http GET '/health/live').Status -eq 200)
    Add-Check 'health/ready is 200 after migration' (Wait-ForStatus '/health/ready' 200 60)
    Add-Check 'unauthenticated API call is a JSON 401' ((Invoke-Http GET '/api/products').Status -eq 401)

    # ---- seed, twice ----
    Invoke-Stack 'seed'
    Add-Check 'login as warehouse supervisor' ((Connect-As 'depo.sorumlu') -eq 200)
    $warehouses = (Invoke-Http GET '/api/warehouses').Body
    $ist = ($warehouses | Where-Object { $_.code -ceq 'IST-01' }).id
    $before = (Invoke-Http GET "/api/inventory/balances?warehouseId=$ist&limit=100").Text
    $movementsBefore = ((Invoke-Http GET "/api/inventory/movements?warehouseId=$ist&limit=100").Body.items | Measure-Object).Count
    Invoke-Stack 'seed'
    $after = (Invoke-Http GET "/api/inventory/balances?warehouseId=$ist&limit=100").Text
    $movementsAfter = ((Invoke-Http GET "/api/inventory/movements?warehouseId=$ist&limit=100").Body.items | Measure-Object).Count
    Add-Check 'second seed changes no balance' ($before.Length -gt 50 -and $before -ceq $after)
    Add-Check 'second seed adds no journal row' ($movementsBefore -gt 0 -and $movementsBefore -eq $movementsAfter) "($movementsBefore rows)"

    # ---- a real command through HTTPS ----
    Add-Check 'login as sales agent' ((Connect-As 'satis1') -eq 200)
    $customers = @((Invoke-Http GET '/api/customers?active=true').Body)
    $product = ((Invoke-Http GET '/api/products?search=AMP-LED').Body.items | Select-Object -First 1)
    $orderBody = @{ customerId = $customers[0].id; warehouseId = $ist; lines = @(@{ productId = $product.id; quantity = 3 }) } | ConvertTo-Json -Compress -Depth 4
    $created = Invoke-Http POST '/api/sales/orders' $orderBody @("X-CSRF-Token: $csrfToken", "Idempotency-Key: smoke-order-$([Guid]::NewGuid().ToString('N'))")
    Add-Check 'sales order draft created' ($created.Status -eq 201) "(HTTP $($created.Status))"
    $orderId = $created.Body.id

    # ---- hardening ----
    $apiContainer = Get-ServiceContainer 'api'
    $inspect = ((Invoke-Native { & $docker inspect $apiContainer }) -join "`n" | ConvertFrom-Json)[0]
    Add-Check 'api container runs as non-root' ($inspect.Config.User -and $inspect.Config.User -cne '0' -and $inspect.Config.User -cne 'root') "(user $($inspect.Config.User))"
    Add-Check 'api container root filesystem is read-only' ($inspect.HostConfig.ReadonlyRootfs -eq $true)
    $envText = ($inspect.Config.Env -join "`n")
    Add-Check 'no password or connection string in the api environment' ($envText -cnotmatch '(?i)password=' -and $envText -cnotmatch '(?i)User Id=')
    $published = (Invoke-Native { & $docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Ports}}' }) -join ' '
    Add-Check 'only the API port is published, on 127.0.0.1' ($published -cmatch "127\.0\.0\.1:$port->" -and $published -cnotmatch '0\.0\.0\.0:' -and $published -cnotmatch ':1433->')

    # ---- API restart: server-side session survives ----
    Invoke-Native { & $docker restart $apiContainer } | Out-Null
    Add-Check 'api is healthy again after restart' (Wait-ForStatus '/health/ready' 200 90)
    Add-Check 'session survives an API restart' ((Invoke-Http GET '/api/auth/me').Status -eq 200)

    # ---- database down / up ----
    $sqlContainer = Get-ServiceContainer 'sqlserver'
    Invoke-Native { & $docker stop $sqlContainer } | Out-Null
    Add-Check 'ready turns 503 when the database is down' (Wait-ForStatus '/health/ready' 503 60)
    Add-Check 'live stays 200 when the database is down' ((Invoke-Http GET '/health/live').Status -eq 200)
    $down = Invoke-Http GET "/api/sales/orders/$orderId"
    Add-Check 'API answers JSON 5xx/401 (no hang, no HTML) while the database is down' (($down.Status -eq 503 -or $down.Status -eq 401) -and $down.Text -cnotmatch '<html')
    Invoke-Native { & $docker start $sqlContainer } | Out-Null
    Add-Check 'ready recovers after the database is back' (Wait-ForStatus '/health/ready' 200 120)
    Add-Check 'login works again after recovery' ((Connect-As 'satis1') -eq 200)
    Add-Check 'order written before the outage is still readable' ((Invoke-Http GET "/api/sales/orders/$orderId").Status -eq 200)

    # ---- down + up keeps data; answer provider disabled ----
    Invoke-Stack 'down'
    Invoke-Stack 'up' @{ NoBuild = $true; RelaxRateLimits = $true; AiProvider = 'Disabled'; WaitForMemorySeconds = 120 }
    Add-Check 'login after down/up' ((Connect-As 'satis1') -eq 200)
    Add-Check 'data persisted across down/up' ((Invoke-Http GET "/api/sales/orders/$orderId").Status -eq 200)
    $assistStatus = Invoke-Http GET '/api/assist/status'
    Add-Check 'assist reports the provider as disabled' ($assistStatus.Body.kind -ceq 'disabled')
    $ask = Invoke-Http POST '/api/assist/ask' (@{ question = 'STK-409 ne anlama geliyor?' } | ConvertTo-Json -Compress) @("X-CSRF-Token: $csrfToken")
    Add-Check 'ask returns ProviderUnavailable, not an error' ($ask.Status -eq 200 -and $ask.Body.status -ceq 'ProviderUnavailable')
    $search = Invoke-Http POST '/api/assist/search' (@{ query = 'STK-409 stok yetersiz' } | ConvertTo-Json -Compress) @("X-CSRF-Token: $csrfToken")
    Add-Check 'source search still works without a provider' ($search.Status -eq 200 -and ($search.Body | Measure-Object).Count -gt 0)
    $fresh = Invoke-Http GET "/api/sales/orders/$orderId"
    $etag = $fresh.Body.eTag
    $confirm = Invoke-Http POST "/api/sales/orders/$orderId/confirm" $null @("X-CSRF-Token: $csrfToken", "Idempotency-Key: smoke-confirm-$([Guid]::NewGuid().ToString('N'))", "If-Match: $etag")
    Add-Check 'ERP command (confirm with reservation) works while the provider is disabled' ($confirm.Status -eq 200 -and $confirm.Body.status -ceq 'Confirmed') "(HTTP $($confirm.Status))"

    # ---- reset guard: a volume in the project that is not Kantar's must stop reset ----
    Invoke-Native { & $docker volume create --label "com.docker.compose.project=$project" $foreignVolume } | Out-Null
    $refused = $false
    try { & $stack reset -Project $project -LocalDir $localDir -Port $port -ConfirmReset $project | Out-Null } catch { $refused = $true }
    Add-Check 'reset refuses when a volume lacks the Kantar label' $refused
    Add-Check 'refused reset deleted nothing' ((Invoke-Http GET '/health/live').Status -eq 200 -and (Test-Path -LiteralPath $local))
    $wrongConfirm = $false
    try { & $stack reset -Project $project -LocalDir $localDir -Port $port -ConfirmReset 'kantar' | Out-Null } catch { $wrongConfirm = $true }
    Add-Check 'reset refuses a wrong confirmation value' $wrongConfirm
}
catch {
    Add-Check 'smoke run completed without an unexpected error' $false "($($_.Exception.Message))"
}
finally {
    # The probe volume was created by this script a few lines above and is removed by name.
    Invoke-Native { & $docker volume rm $foreignVolume } | Out-Null
    Remove-OwnStack
    Remove-Item -LiteralPath $jar -Force -ErrorAction SilentlyContinue
    $leftContainers = @(Invoke-Native { & $docker ps -a --filter "label=com.docker.compose.project=$project" --format '{{.Names}}' } | Where-Object { $_ })
    $leftVolumes = @(Invoke-Native { & $docker volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}' } | Where-Object { $_ })
    Add-Check 'own stack fully removed afterwards' ($leftContainers.Count -eq 0 -and $leftVolumes.Count -eq 0 -and -not (Test-Path -LiteralPath $local))
}

$okCount = @($checks | Where-Object { $_ -cmatch '^ok' }).Count
Write-Output "GATE-NOTE: smoke checks ok $okCount of $($checks.Count)"
if ($script:failed) { exit 1 }
exit 0
