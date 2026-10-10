param(
    [Parameter(Mandatory = $true)]
    [string] $LauncherPath,
    [switch] $Headless
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

foreach ($port in @(5179, 5173)) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) { Stop-Safely "Port $port je zauzet; pokretanje je zaustavljeno bez diranja postojećeg procesa." }
}

$env:CINEKROS_POSTGRES_PASSWORD = Get-LocalValue 'CINEKROS_POSTGRES_PASSWORD'
if ([string]::IsNullOrWhiteSpace($env:CINEKROS_RECOMMENDATION_MODE)) { $env:CINEKROS_RECOMMENDATION_MODE = 'real' }
if ($env:CINEKROS_RECOMMENDATION_MODE -notin @('real', 'fake')) { Stop-Safely 'CINEKROS_RECOMMENDATION_MODE mora biti real ili fake.' }
$env:CINEKROS_SERBIAN_POC = 'false'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:VITE_API_BASE_URL = 'http://127.0.0.1:5179'
$env:CINEKROS_ALLOWED_ORIGINS = 'http://localhost:5173,http://127.0.0.1:5173'
$env:CINEKROS_STARTUP_HEADLESS = if ($Headless) { 'true' } else { 'false' }
if ($env:CINEKROS_RECOMMENDATION_MODE -eq 'real') {
    $env:GEMINI_API_KEY = Get-LocalValue 'GEMINI_API_KEY'
    $escapedPassword = $env:CINEKROS_POSTGRES_PASSWORD.Replace('"', '""')
    $env:DATABASE_CONNECTION_STRING = 'Host=127.0.0.1;Port=5433;Database=cinekros;Username=cinekros;Password="' + $escapedPassword + '";Options=-c default_transaction_read_only=on'
    $env:CINEKROS_E5_MODEL_DIR = Join-Path $repoRoot 'database\data\models\multilingual-e5-base\d128750597153bb5987e10b1c3493a34e5a4502a'
}

$commandLine = '"{0}" --bootstrap-ready' -f $LauncherPath
$process = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/c', $commandLine) -NoNewWindow -PassThru
$process.WaitForExit()
exit $process.ExitCode
