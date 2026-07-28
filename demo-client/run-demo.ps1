#Requires -Version 5.1
<#
    The whole demo topology on one machine, in the order it has to come up:

        ShopConnector.Api   http://localhost:8420
        BankConnector.Api   http://localhost:8410
        ShopConnector.Agent (browser, so the browser-tier providers are testable)
        DemoClient.Web      http://localhost:8430   <- open this

    Each connector's /v1/health must answer before the next process starts.
    Starting them all at once and hoping is how a startup failure surfaces
    three services later as a connection error against the wrong thing.

    Ctrl-C stops everything this script started, youngest first.

        .\run-demo.ps1                  everything
        .\run-demo.ps1 --no-agent       the http-tier providers and the mocks
        .\run-demo.ps1 --bank-agent     also run the bank agent (mock SCA/slow)
        .\run-demo.ps1 --no-build       skip the build, start what is in bin/
#>
[CmdletBinding()]
param(
    # Skips the shopping agent. The BROWSER-tier providers then answer
    # agent_unavailable, which is the correct error and exactly what the UI
    # must render: Albert Heijn, Amazon.nl, bol.com, Coolblue and Jumbo.
    #
    # "Every real provider" is no longer true and has not been for a while.
    # Lidl Plus, Picnic and the two guest-order providers declare runtime
    # `http` and `agent.class: inline`, so they run in the control plane and
    # are completely unaffected by this switch - which makes --no-agent a
    # perfectly good way to demo real providers, not just the fixtures.
    [switch] $NoAgent,

    # The bank fleet's SCA and slow mocks are browser-tier, so they need an
    # agent too - but not browser binaries, because they are mocks.
    [switch] $BankAgent,

    [switch] $NoBuild,

    # Show the browser the shopping agent drives. For working out where a
    # relayed tap actually landed, which no log line can tell you as directly
    # as watching the click happen.
    [switch] $Headed,

    # The one human this demo has. Everything identity-shaped is derived from
    # it, so the agent and the relay cannot end up disagreeing about who is
    # connected - see $ShopSalt below.
    [string] $UserId,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Rest
)

$ErrorActionPreference = 'Stop'

$Tfm = 'net10.0'
$Configuration = 'Debug'

# The ports the connectors' own launch profiles use, so `dotnet run` in a
# project directory and this script are the same system. Overridable only to
# get out of a collision - everything else, including the README, says 8430.
$ShopPort = if ($env:DEMO_SHOP_PORT) { [int]$env:DEMO_SHOP_PORT } else { 8420 }
$BankPort = if ($env:DEMO_BANK_PORT) { [int]$env:DEMO_BANK_PORT } else { 8410 }
$DemoPort = if ($env:DEMO_PORT) { [int]$env:DEMO_PORT } else { 8430 }

<#
    The relay mints one pseudonymous subject PER SERVICE:

        subject = "u_" + base64url(HMACSHA256(key: salt, msg: userId))[0..21]

    This script has to mint the same value, because an agent's enrollment code
    carries the subject and the agents screen lists agents BY SUBJECT - enroll
    under anything else and the UI shows an empty list beside a running agent.
    So the salts are passed to the relay rather than read from it: one source
    of truth, and no parsing of a settings file with comments in it.

    The two salts must differ. Identical ones would hand both connectors the
    same pseudonym for the same person, and the relay refuses to start.
#>
$ShopSalt = 'dev-subject-salt-shop-4c1f8a'
$BankSalt = 'dev-subject-salt-bank-9e07b3'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DemoRoot = $PSScriptRoot

$script:Running = @()

function Show-Usage {
    Write-Host @'
usage: run-demo.ps1 [--no-agent] [--bank-agent] [--no-build] [-UserId <id>]

  --no-agent     do not start ShopConnector.Agent. The browser-tier providers
                 report agent_unavailable without it: Albert Heijn, Amazon.nl,
                 bol.com, Coolblue and Jumbo. The http-tier ones need no agent
                 at all and still work: Lidl Plus, Picnic, the guest-order
                 providers, and every mock-* except mock-store-persistent.
  --bank-agent   also start BankConnector.Agent, which is what mock-bank-sca
                 and mock-bank-slow need. No browser binaries required.
  --no-build     start whatever is already in bin/ instead of building first.
  --headed       show the browser the shopping agent drives.
'@
}

