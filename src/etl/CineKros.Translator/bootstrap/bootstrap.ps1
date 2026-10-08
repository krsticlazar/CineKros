[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PythonPath,
    [Parameter(Mandatory = $true)][string]$RuntimePath,
    [Parameter(Mandatory = $true)][string]$ModelPath
)

$ErrorActionPreference = 'Stop'
foreach ($path in @($PythonPath, $RuntimePath, $ModelPath)) {
    if (-not [System.IO.Path]::IsPathFullyQualified($path)) {
        throw 'PythonPath, RuntimePath and ModelPath must be absolute paths.'
    }
}
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw 'The selected Python interpreter does not exist.'
}
$pythonInfo = & $PythonPath -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')"
if ($LASTEXITCODE -ne 0 -or -not $pythonInfo.StartsWith('3.12.', [StringComparison]::Ordinal)) {
    throw 'The translator requires Python 3.12.x.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $projectRoot 'locks\requirements.lock'
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw 'The complete hash-locked Python requirements file is missing.'
}
$lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeFullPath = [System.IO.Path]::GetFullPath($RuntimePath)
$pythonInVenv = Join-Path $runtimeFullPath 'Scripts\python.exe'
$markerPath = Join-Path $runtimeFullPath 'runtime-manifest.json'
$ownerPath = Join-Path $runtimeFullPath '.translator-bootstrap-owner'
$ownerMarker = 'CineKros offline translator runtime v1'
if (Test-Path -LiteralPath $runtimeFullPath) {
    if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf) -or (Get-Content -Raw -LiteralPath $ownerPath).Trim() -ne $ownerMarker) {
        throw 'The requested runtime path already exists without a verifiable translator manifest; refusing to modify it.'
    }
} else {
    New-Item -ItemType Directory -Path (Split-Path -Parent $runtimeFullPath) -Force | Out-Null
    New-Item -ItemType Directory -Path $runtimeFullPath | Out-Null
    [System.IO.File]::WriteAllText($ownerPath, $ownerMarker + "`n", [System.Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path -LiteralPath $pythonInVenv -PathType Leaf)) {
    & $PythonPath -m venv $runtimeFullPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Python virtual environment.' }
}
$existing = $null
if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    $existing = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
    if ($existing.runtimeLockSha256 -ne $lockHash -or $existing.pythonVersion -ne $pythonInfo) {
        throw 'The existing isolated runtime does not match this locked environment; refusing to overwrite it.'
    }
} else {
    & $pythonInVenv -m pip install --disable-pip-version-check --require-hashes --no-deps --only-binary=:all: --index-url https://pypi.org/simple --extra-index-url https://download.pytorch.org/whl/cpu -r $lockPath
    if ($LASTEXITCODE -ne 0) { throw 'The hash-locked offline translator dependencies could not be installed.' }
}

& $pythonInVenv -m pip check
if ($LASTEXITCODE -ne 0) { throw 'The isolated Python dependency graph is inconsistent.' }
$validatorPath = Join-Path $projectRoot 'bootstrap\validate_runtime.py'
$validationOutput = (& $pythonInVenv -I $validatorPath --lock $lockPath --expected-python $pythonInfo) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'The installed Python packages do not exactly match the complete runtime lock.' }
$validated = $validationOutput | ConvertFrom-Json
if ($existing -and $existing.runtimeFingerprint -and $existing.runtimeFingerprint -ne $validated.runtimeFingerprint) {
    throw 'The existing isolated runtime changed after bootstrap; refusing to bless it.'
}
[System.IO.File]::WriteAllText($markerPath, $validationOutput.Trim() + "`n", [System.Text.UTF8Encoding]::new($false))

$downloadScript = Join-Path $projectRoot 'bootstrap\download_model.py'
$modelFullPath = [System.IO.Path]::GetFullPath($ModelPath)
$stageFolders = @(Get-ChildItem -LiteralPath (Split-Path -Parent $modelFullPath) -Directory -Filter '.opus-mt-stage-*' -ErrorAction SilentlyContinue)
if ($stageFolders.Count -gt 1) { throw 'More than one incomplete OPUS stage exists; refusing to guess which one to resume.' }
if ($stageFolders.Count -eq 1) {
    & $pythonInVenv $downloadScript --model-dir $modelFullPath --resume-stage $stageFolders[0].FullName
} else {
    & $pythonInVenv $downloadScript --model-dir $modelFullPath
}
if ($LASTEXITCODE -ne 0) { throw 'Pinned OPUS model setup did not complete successfully.' }
Write-Output "Translator runtime ready: Python $pythonInfo; runtime lock $lockHash"
