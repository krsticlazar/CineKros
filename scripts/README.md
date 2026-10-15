# Local Workflow Scripts

`bootstrap-e5-model.ps1` explicitly downloads or verifies a pinned local ONNX model and tokenizer. It is not part of application startup. Requires PowerShell and network access for an initial download; `-VerifyOnly` checks an existing cache without downloading. `-ModelRoot` selects the shared local model cache.

The default profile remains the legacy English `e5-base-v2-int8-onnx-v1`. To bootstrap or verify the current multilingual profile, explicitly pass `-Profile multilingual-e5-base-int8-onnx-v1`:

```powershell
pwsh -File scripts/bootstrap-e5-model.ps1 -Profile multilingual-e5-base-int8-onnx-v1 -VerifyOnly
```

Each profile has pinned revisions and SHA-256 checks. Verified cache files are reused; a present but incomplete or mismatched profile fails without overwrite. Run without `-VerifyOnly` only when you intend to download the selected model artifacts.
