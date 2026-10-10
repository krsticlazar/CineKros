$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$backendDir = Join-Path $repoRoot 'src\backend\CineKros.Api'
$frontendDir = Join-Path $repoRoot 'src\frontend'
$reportRoot = Join-Path $repoRoot '.local\runtime'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runDir = Join-Path $reportRoot $runId
$manifestPath = Join-Path $runDir 'owned-processes.json'
$startupClock = [System.Diagnostics.Stopwatch]::StartNew()
$runStartedUtc = [DateTime]::UtcNow.ToString('o')
$ownedLaunchers = [System.Collections.Generic.List[object]]::new()
$manifest = [ordered]@{ runId = $runId; startedUtc = $runStartedUtc; status = 'preflight'; processes = @(); cleanup = @(); error = $null }

function Get-CimProcess([int] $Id) {
    return Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $Id" -ErrorAction SilentlyContinue
}

function Convert-CreationUtc($CreationDate) {
    if ($null -eq $CreationDate) { return $null }
    return ([DateTime]$CreationDate).ToUniversalTime().ToString('o')
}

function Protect-LogText([string] $Text) {
    $safe = $Text -replace '(?i)(GEMINI_API_KEY|CINEKROS_POSTGRES_PASSWORD|TMDB_READ_ACCESS_TOKEN)\s*=\s*[^;\s]+', '$1=[REDACTED]'
    $safe = $safe -replace '(?i)(Password\s*=\s*)("[^"]*"|[^;\s]+)', '$1[REDACTED]'
    $safe = $safe -replace '(?i)(Host\s*=\s*[^;]+;Port\s*=\s*\d+;Database\s*=\s*[^;]+;Username\s*=\s*[^;]+;Password\s*=\s*)("[^"]*"|[^;\s]+)', '$1[REDACTED]'
    return $safe
}

function Save-Manifest {
    if (-not (Test-Path -LiteralPath $runDir -PathType Container)) { return }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Test-DescendantOfLauncher($ProcessRecord, $Launcher) {
    if ($null -eq $ProcessRecord) { return $false }
    $current = $ProcessRecord
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    for ($depth = 0; $depth -lt 32; $depth++) {
        $currentId = [int]$current.ProcessId
        if (-not $seen.Add($currentId)) { return $false }
        if ($currentId -eq [int]$Launcher.pid) {
            $createdUtc = Convert-CreationUtc $current.CreationDate
            if ($createdUtc -ne $Launcher.createdUtc) { return $false }
            if ($current.ExecutablePath -ne $Launcher.executablePath) { return $false }
            return $true
        }
        $parent = Get-CimProcess ([int]$current.ParentProcessId)
        if ($null -eq $parent) { return $false }
        if ([DateTime]$current.CreationDate -lt [DateTime]$parent.CreationDate) { return $false }
        $current = $parent
    }
    return $false
}

function Get-VerifiedDescendants($Launcher) {
    $all = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue)
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in $all) {
        if ([int]$candidate.ProcessId -eq [int]$Launcher.pid) { continue }
        if (Test-DescendantOfLauncher $candidate $Launcher) { $result.Add($candidate) }
    }
    return @($result | Sort-Object { [DateTime]$_.CreationDate } -Descending)
}

function Stop-OwnedProcesses {
    $stopped = [System.Collections.Generic.List[object]]::new()
    foreach ($launcher in $ownedLaunchers) {
        $descendants = @(Get-VerifiedDescendants $launcher)
        foreach ($child in $descendants) {
            $current = Get-CimProcess ([int]$child.ProcessId)
            if ($null -ne $current -and (Test-DescendantOfLauncher $current $launcher)) {
                try {
                    Stop-Process -Id ([int]$current.ProcessId) -Force -ErrorAction Stop
                    $stopped.Add([pscustomobject]@{ pid = [int]$current.ProcessId; executable = $current.Name; createdUtc = (Convert-CreationUtc $current.CreationDate); ownership = 'verified-descendant' })
                } catch { }
            }
        }
        $root = Get-CimProcess ([int]$launcher.pid)
        if ($null -ne $root -and (Test-DescendantOfLauncher $root $launcher)) {
            try {
                Stop-Process -Id ([int]$launcher.pid) -Force -ErrorAction Stop
                $stopped.Add([pscustomobject]@{ pid = [int]$launcher.pid; executable = $root.Name; createdUtc = (Convert-CreationUtc $root.CreationDate); ownership = 'verified-launcher' })
            } catch { }
        }
    }
    $manifest.cleanup = @($stopped)
}

