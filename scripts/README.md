# Local Workflow Scripts

The active `bootstrap-e5-model.ps1` script explicitly downloads or verifies the pinned local E5 ONNX/tokenizer artifact. It lives here because both offline ETL and backend runtime share that repository-level model cache and contract. It never runs as part of normal application startup.

The default remains the legacy English profile. To explicitly bootstrap or verify the selected multilingual profile, pass `-Profile multilingual-e5-base-int8-onnx-v1`; `-ModelRoot` can point at the shared model-cache root. Existing verified files are reused, and a present cache with any hash mismatch is rejected without overwrite.

Workflow scripts must document prerequisites, inputs, outputs and external API usage. Installation, downloads, live provider requests and publication must be explicit actions, not hidden setup side effects.
