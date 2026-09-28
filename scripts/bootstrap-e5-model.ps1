[CmdletBinding()]
param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$revision = 'f52bf8ec8c7124536f0efb74aca902b2995e5bcd'
$target = Join-Path (Split-Path $PSScriptRoot -Parent) "database/data/models/e5-base-v2/$revision"
$files = @{
  'model_qint8_avx512_vnni.onnx' = 'f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e'
  'tokenizer.json' = 'd241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66'
  'tokenizer_config.json' = 'ae83fa6ca0333117ff12606020af925d648667ef70d92ff7f27d781ba0ca4544'
  'special_tokens_map.json' = 'b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3'
  'vocab.txt' = '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3'
}
function Test-ArtifactSet([string]$Root) {
  foreach ($item in $files.GetEnumerator()) {
    $path = Join-Path $Root $item.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing pinned artifact: $($item.Key)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Value) { throw "Checksum mismatch: $($item.Key)" }
  }
}
if ($VerifyOnly) { Test-ArtifactSet $target; Write-Output 'Pinned E5 artifacts verified.'; exit 0 }
if (Test-Path -LiteralPath $target) { try { Test-ArtifactSet $target; Write-Output 'Pinned E5 artifacts already verified.'; exit 0 } catch { throw 'Existing model directory is incomplete or tampered; refusing to overwrite it.' } }
$parent = Split-Path $target -Parent; New-Item -ItemType Directory -Force -Path $parent | Out-Null
$temp = Join-Path $parent ('.e5-download-' + [guid]::NewGuid().ToString('N'))
try {
  foreach ($item in $files.GetEnumerator()) {
    $destination = Join-Path $temp $item.Key; New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    $upstreamPath = if ($item.Key -eq 'model_qint8_avx512_vnni.onnx') { 'onnx/model_qint8_avx512_vnni.onnx' } else { $item.Key }
    Invoke-WebRequest -Uri "https://huggingface.co/intfloat/e5-base-v2/resolve/$revision/$upstreamPath?download=true" -OutFile $destination
  }
  Test-ArtifactSet $temp; Move-Item -LiteralPath $temp -Destination $target; Test-ArtifactSet $target
  Write-Output 'Pinned E5 artifacts downloaded and verified.'
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
