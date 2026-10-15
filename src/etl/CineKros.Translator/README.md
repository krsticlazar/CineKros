# CineKros.Translator

Local ETL tool for translating catalog tags from English to Serbian Latin. The .NET CLI handles JSON/JSONL extraction, checkpoints, normalization, QA, review, hashing, and atomic publication. A child Python process performs CPU inference with the pinned Marian model; there is no API, database, or live provider integration.

The runtime is pinned to `Helsinki-NLP/opus-mt-en-sla` revision `0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8`. Bootstrap requires Python 3.12.14 and explicitly downloads/verifies the locked runtime and model into local paths. Existing unverifiable paths are not overwritten. Model/runtime files remain local.

Commands accept unique `--name value` pairs and absolute paths. Main workflow inputs and output locations are:

```text
extract-tags --catalog <catalog.jsonl> --output-dir <new-dir>
propose --tags <tags.json> --python <venv-python> --model-dir <pinned-model-dir> --checkpoint <checkpoint.jsonl> --output-dir <new-dir> [--max-items <count>]
qa --dictionary <dictionary.json> --output-dir <new-dir>
apply-review --dictionary <dictionary.json> --review <review.json> --output-dir <new-dir>
lock --dictionary <dictionary.json> --source-tags <tags.json> --output-dir <new-dir>
lock-full --candidate <candidate.json> --baseline <baseline.json> --proposals <proposals.json> --source-tags <tags.json> --catalog <catalog.jsonl> --review <review.json> --output-dir <new-release-dir>
```

`propose` resumes only compatible checkpoint rows; Ctrl+C preserves completed rows. The ordinary proposal limit remains conservative; `--max-items` must be explicitly selected for larger authorized batches. `lock-full` is a separately validated release path requiring the frozen candidate/baseline/proposal inputs and an explicit hash-bound review; it writes a new immutable release and does not update the source catalog.

```powershell
dotnet test --project tests/CineKros.Translator.Tests/CineKros.Translator.Tests.csproj
```

Tests use synthetic fixtures and fakes. They do not download or invoke the model; any inference smoke is a separate explicit local operation.