function Get-ListenerPids([int] $Port) {
    return @((Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique) | ForEach-Object { [int]$_ })
}

function Wait-OwnedListener([int] $Port, [int] $Seconds, $Launcher) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $pids = @(Get-ListenerPids $Port)
        if ($pids.Count -gt 1) { throw "Port $Port has multiple listener processes; no listener was accepted." }
        if ($pids.Count -eq 1) {
            $listener = Get-CimProcess $pids[0]
            if (-not (Test-DescendantOfLauncher $listener $Launcher)) { throw "Port $Port listener PID $($pids[0]) is not a verified child of the launcher." }
            return $listener
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "Owned service did not open port $Port within $Seconds seconds."
}

function Test-HttpStatus([string] $Url, [int] $ExpectedStatus) {
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = 'GET'
        $request.Timeout = 3000
        $response = $request.GetResponse()
        try { return [int]$response.StatusCode -eq $ExpectedStatus }
        finally { $response.Dispose() }
    } catch [System.Net.WebException] {
        if ($null -ne $_.Exception.Response) {
            try { return [int]$_.Exception.Response.StatusCode -eq $ExpectedStatus }
            finally { $_.Exception.Response.Dispose() }
        }
        return $false
    }
}

function Start-LoggedProcess([string] $Name, [string] $WorkingDirectory, [string] $Command, [string] $Arguments, [string] $LogPath) {
    $safeLog = $LogPath.Replace("'", "''")
    $safeDirectory = $WorkingDirectory.Replace("'", "''")
    $runner = "`$ErrorActionPreference='Continue'; Set-Location -LiteralPath '$safeDirectory'; & '$Command' $Arguments 2>&1 | ForEach-Object { `$line=`$_.ToString(); `$line=`$line -replace '(?i)(GEMINI_API_KEY|CINEKROS_POSTGRES_PASSWORD|TMDB_READ_ACCESS_TOKEN)\s*=\s*[^;\s]+', '`$1=[REDACTED]'; `$line=`$line -replace '(?i)(Password\s*=\s*)(`"[^`"]*`"|[^;\s]+)', '`$1[REDACTED]'; `$line=`$line -replace '(?i)(Host\s*=\s*[^;]+;Port\s*=\s*\d+;Database\s*=\s*[^;]+;Username\s*=\s*[^;]+;Password\s*=\s*)(`"[^`"]*`"|[^;\s]+)', '`$1[REDACTED]'; Add-Content -LiteralPath '$safeLog' -Value `$line -Encoding UTF8 }; exit `$LASTEXITCODE"
    $encodedRunner = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($runner))
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-EncodedCommand', $encodedRunner) -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
    $processInfo = Get-CimProcess $process.Id
    if ($null -eq $processInfo) { throw "Could not identify the new $Name launcher process." }
    $entry = [pscustomobject]@{
        name = $Name
        pid = [int]$processInfo.ProcessId
        executable = $processInfo.Name
        executablePath = $processInfo.ExecutablePath
        command = "$Command $Arguments"
        commandLine = Protect-LogText ([string]$processInfo.CommandLine)
        createdUtc = Convert-CreationUtc $processInfo.CreationDate
        log = $LogPath
    }
    $ownedLaunchers.Add($entry)
    $manifest.processes = @($manifest.processes) + @($entry)
    Save-Manifest
    return $entry
}

try {
    foreach ($port in @(5179, 5173)) {
        if ((Get-ListenerPids $port).Count -gt 0) { throw "Port $port is occupied; no existing process was changed." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $frontendDir 'node_modules\vite\bin\vite.js') -PathType Leaf)) { throw 'Vite is not installed locally; no package installation was attempted.' }
    if ([string]::IsNullOrWhiteSpace($env:CINEKROS_E5_MODEL_DIR) -or -not (Test-Path -LiteralPath (Join-Path $env:CINEKROS_E5_MODEL_DIR 'model_qint8_avx512_vnni.onnx') -PathType Leaf)) { throw 'Pinned E5 model artifact is unavailable.' }

    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    $manifest.status = 'starting'
    $backendLog = Join-Path $runDir 'backend.log'
    $backend = Start-LoggedProcess 'backend-launcher' $backendDir 'dotnet.exe' 'run --no-launch-profile --urls http://127.0.0.1:5179' $backendLog
    $backendListener = Wait-OwnedListener 5179 180 $backend
    $manifest.processes = @($manifest.processes) + @([pscustomobject]@{
        name = 'backend-listener'; pid = [int]$backendListener.ProcessId; executable = $backendListener.Name
        executablePath = $backendListener.ExecutablePath; command = 'verified descendant listening on 127.0.0.1:5179'
        commandLine = Protect-LogText ([string]$backendListener.CommandLine); createdUtc = Convert-CreationUtc $backendListener.CreationDate; log = $backendLog
    })
    Save-Manifest
    if (-not (Test-HttpStatus 'http://127.0.0.1:5179/' 404)) { throw 'Backend root did not return the expected 404 after startup readiness.' }
    if (-not (Test-HttpStatus 'http://127.0.0.1:5179/api/recommendations' 405)) { throw 'API recommendation route did not return the expected method-only response.' }

    $frontendLog = Join-Path $runDir 'frontend.log'
    $frontend = Start-LoggedProcess 'frontend-launcher' $frontendDir 'npm.cmd' 'run dev -- --host 127.0.0.1 --port 5173 --strictPort' $frontendLog
    $frontendListener = Wait-OwnedListener 5173 60 $frontend
    $manifest.processes = @($manifest.processes) + @([pscustomobject]@{
        name = 'frontend-listener'; pid = [int]$frontendListener.ProcessId; executable = $frontendListener.Name
        executablePath = $frontendListener.ExecutablePath; command = 'verified descendant listening on 127.0.0.1:5173'
        commandLine = Protect-LogText ([string]$frontendListener.CommandLine); createdUtc = Convert-CreationUtc $frontendListener.CreationDate; log = $frontendLog
    })
    Save-Manifest
    if (-not (Test-HttpStatus 'http://127.0.0.1:5173/' 200)) { throw 'Frontend did not return HTTP 200.' }

    $manifest.status = 'ready'
    $manifest.readyUtc = [DateTime]::UtcNow.ToString('o')
    $startupClock.Stop()
    $manifest.startupElapsedSeconds = [Math]::Round($startupClock.Elapsed.TotalSeconds, 2)
    Save-Manifest
    Write-Output "Headless full runtime ready: API root=404, API recommendations GET=405, UI=200; startupSeconds=$($manifest.startupElapsedSeconds); owned PID evidence and sanitized logs: $runDir"
} catch {
    $startupClock.Stop()
    $safeError = Protect-LogText ([string]$_.Exception.Message)
    Stop-OwnedProcesses
    $manifest.status = 'failed-cleaned'
    $manifest.error = $safeError
    $manifest.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $manifest.startupElapsedSeconds = [Math]::Round($startupClock.Elapsed.TotalSeconds, 2)
    Save-Manifest
    [Console]::Error.WriteLine("Startup failed: $safeError")
    if (Test-Path -LiteralPath $runDir -PathType Container) { [Console]::Error.WriteLine("Owned-process cleanup evidence and sanitized logs: $runDir") }
    exit 1
}