# $UserId is positional, so PowerShell binds the FIRST bare argument to it -
# and a GNU-style flag is a bare argument. `run-demo.ps1 --no-build` therefore
# never reached the loop below: it became the user id, and since every subject
# is HMAC(salt, userId), the relay logged in as a different person than the one
# the agent enrolled under. The only symptom was every browser-tier provider
# reporting agent_unavailable forever, which points at the agent rather than at
# the argument. Hand anything flag-shaped back to the parser instead.
if ($UserId -and $UserId.StartsWith('-')) {
    # $Rest is $null when there were no further arguments, and @($null) is an
    # array holding one empty element, not an empty array.
    $Rest = @($UserId) + @($Rest | Where-Object { $_ })
    $UserId = $null
}

# GNU-style flags as well as PowerShell switches: the two scripts are meant to
# be typed interchangeably, and a demo that rejects the flag printed in its own
# README is a bad first impression.
foreach ($arg in $Rest) {
    if (-not $arg) { continue }
    if ($arg -eq '--no-agent') { $NoAgent = $true }
    elseif ($arg -eq '--bank-agent') { $BankAgent = $true }
    elseif ($arg -eq '--no-build') { $NoBuild = $true }
    elseif ($arg -eq '--headed') { $Headed = $true }
    elseif ($arg -eq '-h' -or $arg -eq '--help') { Show-Usage; exit 0 }
    else { Write-Host "unknown argument '$arg'"; Show-Usage; exit 2 }
}

if (-not $UserId) { $UserId = $env:DEMO_USER_ID }
if (-not $UserId) { $UserId = 'demo-user' }

function Get-Subject {
    param([string] $Salt)

    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    try {
        $hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($Salt)
        $digest = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($UserId))
    }
    finally {
        $hmac.Dispose()
    }

    # base64url, unpadded, truncated to 21 characters - the relay's
    # SubjectMinter, character for character.
    $encoded = [Convert]::ToBase64String($digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    return 'u_' + $encoded.Substring(0, 21)
}

$ShopSubject = Get-Subject $ShopSalt
$BankSubject = Get-Subject $BankSalt

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn { param([string] $Message) Write-Host "!!  $Message" -ForegroundColor Yellow }
function Write-Fail { param([string] $Message) Write-Host "!!  $Message" -ForegroundColor Red }

function Resolve-Project {
    param([string] $Path)

    $csproj = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $RepoRoot $Path }
    if (-not (Test-Path $csproj)) { return $null }

    $dir = Split-Path -Parent $csproj
    $name = [System.IO.Path]::GetFileNameWithoutExtension($csproj)

    return [pscustomobject]@{
        Csproj = $csproj
        Dir    = $dir
        Dll    = Join-Path $dir "bin\$Configuration\$Tfm\$name.dll"
        Name   = $name
    }
}

function Invoke-Build {
    param([object] $Project)

    if ($NoBuild) { return }

    Write-Step "building $($Project.Name)"
    & dotnet build $Project.Csproj -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "build failed for $($Project.Name)" }
}

