$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\src\start_script.cmd'
$loaderPath = Join-Path $PSScriptRoot '..\src\load-startup-env.ps1'
$content = Get-Content -Raw -LiteralPath $scriptPath
$loader = Get-Content -Raw -LiteralPath $loaderPath

function Assert-Contains([string] $Name, [string] $Text, [string] $Pattern) {
    if ($Text -notmatch $Pattern) { throw "FAIL: $Name" }
    Write-Output "PASS: $Name"
}

function Assert-Order([string] $Name, [string[]] $Patterns) {
    $cursor = -1
    foreach ($pattern in $Patterns) {
        $match = [regex]::Match($content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success -or $match.Index -le $cursor) { throw "FAIL: $Name" }
        $cursor = $match.Index
    }
    Write-Output "PASS: $Name"
}

function Assert-LoopContains([string] $Name, [string] $Header, [string] $RequiredPattern) {
    $headerMatch = [regex]::Match($content, [regex]::Escape($Header), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $headerMatch.Success) { throw "FAIL: $Name" }
    $end = $content.IndexOf("`n)", $headerMatch.Index)
    if ($end -lt 0 -or $content.Substring($headerMatch.Index, $end - $headerMatch.Index) -notmatch $RequiredPattern) { throw "FAIL: $Name" }
    Write-Output "PASS: $Name"
}

Assert-Contains 'launcher resolves an absolute repository root from its own location' $content 'for %%I in \("%~dp0\.\."\) do set "REPO_DIR=%%~fI"'
if ($content -match 'net session|Verb RunAs') { throw 'FAIL: local Docker CLI startup unexpectedly requires administrator elevation' }
Write-Output 'PASS: local Docker CLI startup does not require administrator elevation'
Assert-Contains 'PowerShell loader runs before Docker and Docker receives child process environment' $content 'load-startup-env\.ps1[\s\S]*?docker\.exe info'
Assert-Contains 'loader reads root .env without emitting values' $loader 'ReadAllLines\(\$envPath\)[\s\S]*?\$env:CINEKROS_POSTGRES_PASSWORD = Get-LocalValue[\s\S]*?Start-Process'
Assert-Contains 'existing process credential has precedence over root .env' $loader 'GetEnvironmentVariable\(\$Name, ''Process''\)[\s\S]*?if \(\$null -ne \$existing\)'
Assert-Contains 'real mode derives local process-only database and E5 configuration' $loader 'CINEKROS_RECOMMENDATION_MODE = ''real''[\s\S]*?GEMINI_API_KEY = Get-LocalValue[\s\S]*?DATABASE_CONNECTION_STRING[\s\S]*?CINEKROS_E5_MODEL_DIR'
Assert-Contains 'launcher waits for its direct child only' $loader '\$process\.WaitForExit\(\)'
Assert-Contains 'Docker-ready check bypasses Desktop launch' $content 'docker\.exe info >nul 2>&1\s*if not errorlevel 1 goto docker_ready[\s\S]*?if not exist "%ProgramFiles%\\Docker\\Docker\\Docker Desktop\.exe"[\s\S]*?start "" "%ProgramFiles%\\Docker\\Docker\\Docker Desktop\.exe"'
Assert-Order 'Docker ready, Compose, database health, backend, frontend, browser order is preserved' @(
    'docker\.exe info >nul 2>&1',
    'docker\.exe compose -f .* up -d cinekros-postgres',
    ':database_ready',
    'start "CineKros Backend"',
    'start "CineKros Frontend"',
    'start "" "http://localhost:5173"'
)
Assert-LoopContains 'Docker engine waits are bounded to 120 seconds' 'for /l %%I in (1,1,60)' 'docker\.exe info'
Assert-LoopContains 'database health waits are bounded to 90 seconds' 'for /l %%I in (1,1,45)' 'docker\.exe inspect'
Assert-Contains 'database health accepts LF-only Docker output without findstr' $content 'for /f "delims=" %%H in \(''docker\.exe inspect[\s\S]*?if /i "%%H"=="healthy" goto database_ready'
if ($content -match 'docker\.exe inspect[^\r\n]*\|\s*findstr') { throw 'FAIL: LF-only Docker health output still uses findstr' }
Assert-Contains 'backend and frontend readiness waits remain bounded' $content 'backend nije postao dostupan u roku od 60 sekundi[\s\S]*?Frontend nije postao dostupan u roku od 60 sekundi'
Assert-Contains 'real default, explicit fake override and local E5 model path remain explicit' $content 'if not defined CINEKROS_RECOMMENDATION_MODE set "CINEKROS_RECOMMENDATION_MODE=real"[\s\S]*?Development fake[\s\S]*?DATABASE_CONNECTION_STRING[\s\S]*?GEMINI_API_KEY[\s\S]*?CINEKROS_E5_MODEL_DIR[\s\S]*?model_qint8_avx512_vnni\.onnx'

