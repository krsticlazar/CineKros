param([switch] $CheckOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'
$databaseName = 'cinekros_sr_poc_phase06t_v2_20261009'
$modelRevision = 'd128750597153bb5987e10b1c3493a34e5a4502a'
$modelDir = Join-Path $repoRoot "database\data\models\multilingual-e5-base\$modelRevision"
$backendDir = Join-Path $repoRoot 'src\backend\CineKros.Api'
$frontendDir = Join-Path $repoRoot 'src\frontend'

function Stop-Poc([string] $Message) {
    [Console]::Error.WriteLine("POC preflight failed: $Message")
    exit 1
}

function Get-LocalValue([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ($null -ne $value) {
        if ([string]::IsNullOrWhiteSpace($value)) { Stop-Poc "Process value $Name is empty." }
        return $value
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) { Stop-Poc "Missing root .env or $Name." }
    $matches = @([System.IO.File]::ReadAllLines($envPath) | Where-Object { $_ -match ('^\s*' + [regex]::Escape($Name) + '\s*=') })
    if ($matches.Count -ne 1) { Stop-Poc "Root .env must contain exactly one $Name entry." }
    $value = ($matches[0] -replace ('^\s*' + [regex]::Escape($Name) + '\s*=\s*'), '').Trim()
    if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    } elseif ($value.StartsWith('"') -or $value.StartsWith("'") -or $value.EndsWith('"') -or $value.EndsWith("'")) {
        Stop-Poc "Root .env value $Name has mismatched quotes."
    }
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Contains("`r") -or $value.Contains("`n") -or $value.Contains([char]0)) { Stop-Poc "Root .env value $Name is empty or malformed." }
    return $value
}

