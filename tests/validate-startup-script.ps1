$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$loaderPath = Join-Path $root 'src/load-startup-env.ps1'
$runtime = Get-Content -Raw (Join-Path $root 'src/start-headless-runtime.ps1')
$loader = Get-Content -Raw $loaderPath
$cmd = Get-Content -Raw (Join-Path $root 'src/start_script.cmd')
function Assert($Condition, $Name) { if (-not $Condition) { throw "FAIL: $Name" }; Write-Output "PASS: $Name" }
Assert ($loader.Contains('$process.WaitForExit()') -and $loader -notmatch '(?m)^\$process = Start-Process[^\r\n]* -Wait\b') 'startup waits only for bootstrap, not the independent service tree'
foreach ($file in @($loaderPath, (Join-Path $root 'src/start-headless-runtime.ps1'))) {
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) "PowerShell syntax: $([IO.Path]::GetFileName($file)) ($($parseErrors -join '; '))"
}
Assert ($cmd -match '--v5' -and $cmd -match '--stop' -and $cmd -match '--headless') 'explicit legacy, stop and headless options'
Assert ($cmd -match '--bootstrap-ready' -and $cmd -match 'start "CineKros %START_VERSION% - Backend" /D "%REPO_DIR%"' -and $cmd -match 'cmd\.exe /d /k dotnet' -and $cmd -match 'start "CineKros %START_VERSION% - Frontend" /D "%FRONTEND_DIR%"' -and $cmd -match 'cmd\.exe /d /k node node_modules/vite/bin/vite\.js' -and $cmd -match 'CINEKROS_RUNTIME_PROFILE%.*actor-v6-isolated.*v5') 'normal mode uses profile-guarded native CMD service consoles with correct working directories'
Assert ($cmd -match '127\.0\.0\.1.*CINEKROS_BACKEND_PORT' -and $cmd -match 'Invoke-WebRequest.*5173' -and $cmd -match 'start "" "http://127.0.0.1:5173/') 'backend TCP readiness precedes frontend HTTP 200 and browser launch'
Assert ($loader -match 'if \(\$Headless\)[\s\S]*start-headless-runtime\.ps1' -and $loader -match '--bootstrap-ready' -and $loader -match 'WaitForExit\(5000\)' -and $loader -match "BeginConnect\('127\.0\.0\.1', 5433" -and $loader -match '-NoNewWindow') 'headless remains legacy while normal mode performs bounded existing-service preflight in control console'
Assert ($loader -notmatch "Test-HttpStatus.*(404|405)" -and $cmd -notmatch '404|405' -and $cmd -notmatch '>>|2>') 'normal route has no 404/405 probes or service log redirection'
Assert ($cmd -match 'CineKros.*CTRL\+C' -and $cmd -match 'start "CineKros %START_VERSION%') 'independent service consoles communicate manual shutdown'
Assert ($runtime -match 'Test-DescendantOfLauncher' -and $runtime -match 'createdUtc' -and $runtime -match 'executablePath') 'ownership checks include process identity, not PID alone'
Assert ($runtime.IndexOf("Test-HttpStatus 'http://127.0.0.1:5173/' 200") -lt $runtime.IndexOf("Start-Process 'http://127.0.0.1:5173'")) 'browser opens after readiness'
Assert ($runtime -notmatch 'compose|migrat|import|taskkill|Stop-Process -Name') 'no database mutations or broad process termination'

# Load only pure ownership functions, never the runtime entry point or Stop-Process.
$runtimeAst = [Management.Automation.Language.Parser]::ParseInput($runtime, [ref]$null, [ref]$null)
$functions = $runtimeAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Protect-LogText','Convert-CreationUtc','Test-DescendantOfLauncher') }, $false)
. ([scriptblock]::Create(($functions.Extent.Text -join "`n")))
$created = [DateTime]::Parse('2026-10-17T12:00:00Z').ToUniversalTime()
$record = [pscustomobject]@{ ProcessId=123; CreationDate=$created; ExecutablePath='synthetic.exe'; CommandLine='synthetic --owned' }
$owner = [pscustomobject]@{ pid=123; createdUtc=$created.ToString('o'); executablePath='synthetic.exe'; commandLine='synthetic --owned' }
Assert (Test-DescendantOfLauncher $record $owner) 'matching ownership identity accepted'
$owner.commandLine='different command'
Assert (-not (Test-DescendantOfLauncher $record $owner)) 'changed command identity rejected'
$owner.commandLine=$record.CommandLine; $owner.createdUtc=$created.AddSeconds(1).ToString('o')
Assert (-not (Test-DescendantOfLauncher $record $owner)) 'PID reuse with different creation time rejected'
$owner.createdUtc=$created.ToString('o'); $owner.executablePath='unrelated.exe'
Assert (-not (Test-DescendantOfLauncher $record $owner)) 'different executable rejected'
$owner.executablePath=$record.ExecutablePath
$child=[pscustomobject]@{ProcessId=124;ParentProcessId=123;CreationDate=$created.AddSeconds(1)}
Assert (Test-DescendantOfLauncher $child $owner @{123=$record}) 'verified descendant accepted'
Assert (-not (Test-DescendantOfLauncher $child $owner @{})) 'missing parent rejected without guessing'
Assert ($cmd.IndexOf('set "STARTUP_DIR=%~dp0"') -lt $cmd.IndexOf('shift')) 'CMD location preserved before option SHIFT'
Assert ($runtime.Contains('$windowStyle = if ($Headless) { ''Hidden'' } else { ''Normal'' }')) 'headless stays hidden; interactive service windows are visible'
Assert ($runtime.Contains('Write-Host `$line; Add-Content') -and $runtime.Contains("@('-NoExit')")) 'interactive windows display actual streamed log lines and remain open'
Assert ($runtime -notmatch 'Read-Host') 'startup does not keep an ENTER control console'
Assert ($cmd -match 'START_EXIT.*errorlevel[\s\S]*START_INTERACTIVE.*pause') 'interactive startup failure remains readable'

