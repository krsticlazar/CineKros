# Local Workflow Scripts

The active `bootstrap-e5-model.ps1` script explicitly downloads or verifies the pinned local E5 ONNX/tokenizer artifact. It lives here because both offline ETL and backend runtime share that repository-level model cache and contract. It never runs as part of normal application startup.

Workflow scripts must document prerequisites, inputs, outputs and external API usage. Installation, downloads, live provider requests and publication must be explicit actions, not hidden setup side effects.