<#
    Starts the built assembly rather than `dotnet run`.

    `dotnet run` forks the app as a child of the build host, so the pid this
    script holds is not the pid it has to stop - and a demo that leaves a
    connector holding port 8420 after Ctrl-C is worse than one that never
    started. The working directory is the project directory so relative paths
    in appsettings (the Sqlite dev database, the agent's artifacts/) land
    exactly where a plain `dotnet run` in that directory would put them.
#>
function Start-Child {
    param(
        [string] $Name,
        [object] $Project,
        [string[]] $Arguments = @(),
        [hashtable] $Environment
    )

    if (-not (Test-Path $Project.Dll)) {
        throw "$($Project.Name) is not built: $($Project.Dll) does not exist. Drop --no-build."
    }

    $applied = @()
    if ($Environment) {
        foreach ($key in $Environment.Keys) {
            Set-Item -Path "env:$key" -Value $Environment[$key]
            $applied += $key
        }
    }

    try {
        $quoted = @('"' + $Project.Dll + '"') + $Arguments
        $process = Start-Process -FilePath 'dotnet' -ArgumentList ($quoted -join ' ') `
            -WorkingDirectory $Project.Dir -NoNewWindow -PassThru
    }
    finally {
        # The child has inherited them; leaving them set would leak into the
        # next child and into the caller's shell.
        foreach ($key in $applied) { Remove-Item -Path "env:$key" -ErrorAction SilentlyContinue }
    }

    $script:Running += [pscustomobject]@{ Name = $Name; Process = $process }
    return $process
}

<#
    A port already in use is the failure this script hits most: a previous run
    that was killed rather than stopped, or a `dotnet run` in another terminal.
    Caught here it names the port; caught by the build it is ten retries of an
    MSBuild file-lock warning that names nothing useful.
#>
function Assert-PortFree {
    param([string] $What, [int] $Port)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($connect.AsyncWaitHandle.WaitOne(400) -and $client.Connected) {
            throw "port $Port is already in use, so $What cannot start. Stop whatever is on it, or set DEMO_SHOP_PORT / DEMO_BANK_PORT / DEMO_PORT."
        }
    }
    finally {
        $client.Close()
    }
}

function Test-Http {
    param([string] $Url, [switch] $AnyStatus)

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -Method Get
        return $response.StatusCode -lt 500
    }
    catch [System.Net.WebException] {
        # An HTTP error response still proves something is listening, which is
        # all a readiness probe on a relay can honestly assert.
        if ($AnyStatus -and $_.Exception.Response) { return $true }
        return $false
    }
    catch {
        if ($AnyStatus -and $_.Exception.Response) { return $true }
        return $false
    }
}

function Wait-Http {
    param(
        [string] $What,
        [string] $Url,
        [System.Diagnostics.Process] $Process,
        [int] $TimeoutSeconds = 90,
        [switch] $AnyStatus
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) {
            throw "$What exited with code $($Process.ExitCode) before it answered $Url"
        }

        if (Test-Http -Url $Url -AnyStatus:$AnyStatus) { return }
        Start-Sleep -Milliseconds 400
    }

    throw "$What did not answer $Url within $TimeoutSeconds seconds"
}

<#
    An enrollment code is one-time, HMAC-signed and minted by the running
    control plane, so it cannot be pre-configured. Minting one on every start
    is safe: an agent that already has state ignores it.
#>
function New-EnrollmentCode {
    param([string] $BaseUrl, [string] $AgentName, [string] $Subject)

    $body = @{ subject = $Subject; name = $AgentName } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$BaseUrl/v1/agents/enrollment" -Method Post `
        -ContentType 'application/json' -Body $body -TimeoutSec 15
    return $response.code
}

function Wait-Agent {
    param([string] $BaseUrl, [string] $Subject, [int] $TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $agents = (Invoke-RestMethod -Uri "$BaseUrl/v1/agents?subject=$Subject" -TimeoutSec 5).agents
            $online = @($agents | Where-Object { $_.online })
            if ($online.Count -gt 0) { return $online[0].id }
        }
        catch {
            # The control plane is up - it answered health - so a failure here
            # is the agent not having enrolled yet.
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Get-PlaywrightRoot {
    if ($env:PLAYWRIGHT_BROWSERS_PATH) { return $env:PLAYWRIGHT_BROWSERS_PATH }
    if ($env:USERPROFILE) { return (Join-Path $env:USERPROFILE 'AppData\Local\ms-playwright') }
    if (Test-Path (Join-Path $HOME 'Library/Caches')) { return (Join-Path $HOME 'Library/Caches/ms-playwright') }
    return (Join-Path $HOME '.cache/ms-playwright')
}

<#
    Detected, never installed unasked: `playwright install` downloads a few
    hundred megabytes from Microsoft's CDN, and a script that does that
    silently because someone typed run-demo is a script nobody trusts twice.
#>
function Test-PlaywrightBrowsers {
    param([object] $AgentProject)

    $root = Get-PlaywrightRoot
    $found = (Test-Path $root) -and
             @(Get-ChildItem -Path $root -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue).Count -gt 0

    if ($found) {
        Write-Host "    Playwright chromium: found in $root"
        return $true
    }

    $installer = Join-Path $AgentProject.Dir "bin\$Configuration\$Tfm\playwright.ps1"
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell -ExecutionPolicy Bypass -File' }

    Write-Warn "Playwright browsers are not installed ($root has no chromium-*)."
    Write-Host  '    The browser-tier providers (AH, Amazon.nl, bol.com, Coolblue, Jumbo) will fail at browser launch until they are. Install them with:'
    Write-Host  ''
    Write-Host  "        $shell `"$installer`" install chromium" -ForegroundColor Green
    Write-Host  ''
    return $false
}

function Stop-All {
    if ($script:Running.Count -eq 0) { return }

    Write-Host ''
    Write-Step 'stopping'

    # Ctrl-C already reached every child through the shared console, so this is
    # mostly a wait. The deadline is shared rather than per-process: three
    # sequential grace periods would make Ctrl-C feel broken.
    $deadline = (Get-Date).AddSeconds(8)

    for ($i = $script:Running.Count - 1; $i -ge 0; $i--) {
        $entry = $script:Running[$i]
        $process = $entry.Process
        if ($null -eq $process) { continue }

        try {
            while (-not $process.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
        catch {
            # Already gone.
        }

        Write-Host "    stopped $($entry.Name)"
    }

    $script:Running = @()
}

# ---------------------------------------------------------------------------

$shopApi = Resolve-Project 'shop-connector\src\ShopConnector.Api\ShopConnector.Api.csproj'
$bankApi = Resolve-Project 'bank-connector\src\BankConnector.Api\BankConnector.Api.csproj'
$shopAgent = Resolve-Project 'shop-connector\src\ShopConnector.Agent\ShopConnector.Agent.csproj'
$bankAgentProject = Resolve-Project 'bank-connector\src\BankConnector.Agent\BankConnector.Agent.csproj'

if (-not $shopApi -or -not $bankApi -or -not $shopAgent) {
    Write-Fail "this script must live in <repo>/demo-client - the connectors are not where it expects ($RepoRoot)"
    exit 1
}

# The demo client is a separate deliverable and may not exist yet. Its absence
# is reported and the connectors still come up, because they are useful on
# their own with curl.
$demo = $null
$demoCsproj = Get-ChildItem -Path $DemoRoot -Recurse -Filter 'DemoClient.Web.csproj' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($demoCsproj) {
    $demo = Resolve-Project $demoCsproj.FullName
}

$shopUrl = "http://localhost:$ShopPort"
$bankUrl = "http://localhost:$BankPort"
$demoUrl = "http://localhost:$DemoPort"
$credentials = Join-Path $DemoRoot 'credentials.local.jsonc'

try {
    Assert-PortFree 'ShopConnector.Api' $ShopPort
    Assert-PortFree 'BankConnector.Api' $BankPort
    if ($demo) { Assert-PortFree 'DemoClient.Web' $DemoPort }

    Invoke-Build $shopApi
    Invoke-Build $bankApi
    if (-not $NoAgent) { Invoke-Build $shopAgent }
    if ($BankAgent -and $bankAgentProject) { Invoke-Build $bankAgentProject }
    if ($demo) { Invoke-Build $demo }

    Write-Step "starting ShopConnector.Api on $shopUrl"
    $shopProcess = Start-Child -Name 'ShopConnector.Api' -Project $shopApi `
        -Arguments @('--urls', $shopUrl) `
        -Environment @{ ASPNETCORE_ENVIRONMENT = 'Development' }
    Wait-Http -What 'ShopConnector.Api' -Url "$shopUrl/v1/health" -Process $shopProcess

    Write-Step "starting BankConnector.Api on $bankUrl"
    $bankProcess = Start-Child -Name 'BankConnector.Api' -Project $bankApi `
        -Arguments @('--urls', $bankUrl) `
        -Environment @{ ASPNETCORE_ENVIRONMENT = 'Development' }
    Wait-Http -What 'BankConnector.Api' -Url "$bankUrl/v1/health" -Process $bankProcess

    $shopAgentId = $null
    if ($NoAgent) {
        # Said once, up front, rather than only in the summary: someone typing
        # --no-agent out of habit deserves to be told which half of the
        # catalogue they just switched off, before they pick one from it.
        Write-Warn '--no-agent: Albert Heijn, Amazon.nl, bol.com, Coolblue and Jumbo are browser-tier and will report agent_unavailable.'
        Write-Host '    Lidl Plus, Picnic and the guest-order providers are http tier - they run in the control plane and are unaffected.'
        Write-Host '    So are the mock-* providers, except mock-store-persistent, which wants a BYO agent this script never starts.'
    }
    else {
        Write-Step 'starting ShopConnector.Agent'
        [void](Test-PlaywrightBrowsers -AgentProject $shopAgent)

        $code = New-EnrollmentCode -BaseUrl $shopUrl -AgentName 'local-shop-agent' -Subject $ShopSubject

        # The control plane is stated rather than left to the agent's own
        # settings: those name a fixed port, and an agent long-polling a
        # different instance than the one the UI talks to looks exactly like
        # an agent that never picks up work.
        # --headed shows the browser the agent is driving. The relay is
        # unaffected: a widget whose grid can be named is photographed and
        # relayed whether or not anyone is watching, and Attended only decides
        # the FALLBACK when nothing could be relayed. So this is a window onto
        # the same run, not a different one - which is what makes it usable for
        # working out where a replayed tap actually landed.
        $agentArgs = @("--ConnectorAgent:ControlPlaneBaseUrl=$shopUrl/", "--ConnectorAgent:EnrollmentCode=$code")
        if ($Headed) { $agentArgs += '--ConnectorAgent:Headless=false' }

        $agentProcess = Start-Child -Name 'ShopConnector.Agent' -Project $shopAgent `
            -Arguments $agentArgs `
            -Environment @{ DOTNET_ENVIRONMENT = 'Development' }

        $shopAgentId = Wait-Agent -BaseUrl $shopUrl -Subject $ShopSubject
        if (-not $shopAgentId) {
            if ($agentProcess.HasExited) {
                Write-Warn "the shopping agent exited with code $($agentProcess.ExitCode). If its stored token was rejected it has cleared its state - run this again."
            }
            else {
                Write-Warn 'the shopping agent has not enrolled yet; the browser-tier providers will report agent_unavailable until it does'
            }
        }
    }

    $bankAgentId = $null
    if ($BankAgent) {
        if (-not $bankAgentProject) {
            Write-Warn 'BankConnector.Agent is not in this repo; skipping --bank-agent'
        }
        else {
            Write-Step 'starting BankConnector.Agent'
            $code = New-EnrollmentCode -BaseUrl $bankUrl -AgentName 'local-bank-agent' -Subject $BankSubject
            [void](Start-Child -Name 'BankConnector.Agent' -Project $bankAgentProject `
                -Arguments @("--ConnectorAgent:ControlPlaneBaseUrl=$bankUrl/", "--ConnectorAgent:EnrollmentCode=$code") `
                -Environment @{ DOTNET_ENVIRONMENT = 'Development' })

            $bankAgentId = Wait-Agent -BaseUrl $bankUrl -Subject $BankSubject
            if (-not $bankAgentId) { Write-Warn 'the bank agent has not enrolled yet' }
        }
    }

    $demoProcess = $null
    if ($demo) {
        Write-Step "starting DemoClient.Web on $demoUrl"
        $demoProcess = Start-Child -Name 'DemoClient.Web' -Project $demo `
            -Arguments @('--urls', $demoUrl) `
            -Environment @{
                ASPNETCORE_ENVIRONMENT                = 'Development'
                # The ports and the identity, stated rather than inherited
                # from appsettings: this script already minted the agent's
                # subject from these salts, and the two must not drift.
                # CredentialsFile is deliberately not set - the relay resolves
                # it from its content root upwards, which finds the file
                # whether it sits beside the project or beside this script.
                Relay__Connectors__shop__BaseUrl      = $shopUrl
                Relay__Connectors__shop__SubjectSalt  = $ShopSalt
                Relay__Connectors__bank__BaseUrl      = $bankUrl
                Relay__Connectors__bank__SubjectSalt  = $BankSalt
                Demo__UserId                          = $UserId
            }

        Wait-Http -What 'DemoClient.Web' -Url "$demoUrl/api/services" -Process $demoProcess -AnyStatus -TimeoutSeconds 60
    }
    else {
        Write-Warn "DemoClient.Web was not found under $DemoRoot - the connectors are running, the UI is not"
    }

    Write-Host ''
    Write-Host '  running' -ForegroundColor Green
    Write-Host '  -------'
    Write-Host ("  {0,-20} {1,-32} pid {2}" -f 'Shopping connector', "$shopUrl/v1/providers", $shopProcess.Id)
    Write-Host ("  {0,-20} {1,-32} pid {2}" -f 'Bank connector', "$bankUrl/v1/providers", $bankProcess.Id)

    if ($NoAgent) {
        Write-Host ("  {0,-20} {1}" -f 'Shopping agent', 'not started (--no-agent): browser tier reports agent_unavailable; http tier and mock-* still work')
    }
    elseif ($shopAgentId) {
        Write-Host ("  {0,-20} {1}" -f 'Shopping agent', $shopAgentId)
    }

    if ($bankAgentId) {
        Write-Host ("  {0,-20} {1}" -f 'Bank agent', $bankAgentId)
    }

    if ($demoProcess) {
        Write-Host ("  {0,-20} {1,-32} pid {2}" -f 'Demo client', $demoUrl, $demoProcess.Id)

        # One honest line about what the relay can actually reach, straight
        # from the endpoint the SPA uses.
        try {
            $services = Invoke-RestMethod -Uri "$demoUrl/api/services" -TimeoutSec 5
            foreach ($service in $services) {
                $state = if ($service.reachable) { 'reachable' } else { "unreachable: $($service.error)" }
                Write-Host ("      {0,-16} {1}" -f $service.key, $state)
            }
        }
        catch {
            Write-Warn "GET $demoUrl/api/services did not answer as expected: $($_.Exception.Message)"
        }

        Write-Host ''
        Write-Host "  Open $demoUrl" -ForegroundColor Green
    }

    # One human, two pseudonyms, and no way to line them up. Printing them is
    # the cheapest demonstration that the two connectors cannot correlate the
    # same person - and it is how you check the agent enrolled under the
    # subject the agents screen asks for.
    Write-Host ''
    Write-Host ("  user '{0}' is {1} to the shopping connector and {2} to the bank" -f $UserId, $ShopSubject, $BankSubject)

    # The relay looks from its content root upwards, so a file beside the
    # project counts too.
    $hasCredentials = (Test-Path $credentials) -or
        ($demo -and (Test-Path (Join-Path $demo.Dir 'credentials.local.jsonc')))

    if (-not $hasCredentials) {
        Write-Host ''
        Write-Host "  No $credentials - the real providers' forms come up empty."
        Write-Host '  Copy credentials.example.jsonc to credentials.local.jsonc and fill it in.'
    }

    Write-Host ''
    Write-Host '  Ctrl-C stops everything.'
    Write-Host ''

    # The supervision loop. A service that dies takes the rest down with it:
    # a half-running topology produces errors that describe the wrong thing.
    while ($true) {
        Start-Sleep -Milliseconds 700
        foreach ($entry in $script:Running) {
            if ($entry.Process.HasExited) {
                Write-Fail "$($entry.Name) exited with code $($entry.Process.ExitCode)"
                return
            }
        }
    }
}
catch {
    Write-Fail $_.Exception.Message
    exit 1
}
finally {
    Stop-All
}