# Exercise the real credential/configuration loader with an isolated capture stub.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cinekros-startup-validation-' + [guid]::NewGuid().ToString('N'))
$src = Join-Path $scratch 'src'
New-Item -ItemType Directory -Path $src -Force | Out-Null
Copy-Item -LiteralPath $loaderPath -Destination $src
@'
param([switch]$Headless,[switch]$Stop)
@{profile=$env:CINEKROS_RUNTIME_PROFILE;api=$env:VITE_API_BASE_URL;db=$env:DATABASE_CONNECTION_STRING;password=$env:CINEKROS_POSTGRES_PASSWORD;mode=$env:CINEKROS_RECOMMENDATION_MODE;stop=[bool]$Stop} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'capture.json')
exit 0
'@ | Set-Content (Join-Path $src 'start-headless-runtime.ps1')
function Run-Loader([string]$Options = '', [string]$Mode = 'fake') {
    $capture = Join-Path $src 'capture.json'
    Remove-Item -LiteralPath $capture -ErrorAction SilentlyContinue
    $info = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $src 'load-startup-env.ps1') + '" -LauncherPath unused ' + $Options
    $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($name in @('CINEKROS_POSTGRES_PASSWORD','GEMINI_API_KEY')) { [void]$info.EnvironmentVariables.Remove($name) }
    $info.EnvironmentVariables['CINEKROS_RECOMMENDATION_MODE']=$Mode
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    Assert (-not $output.Contains('synthetic-secret')) 'no credential output'
    return @{exit=$process.ExitCode;data=if(Test-Path $capture){Get-Content -Raw $capture|ConvertFrom-Json}else{$null}}
}
try {
    @('CINEKROS_POSTGRES_PASSWORD="synthetic-secret"','GEMINI_API_KEY=synthetic-key') | Set-Content (Join-Path $scratch '.env')
    $v6=Run-Loader '-Headless'
    Assert ($v6.exit -eq 0 -and $v6.data.profile -eq 'actor-v6-isolated' -and $v6.data.mode -eq 'real' -and $v6.data.api -eq 'http://127.0.0.1:5186') 'default V6 overrides inherited fake mode and API target'
    Assert ($v6.data.db -match 'Database=cinekros_v6_rehearsal_20261015t2154z_4fd266aa;' -and $v6.data.db -match 'default_transaction_read_only=on' -and $v6.data.password -eq 'synthetic-secret') 'V6 exact read-only database and quoted credential loading'
    $v5=Run-Loader '-V5 -Headless' 'real'
    Assert ($v5.exit -eq 0 -and $v5.data.profile -eq 'v5' -and $v5.data.api -eq 'http://127.0.0.1:5179' -and $v5.data.db -match 'Database=cinekros;') 'explicit V5 retains its own database and API target'
    'CINEKROS_POSTGRES_PASSWORD=synthetic-secret' | Set-Content (Join-Path $scratch '.env')
    $missing=Run-Loader
    Assert ($missing.exit -ne 0 -and $null -eq $missing.data) 'missing Gemini key fails before child launch'
    'CINEKROS_POSTGRES_PASSWORD="unterminated' | Set-Content (Join-Path $scratch '.env')
    $bad=Run-Loader
    Assert ($bad.exit -ne 0 -and $null -eq $bad.data) 'malformed secret fails closed'
    $stop=Run-Loader '-Stop'
    Assert ($stop.exit -eq 0 -and $stop.data.stop) 'stop works without reading credentials'
} finally {
    $resolved=[IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('cinekros-startup-validation-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