if ($content -match '(?im)\bstart\s+docker\b|docker://|App\s*Picker|docker\.exe\s+compose\s+down\s+-v|docker\.exe\s+volume\s+rm|docker\.exe\s+system\s+prune|docker\.exe\s+rm\s+-f') {
    throw 'FAIL: forbidden launcher, App Picker, or destructive Docker operation found'
}
Write-Output 'PASS: no App Picker path, forbidden Docker launcher, or destructive Docker operation'

# Exercise the loader only against isolated synthetic credentials and a fake CMD launcher.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('cinekros-startup-validation-' + [guid]::NewGuid().ToString('N'))
$srcDir = Join-Path $scratch 'src'
New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
Copy-Item -LiteralPath $loaderPath -Destination (Join-Path $srcDir 'load-startup-env.ps1')
$fakeLauncher = Join-Path $scratch 'capture.cmd'
$capturePath = Join-Path $scratch 'captured.txt'
$configCapturePath = Join-Path $scratch 'config.txt'
Set-Content -LiteralPath $fakeLauncher -Encoding Ascii -Value @(
    '@echo off',
    ('> "{0}" echo %CINEKROS_POSTGRES_PASSWORD%' -f $capturePath),
    ('> "{0}" echo %CINEKROS_RECOMMENDATION_MODE%' -f $configCapturePath),
    ('if defined GEMINI_API_KEY >> "{0}" echo KEY_PRESENT' -f $configCapturePath),
    ('if defined DATABASE_CONNECTION_STRING >> "{0}" echo DB_PRESENT' -f $configCapturePath),
    ('if defined CINEKROS_E5_MODEL_DIR >> "{0}" echo E5_PRESENT' -f $configCapturePath)
)
$testSecret = 'synthetic-startup-secret-8742'
$powerShellExe = (Get-Command powershell.exe).Source

function Invoke-IsolatedLoader([string] $ExpectedPassword, [switch] $SetProcessPassword, [string] $Mode = 'fake') {
    Remove-Item -LiteralPath $capturePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $configCapturePath -Force -ErrorAction SilentlyContinue
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShellExe
    $startInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -LauncherPath "{1}"' -f (Join-Path $srcDir 'load-startup-env.ps1'), $fakeLauncher
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void] $startInfo.EnvironmentVariables.Remove('CINEKROS_POSTGRES_PASSWORD')
    [void] $startInfo.EnvironmentVariables.Remove('GEMINI_API_KEY')
    [void] $startInfo.EnvironmentVariables.Remove('DATABASE_CONNECTION_STRING')
    [void] $startInfo.EnvironmentVariables.Remove('CINEKROS_E5_MODEL_DIR')
    if ($Mode -ne 'default') { $startInfo.EnvironmentVariables['CINEKROS_RECOMMENDATION_MODE'] = $Mode } else { [void] $startInfo.EnvironmentVariables.Remove('CINEKROS_RECOMMENDATION_MODE') }
    if ($SetProcessPassword) { $startInfo.EnvironmentVariables['CINEKROS_POSTGRES_PASSWORD'] = $ExpectedPassword }
    $child = [System.Diagnostics.Process]::new()
    $child.StartInfo = $startInfo
    [void] $child.Start()
    $stdout = $child.StandardOutput.ReadToEnd()
    $stderr = $child.StandardError.ReadToEnd()
    $child.WaitForExit()
    if (($stdout + $stderr).Contains($testSecret) -or ($stdout + $stderr).Contains($ExpectedPassword)) {
        throw 'FAIL: loader exposed a synthetic secret in output'
    }
    return [pscustomobject]@{ ExitCode = $child.ExitCode; Output = $stdout + $stderr; CaptureExists = (Test-Path -LiteralPath $capturePath); Captured = if (Test-Path -LiteralPath $capturePath) { (Get-Content -Raw -LiteralPath $capturePath).TrimEnd("`r", "`n") } else { $null }; Config = if (Test-Path -LiteralPath $configCapturePath) { Get-Content -LiteralPath $configCapturePath } else { @() } }
}

