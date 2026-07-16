[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status','sessions','config-get','config-set','environment-start','environment-wait','environment-status','environment-stop','capabilities','run','wait','cancel','capture-start','capture-stop','events','restart-dashboard')]
    [string]$Command,
    [string]$BaseUrl = 'http://127.0.0.1:7781/',
    [string]$InputJson,
    [string]$TestId,
    [string]$TargetSessionId,
    [string]$TargetRole,
    [string]$RequestId,
    [string]$MutationKind,
    [string]$ParametersJson = '{}',
    [string]$Name = 'codex-capture',
    [int]$TimeoutSeconds = 120,
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/') + '/'

function Read-JsonInput([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    if (Test-Path -LiteralPath $value) { return Get-Content -LiteralPath $value -Raw | ConvertFrom-Json }
    return $value | ConvertFrom-Json
}

function Get-Session {
    return Invoke-RestMethod -Uri ($BaseUrl + 'api/session') -Method Get -TimeoutSec 5
}

$script:Session = $null
function Invoke-HarnessApi([string]$method, [string]$path, $body = $null) {
    if ($null -eq $script:Session) { $script:Session = Get-Session }
    $headers = @{ 'X-DVMP-Debug-Token' = $script:Session.apiToken }
    $arguments = @{ Uri = ($BaseUrl + $path.TrimStart('/')); Method = $method; Headers = $headers; TimeoutSec = [Math]::Max(5, $TimeoutSeconds) }
    if ($null -ne $body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = ($body | ConvertTo-Json -Depth 20 -Compress)
    }
    return Invoke-RestMethod @arguments
}

function Write-Result($value) {
    if ($null -eq $value) { Write-Output '{}' }
    else { Write-Output ($value | ConvertTo-Json -Depth 30) }
}

function Write-RunResult($run) {
    Write-Result $run
    if ([string]$run.status -ne 'Passed') { exit 2 }
}

function Wait-Run([string]$id) {
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
    do {
        $run = Invoke-HarnessApi 'GET' ('api/runtime-tests/runs/' + [Uri]::EscapeDataString($id))
        if (@('Passed','Failed','FailedDirty','Cancelled','Unsupported') -contains [string]$run.status) { return $run }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "runtime-test-wait-timeout:$id"
}

function Wait-Environment {
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
    do {
        $status = Invoke-HarnessApi 'GET' 'api/runtime-environment/status'
        if ([bool]$status.ready) { return $status }
        if ([string]$status.stage -eq 'Failed') {
            throw "runtime-environment-failed:$($status.error):$($status.message)"
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'runtime-environment-wait-timeout'
}

if ($Command -eq 'restart-dashboard') {
    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
        $ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    }
    $oldProcessId = 0
    $oldSession = $null
    try {
        $oldSession = Get-Session
        $oldProcessId = [int]$oldSession.processId
    } catch { }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
    $output = Join-Path $env:LOCALAPPDATA ("DVMultiplayer\dashboard-builds\$stamp")
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $project = Join-Path $ProjectRoot 'DebugRemoteClient\Multiplayer.DebugClient.csproj'
    $buildOutput = & dotnet build $project -c Debug --no-restore ("-p:OutputPath=$output\") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dashboard-build-failed:`n$($buildOutput -join [Environment]::NewLine)" }
    $executable = Join-Path $output 'Multiplayer.DebugClient.exe'

    if ($oldProcessId -gt 0) {
        try {
            $headers = @{ 'X-DVMP-Debug-Token' = $oldSession.apiToken }
            Invoke-RestMethod -Uri ($BaseUrl + 'api/dashboard/shutdown') -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' -TimeoutSec 3 | Out-Null
        } catch { }

        $handoffDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while ($null -ne (Get-Process -Id $oldProcessId -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $handoffDeadline) {
            Start-Sleep -Milliseconds 100
        }

        $oldProcess = Get-Process -Id $oldProcessId -ErrorAction SilentlyContinue
        if ($null -ne $oldProcess) {
            if ([string]$oldProcess.ProcessName -ne 'Multiplayer.DebugClient') {
                throw "dashboard-restart-refused-unexpected-process:${oldProcessId}:$($oldProcess.ProcessName)"
            }
            Stop-Process -Id $oldProcessId -Force
            $oldProcess.WaitForExit(5000) | Out-Null
        }
    }

    Start-Process -FilePath $executable -ArgumentList '--dashboard' -WorkingDirectory $ProjectRoot -WindowStyle Hidden | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(10, $TimeoutSeconds))
    do {
        Start-Sleep -Milliseconds 200
        try {
            $automation = Invoke-RestMethod -Uri ($BaseUrl + 'api/automation') -TimeoutSec 2
            if ([int]$automation.processId -ne $oldProcessId -and [int]$automation.processId -gt 0) {
                Write-Result ([ordered]@{ restarted = $true; oldProcessId = $oldProcessId; processId = [int]$automation.processId; executable = $executable; apiVersion = $automation.apiVersion })
                exit 0
            }
        } catch { }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'dashboard-restart-timeout'
}

switch ($Command) {
    'status' {
        Write-Result ([ordered]@{
            automation = Invoke-RestMethod -Uri ($BaseUrl + 'api/automation') -TimeoutSec 5
            environment = Invoke-HarnessApi 'GET' 'api/runtime-environment/status'
            sessions = Invoke-HarnessApi 'GET' 'api/sessions'
        })
    }
    'sessions' { Write-Result (Invoke-HarnessApi 'GET' 'api/sessions') }
    'config-get' { Write-Result (Invoke-HarnessApi 'GET' 'api/runtime-environment/config') }
    'config-set' {
        $configuration = Read-JsonInput $InputJson
        if ($null -eq $configuration) { throw 'config-set-requires-InputJson' }
        Invoke-HarnessApi 'POST' 'api/runtime-environment/config' $configuration | Out-Null
        Write-Result (Invoke-HarnessApi 'GET' 'api/runtime-environment/config')
    }
    'environment-start' {
        $configuration = Read-JsonInput $InputJson
        if ($null -eq $configuration) { $configuration = (Invoke-HarnessApi 'GET' 'api/runtime-environment/config').configuration }
        Write-Result (Invoke-HarnessApi 'POST' 'api/runtime-environment/start' $configuration)
    }
    'environment-wait' { Write-Result (Wait-Environment) }
    'environment-status' { Write-Result (Invoke-HarnessApi 'GET' 'api/runtime-environment/status') }
    'environment-stop' { Write-Result (Invoke-HarnessApi 'POST' 'api/runtime-environment/stop' @{}) }
    'capabilities' { Write-Result (Invoke-HarnessApi 'GET' 'api/runtime-tests/capabilities') }
    'run' {
        if ([string]::IsNullOrWhiteSpace($TestId)) { throw 'run-requires-TestId' }
        $capabilities = Invoke-HarnessApi 'GET' 'api/runtime-tests/capabilities'
        $descriptor = $capabilities.tests | Where-Object { $_.testId -eq $TestId } | Select-Object -First 1
        if ($null -eq $descriptor) { throw "unknown-runtime-test:$TestId" }
        if ([string]::IsNullOrWhiteSpace($MutationKind)) { $MutationKind = [string]$descriptor.mutationKind }
        $rawParameters = Read-JsonInput $ParametersJson
        $parameters = @{}
        if ($null -ne $rawParameters) { $rawParameters.psobject.Properties | ForEach-Object { $parameters[$_.Name] = [string]$_.Value } }
        $id = [Guid]::NewGuid().ToString('N')
        $request = [ordered]@{
            requestId = $id; runId = ('codex-' + $id); caseId = $TestId; phaseId = 'act'; stepId = $TestId
            command = $TestId; targetSessionId = $TargetSessionId; targetRole = $TargetRole
            mutationKind = $MutationKind; timeoutMilliseconds = [Math]::Max(1000, [int]$descriptor.timeoutMilliseconds); parameters = $parameters
        }
        $accepted = Invoke-HarnessApi 'POST' 'api/runtime-tests/commands' $request
        if (-not $accepted.accepted) { throw "runtime-test-rejected:$($accepted.reason)" }
        Write-RunResult (Wait-Run $accepted.requestId)
    }
    'wait' {
        if ([string]::IsNullOrWhiteSpace($RequestId)) { throw 'wait-requires-RequestId' }
        Write-RunResult (Wait-Run $RequestId)
    }
    'cancel' {
        if ([string]::IsNullOrWhiteSpace($RequestId)) { throw 'cancel-requires-RequestId' }
        Invoke-HarnessApi 'POST' ('api/runtime-tests/runs/' + [Uri]::EscapeDataString($RequestId) + '/cancel') @{} | Out-Null
        Write-Result ([ordered]@{ requestId = $RequestId; cancelled = $true })
    }
    'capture-start' { Write-Result (Invoke-HarnessApi 'POST' 'api/capture/start' ([ordered]@{ name = $Name; captureId = [Guid]::NewGuid().ToString('N') })) }
    'capture-stop' { Write-Result (Invoke-HarnessApi 'POST' 'api/capture/stop' @{}) }
    'events' {
        $query = Read-JsonInput $InputJson
        if ($null -eq $query) { $query = [ordered]@{ limit = 200 } }
        Write-Result (Invoke-HarnessApi 'POST' 'api/events/query' $query)
    }
}