function Require-Command([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { Stop-Poc "Required tool '$Name' is unavailable on PATH." }
}

function Get-ArtifactSha256([string] $Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Test-TcpPort([int] $Port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try { $task = $client.ConnectAsync('127.0.0.1', $Port); return $task.Wait(700) -and $client.Connected }
    catch { return $false }
    finally { $client.Dispose() }
}

function Wait-TcpPort([int] $Port, [int] $Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-TcpPort $Port) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Invoke-Docker([string[]] $Arguments) {
    if ($Arguments | Where-Object { $_ -notmatch '^[A-Za-z0-9_.:/{}=-]+$' }) { Stop-Poc 'A Docker argument contains unexpected shell characters.' }
    $commandLine = 'docker.exe ' + ($Arguments -join ' ')
    $output = & $env:ComSpec /d /s /c $commandLine 2>$null
    if ($LASTEXITCODE -ne 0) { Stop-Poc 'A read-only Docker/POC database check failed.' }
    return $output
}

function Invoke-DockerSql([string] $Sql) {
    $dockerSqlCommand = "docker.exe exec -i cinekros-postgres psql -X -qAt -v ON_ERROR_STOP=1 -U cinekros -d $databaseName -f -"
    $output = $Sql | & $env:ComSpec /d /s /c $dockerSqlCommand 2>$null
    if ($LASTEXITCODE -ne 0) { Stop-Poc 'A read-only POC database readiness check failed.' }
    return $output
}

foreach ($tool in @('docker.exe', 'dotnet.exe', 'node.exe', 'npm.cmd')) { Require-Command $tool }
if (-not (Test-Path -LiteralPath (Join-Path $frontendDir 'node_modules\vite\bin\vite.js') -PathType Leaf)) { Stop-Poc 'Frontend dependencies are not already installed; no installation was attempted.' }

$postgresPassword = Get-LocalValue 'CINEKROS_POSTGRES_PASSWORD'
$geminiKey = Get-LocalValue 'GEMINI_API_KEY'
$escapedPassword = $postgresPassword.Replace('"', '""')
$env:DATABASE_CONNECTION_STRING = "Host=127.0.0.1;Port=5433;Database=$databaseName;Username=cinekros;Password=`"$escapedPassword`";Options=-c default_transaction_read_only=on"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:CINEKROS_RECOMMENDATION_MODE = 'real'
$env:CINEKROS_SERBIAN_POC = 'true'
$env:CINEKROS_POSTGRES_PASSWORD = $postgresPassword
$env:GEMINI_API_KEY = $geminiKey
$env:CINEKROS_E5_MODEL_DIR = $modelDir
$env:VITE_API_BASE_URL = 'http://127.0.0.1:5179'
$env:CINEKROS_ALLOWED_ORIGINS = 'http://localhost:5173,http://127.0.0.1:5173'

if (-not (Test-Path -LiteralPath (Join-Path $backendDir 'CineKros.Api.csproj') -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $frontendDir 'package.json') -PathType Leaf)) { Stop-Poc 'Expected backend or frontend project files are missing.' }
if (-not (Test-Path -LiteralPath $modelDir -PathType Container)) { Stop-Poc "Locked multilingual model directory is missing: $modelDir" }
$artifactPins = @{
    'model_qint8_avx512_vnni.onnx' = '2523551878658b305550d8759443822dbfda9ed9c8012ef2c354ba2c5b9de503'
    'tokenizer.json' = '62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626'
    'config.json' = '4c27930e59106027abab56f7531c1fa6b14bbf31e8229ec36d68affa4e869bcd'
    'tokenizer_config.json' = 'efb5c0d09722e5fe59a462cd2a9976ee216d55b037597d997cd3fe833216da15'
    'special_tokens_map.json' = '06e405a36dfe4b9604f484f6a1e619af1a7f7d09e34a8555eb0b77b66318067f'
}
foreach ($name in $artifactPins.Keys) {
    $path = Join-Path $modelDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-ArtifactSha256 $path) -ne $artifactPins[$name]) { Stop-Poc "Locked multilingual artifact validation failed: $name" }
}

try { Invoke-Docker @('info') | Out-Null } catch { Stop-Poc 'Docker engine is unavailable. Start Docker manually, then run this launcher again.' }
$containerExists = Invoke-Docker @('container', 'inspect', 'cinekros-postgres', '--format', '{{.State.Status}}')
if ($containerExists -notcontains 'running') {
    if ($containerExists -notcontains 'exited' -and $containerExists -notcontains 'created') { Stop-Poc 'The existing cinekros-postgres container is missing or in an unsupported state.' }
    Invoke-Docker @('start', 'cinekros-postgres') | Out-Null
}
$healthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $health = Invoke-Docker @('inspect', 'cinekros-postgres', '--format', '{{.State.Health.Status}}')
    if ($health -contains 'healthy') { $healthy = $true; break }
    if ($health -contains 'unhealthy') { Stop-Poc 'Existing cinekros-postgres container is unhealthy.' }
    Start-Sleep -Seconds 2
}
if (-not $healthy) { Stop-Poc 'Existing cinekros-postgres container did not become healthy within 120 seconds.' }
if (-not (Wait-TcpPort 5433 5)) { Stop-Poc 'Expected PostgreSQL port 5433 is not open on 127.0.0.1.' }

$readinessSql = @"
BEGIN READ ONLY;
SELECT current_database() = '$databaseName'
 AND (SELECT count(*) FROM movies) = 150
 AND (SELECT count(*) FROM movie_embeddings) = 150
 AND (SELECT count(*) FROM movie_embeddings WHERE embedding IS NOT NULL AND embedding_sr IS NOT NULL AND vector_dims(embedding)=768 AND vector_dims(embedding_sr)=768 AND document_fingerprint ~ '^[0-9a-f]{64}$' AND document_fingerprint_sr ~ '^[0-9a-f]{64}$') = 150
 AND EXISTS (SELECT 1 FROM catalog_import_state WHERE id=1 AND catalog_version='B05a-bilingual-catalog-v2' AND btrim(catalog_jsonl_sha256)='2887a937689294b029dd918911dd10151bfd3ece74fa573fcc5d85dc2f86886e' AND btrim(catalog_content_fingerprint)='0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66' AND movie_count=150)
 AND EXISTS (SELECT 1 FROM embedding_set_state WHERE language='en' AND btrim(profile_fingerprint)='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' AND btrim(catalog_content_fingerprint)='0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66' AND btrim(corpus_sha256)='5c5760576471547ea4d6db17acfd254ff1a0d4e7c47fe54b0f4db6a9795fdde0' AND text_format_version='en-title-year-director-cast-tags-v1' AND btrim(artifact_sha256)='5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81' AND btrim(coalesce(translation_dictionary_sha256,''))='' AND dimension=768 AND embedded_count=150)
 AND EXISTS (SELECT 1 FROM embedding_set_state WHERE language='sr' AND btrim(profile_fingerprint)='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' AND btrim(catalog_content_fingerprint)='0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66' AND btrim(corpus_sha256)='fc26b53581e6adc795573df511b7e7f72c7fd59fe7e31efddb29e0ebf9d7e8c0' AND text_format_version='sr-title-year-director-cast-tags-latn-v1' AND btrim(artifact_sha256)='228493dfeabf83dd5b3d5b0b9cf6c445ebc48742e9cf8f810bc8517959d7baba' AND btrim(translation_dictionary_sha256)='09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e' AND dimension=768 AND embedded_count=150)
 AND (SELECT count(*) FROM embedding_set_state WHERE language IN ('en','sr')) = 2;
ROLLBACK;
"@
$dbResult = Invoke-DockerSql $readinessSql
if ($dbResult -notcontains 't') { Stop-Poc 'Corrected POC v2 readiness mismatch: check the exact database, catalog identity, EN/SR corpus/artifact/dictionary/profile pins and 150-row/768D completeness. No old or production database fallback is allowed.' }

if (-not (Test-Path -LiteralPath (Join-Path $frontendDir 'node_modules\.bin\vite.cmd') -PathType Leaf)) { Stop-Poc 'Local Vite executable is missing.' }
if ((Test-TcpPort 5179)) { Stop-Poc 'Port 5179 is occupied; close the unknown listener before launching.' }
if ((Test-TcpPort 5173)) { Stop-Poc 'Port 5173 is occupied; close the unknown listener before launching.' }
if (-not $CheckOnly) {
    $backendCommand = "cd /d `"$backendDir`" && title CineKros POC Backend && dotnet run --no-launch-profile --urls http://127.0.0.1:5179"
    Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/k', $backendCommand) | Out-Null
    if (-not (Wait-TcpPort 5179 120)) { Stop-Poc 'Backend did not open port 5179 within 120 seconds; inspect its visible window.' }
    $frontendCommand = "cd /d `"$frontendDir`" && title CineKros POC Frontend && npm run dev -- --host 127.0.0.1 --port 5173 --strictPort"
    Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/k', $frontendCommand) | Out-Null
    if (-not (Wait-TcpPort 5173 60)) { Stop-Poc 'Frontend did not open port 5173 within 60 seconds; inspect its visible window.' }
    Start-Process 'http://localhost:5173' | Out-Null
}

Write-Output "POC preflight passed: database=$databaseName movies=150 multilingual-profile=multilingual-e5-base-int8-onnx-v1 model=$modelRevision Gemini=present backend=http://127.0.0.1:5179 UI=http://localhost:5173"
