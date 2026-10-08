[CmdletBinding()]
param(
  [ValidateSet('e5-base-v2-int8-onnx-v1','multilingual-e5-base-int8-onnx-v1')][string]$Profile = 'e5-base-v2-int8-onnx-v1',
  [string]$ModelRoot,
  [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$legacy = $Profile -eq 'e5-base-v2-int8-onnx-v1'
$revision = if ($legacy) { 'f52bf8ec8c7124536f0efb74aca902b2995e5bcd' } else { 'd128750597153bb5987e10b1c3493a34e5a4502a' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$root = if ($ModelRoot) { [IO.Path]::GetFullPath($ModelRoot) } else { Join-Path $repoRoot 'database/data/models' }
$modelName = if ($legacy) { 'e5-base-v2' } else { 'multilingual-e5-base' }
$target = Join-Path (Join-Path (Join-Path $root $modelName) $revision) ''
$files = if ($legacy) { @{
  'model_qint8_avx512_vnni.onnx' = 'f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e'
  'tokenizer.json' = 'd241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66'
  'tokenizer_config.json' = 'ae83fa6ca0333117ff12606020af925d648667ef70d92ff7f27d781ba0ca4544'
  'special_tokens_map.json' = 'b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3'
  'vocab.txt' = '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3'
} } else { @{
  'model_qint8_avx512_vnni.onnx' = '2523551878658b305550d8759443822dbfda9ed9c8012ef2c354ba2c5b9de503'
  'tokenizer.json' = '62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626'
  'config.json' = '4c27930e59106027abab56f7531c1fa6b14bbf31e8229ec36d68affa4e869bcd'
  'tokenizer_config.json' = 'efb5c0d09722e5fe59a462cd2a9976ee216d55b037597d997cd3fe833216da15'
  'special_tokens_map.json' = '06e405a36dfe4b9604f484f6a1e619af1a7f7d09e34a8555eb0b77b66318067f'
} }
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
    $upstreamPath = if ($legacy) {
      if ($item.Key -eq 'model_qint8_avx512_vnni.onnx') { 'onnx/model_qint8_avx512_vnni.onnx' } else { $item.Key }
    } else { "onnx/$($item.Key)" }
    $modelId = if ($legacy) { 'e5-base-v2' } else { 'multilingual-e5-base' }
    Invoke-WebRequest -Uri "https://huggingface.co/intfloat/$modelId/resolve/$revision/$upstreamPath?download=true" -OutFile $destination
  }
  Test-ArtifactSet $temp; Move-Item -LiteralPath $temp -Destination $target; Test-ArtifactSet $target
  Write-Output 'Pinned E5 artifacts downloaded and verified.'
} finally {
  if (Test-Path -LiteralPath $temp) {
    $resolvedTemp = [IO.Path]::GetFullPath($temp)
    $resolvedParent = [IO.Path]::GetFullPath($parent)
    if ([IO.Path]::GetDirectoryName($resolvedTemp) -ne $resolvedParent -or -not [IO.Path]::GetFileName($resolvedTemp).StartsWith('.e5-download-', [StringComparison]::Ordinal)) { throw 'Refusing unsafe staging cleanup.' }
    Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
  }
}