try {
    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value @('# synthetic test fixture', (' CINEKROS_POSTGRES_PASSWORD = "  {0}  " ' -f $testSecret))
    $loaded = Invoke-IsolatedLoader -ExpectedPassword ('  ' + $testSecret + '  ')
    if ($loaded.ExitCode -ne 0 -or -not $loaded.CaptureExists -or $loaded.Captured -ne ('  ' + $testSecret + '  ')) { throw "FAIL: quoted .env value was not safely loaded into the child process (exit=$($loaded.ExitCode), captured=$($loaded.CaptureExists), output=$($loaded.Output))" }
    Write-Output 'PASS: quoted .env value and surrounding whitespace load into child process without output leakage'

    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value 'UNRELATED=value'
    $missing = Invoke-IsolatedLoader -ExpectedPassword $testSecret
    if ($missing.ExitCode -eq 0 -or $missing.CaptureExists -or $missing.Output -notmatch 'CINEKROS_POSTGRES_PASSWORD') { throw "FAIL: missing password did not fail safely before launching child CMD (exit=$($missing.ExitCode), captured=$($missing.CaptureExists), output=$($missing.Output))" }
    Write-Output 'PASS: missing .env entry fails before child CMD'

    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value @('CINEKROS_POSTGRES_PASSWORD="unterminated')
    $invalid = Invoke-IsolatedLoader -ExpectedPassword $testSecret
    if ($invalid.ExitCode -eq 0 -or $invalid.CaptureExists -or $invalid.Output -notmatch 'navodnike') { throw 'FAIL: malformed quoted password did not fail safely before launching child CMD' }
    Write-Output 'PASS: malformed .env entry fails before child CMD'

    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value ('CINEKROS_POSTGRES_PASSWORD={0}' -f $testSecret)
    $precedenceSecret = 'synthetic-process-secret-9631'
    $precedence = Invoke-IsolatedLoader -ExpectedPassword $precedenceSecret -SetProcessPassword
    if ($precedence.ExitCode -ne 0 -or -not $precedence.CaptureExists -or $precedence.Captured -ne $precedenceSecret) { throw 'FAIL: existing process value did not take precedence over root .env' }
    Write-Output 'PASS: existing process value takes precedence over .env'

    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value @('CINEKROS_POSTGRES_PASSWORD=synthetic-db-password', 'GEMINI_API_KEY=synthetic-gemini-key')
    $real = Invoke-IsolatedLoader -ExpectedPassword 'synthetic-db-password' -Mode 'default'
    if ($real.ExitCode -ne 0 -or $real.Config -notcontains 'real' -or $real.Config -notcontains 'KEY_PRESENT' -or $real.Config -notcontains 'DB_PRESENT' -or $real.Config -notcontains 'E5_PRESENT') { throw 'FAIL: default real mode did not load Gemini and derive DB/E5 configuration' }
    Write-Output 'PASS: default real mode loads process-only Gemini and derives DB/E5 configuration'

    Set-Content -LiteralPath (Join-Path $scratch '.env') -Encoding Ascii -Value 'CINEKROS_POSTGRES_PASSWORD=synthetic-db-password'
    $missingKey = Invoke-IsolatedLoader -ExpectedPassword 'synthetic-db-password' -Mode 'default'
    if ($missingKey.ExitCode -eq 0 -or $missingKey.CaptureExists -or $missingKey.Output -notmatch 'GEMINI_API_KEY') { throw 'FAIL: missing Gemini key did not fail before launching real mode' }
    Write-Output 'PASS: missing Gemini key fails safely before real child launch'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

# Docker emits LF-only output on Windows. The old `findstr /x` readiness pipeline
# rejected this exact healthy status; FOR /F must accept the same bytes.
$lfHealthPath = Join-Path ([System.IO.Path]::GetTempPath()) ('cinekros-health-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    [System.IO.File]::WriteAllBytes($lfHealthPath, [byte[]](104,101,97,108,116,104,121,10))
    $healthProbe = 'for /f "usebackq delims=" %H in ({0}) do @if /i "%H"=="healthy" echo HEALTH_OK' -f ('"' + $lfHealthPath + '"')
    $healthOutput = & cmd.exe /d /s /c $healthProbe
    if ($LASTEXITCODE -ne 0 -or $healthOutput -notcontains 'HEALTH_OK') { throw 'FAIL: LF-only healthy Docker status did not pass CMD readiness parsing' }
    Write-Output 'PASS: LF-only Docker healthy output passes actual CMD readiness parsing'
} finally {
    Remove-Item -LiteralPath $lfHealthPath -Force -ErrorAction SilentlyContinue
}
