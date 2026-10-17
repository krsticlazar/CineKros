param(
    [Parameter(Mandatory = $true)]
    [string] $LauncherPath,
    [switch] $Headless,
    [switch] $V5,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'

function Stop-Safely([string] $Message) {
    [Console]::Error.WriteLine("GRESKA: $Message")
    exit 1
}

function Get-LocalValue([string] $Name) {
    $existing = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ($null -ne $existing) {
        if ([string]::IsNullOrWhiteSpace($existing)) { Stop-Safely "Procesna promenljiva $Name je prazna." }
        return $existing
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) { Stop-Safely "Nedostaje root .env ili $Name." }
    $assignments = @([System.IO.File]::ReadAllLines($envPath) | Where-Object { $_ -match ('^\s*' + [regex]::Escape($Name) + '\s*=') })
    if ($assignments.Count -ne 1) { Stop-Safely "Root .env mora sadržati tačno jednu ispravnu $Name stavku." }
    $value = ($assignments[0] -replace ('^\s*' + [regex]::Escape($Name) + '\s*='), '').Trim()
    if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    } elseif ($value.StartsWith('"') -or $value.StartsWith("'") -or $value.EndsWith('"') -or $value.EndsWith("'")) {
        Stop-Safely "Vrednost $Name u root .env ima neispravne navodnike."
    }
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Contains("`r") -or $value.Contains("`n") -or $value.Contains([char]0)) {
        Stop-Safely "Vrednost $Name u root .env je prazna ili neispravna."
    }
    return $value
}

if ($Stop) { & (Join-Path $PSScriptRoot 'start-headless-runtime.ps1') -Stop; exit $LASTEXITCODE }
$backendPort = if ($V5) { 5179 } else { 5186 }
$database = if ($V5) { 'cinekros' } else { 'cinekros_v6_rehearsal_20261015t2154z_4fd266aa' }
foreach ($port in @($backendPort, 5173)) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) { Stop-Safely "Port $port je zauzet; pokretanje je zaustavljeno bez diranja postojećeg procesa." }
}

$env:CINEKROS_POSTGRES_PASSWORD = Get-LocalValue 'CINEKROS_POSTGRES_PASSWORD'
if (-not $V5 -or [string]::IsNullOrWhiteSpace($env:CINEKROS_RECOMMENDATION_MODE)) { $env:CINEKROS_RECOMMENDATION_MODE = 'real' }
if ($env:CINEKROS_RECOMMENDATION_MODE -notin @('real', 'fake')) { Stop-Safely 'CINEKROS_RECOMMENDATION_MODE mora biti real ili fake.' }
$env:CINEKROS_SERBIAN_POC = 'false'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:CINEKROS_RUNTIME_PROFILE = if ($V5) { 'v5' } else { 'actor-v6-isolated' }
$env:CINEKROS_V6_SQL_DIAGNOSTICS = if ($V5) { 'false' } else { 'true' }
$env:VITE_API_BASE_URL = "http://127.0.0.1:$backendPort"
$env:CINEKROS_ALLOWED_ORIGINS = 'http://localhost:5173,http://127.0.0.1:5173'
$env:CINEKROS_STARTUP_HEADLESS = if ($Headless) { 'true' } else { 'false' }
if ($env:CINEKROS_RECOMMENDATION_MODE -eq 'real') {
    $env:GEMINI_API_KEY = Get-LocalValue 'GEMINI_API_KEY'
    $escapedPassword = $env:CINEKROS_POSTGRES_PASSWORD.Replace('"', '""')
    $env:DATABASE_CONNECTION_STRING = 'Host=127.0.0.1;Port=5433;Database=' + $database + ';Username=cinekros;Password="' + $escapedPassword + '";Options=-c default_transaction_read_only=on'
    $env:CINEKROS_E5_MODEL_DIR = Join-Path $repoRoot 'database\data\models\multilingual-e5-base\d128750597153bb5987e10b1c3493a34e5a4502a'
}

if ($Headless) {
    & (Join-Path $PSScriptRoot 'start-headless-runtime.ps1') -Headless
    exit $LASTEXITCODE
}

$frontendDir = Join-Path $repoRoot 'src\frontend'
$backendDir = Join-Path $repoRoot 'src\backend\CineKros.Api'
$backendDll = Join-Path $backendDir 'bin\Debug\net10.0\CineKros.Api.dll'
$viteEntry = Join-Path $frontendDir 'node_modules\vite\bin\vite.js'
if (-not (Test-Path -LiteralPath $backendDll -PathType Leaf)) { Stop-Safely 'Nedostaje prethodno izgradjen backend DLL.' }
if (-not (Test-Path -LiteralPath $viteEntry -PathType Leaf)) { Stop-Safely 'Nedostaje lokalni Vite; zavisnosti nisu instalirane.' }
if ($env:CINEKROS_RECOMMENDATION_MODE -eq 'real') {
    $modelFile = Join-Path $env:CINEKROS_E5_MODEL_DIR 'model_qint8_avx512_vnni.onnx'
    if (-not (Test-Path -LiteralPath $modelFile -PathType Leaf)) { Stop-Safely 'Nedostaje postojeći lokalni E5 model.' }
}

$docker = Get-Command docker.exe -ErrorAction SilentlyContinue
if ($null -eq $docker) { Stop-Safely 'Docker CLI nije dostupan; pokreni postojeći Docker engine i bazu.' }
$dockerInfo = [Diagnostics.ProcessStartInfo]::new($docker.Source, 'info --format {{.ServerVersion}}')
$dockerInfo.UseShellExecute = $false
$dockerInfo.CreateNoWindow = $true
$dockerInfo.RedirectStandardOutput = $true
$dockerInfo.RedirectStandardError = $true
$dockerProcess = [Diagnostics.Process]::new()
$dockerProcess.StartInfo = $dockerInfo
try {
    if (-not $dockerProcess.Start()) { Stop-Safely 'Postojeći Docker engine nije dostupan.' }
    if (-not $dockerProcess.WaitForExit(5000)) {
        try { $dockerProcess.Kill() } catch {}
        Stop-Safely 'Provera Docker engine-a je istekla; pokreni postojeći engine pa pokušaj ponovo.'
    }
    if ($dockerProcess.ExitCode -ne 0) { Stop-Safely 'Postojeći Docker engine nije dostupan; pokreni engine i bazu.' }
} catch [System.Management.Automation.ExitException] { throw }
catch { Stop-Safely 'Provera postojećeg Docker engine-a nije uspela.' }
finally { $dockerProcess.Dispose() }

$postgres = [Net.Sockets.TcpClient]::new()
try {
    $pending = $postgres.BeginConnect('127.0.0.1', 5433, $null, $null)
    if (-not $pending.AsyncWaitHandle.WaitOne(3000)) { Stop-Safely 'PostgreSQL nije dostupan na portu 5433; pokreni postojeći kontejner.' }
    $postgres.EndConnect($pending)
} catch { Stop-Safely 'PostgreSQL nije dostupan na portu 5433; pokreni postojeći kontejner.' }
finally { $postgres.Dispose() }

$env:CINEKROS_BACKEND_PORT = [string]$backendPort
$quotedLauncher = 'call "' + $LauncherPath + '" --bootstrap-ready'
$process = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/c', $quotedLauncher) -PassThru -NoNewWindow
# Wait only for bootstrap CMD, not its independent /k service consoles.
$process.WaitForExit()
exit $process.ExitCode
