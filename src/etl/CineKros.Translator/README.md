# CineKros offline Serbian tag translator

This project is local ETL tooling. It is never referenced by the API and performs no database or catalog writes. The .NET console owns extraction, JSONL orchestration, checkpoints, Serbian Latin normalization, QA, review, hashing, and atomic output. The Python process performs only local CPU inference with the pinned Marian model.

## Locked runtime

The only accepted model is `Helsinki-NLP/opus-mt-en-sla` at revision `0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8` (Apache-2.0). Inference uses `MarianTokenizer` and `MarianMTModel`, the `>>srp_Latn<<` token (ID 36), CPU, `do_sample=false`, `num_beams=4`, `max_new_tokens=32`, and a 512-token source limit. The prefix is prepended once. Inference is local-only; startup and inference fail if the model files do not match the pre-frozen Git/LFS identities and downloaded SHA-256 manifest.

`locks/requirements.in` records direct package pins. `locks/requirements.lock` is the generated complete transitive hash lock for the Phase 2 Windows/Python 3.12.14 runtime. The bootstrap requires exactly Python 3.12.14 and installs only wheels into the isolated venv. No package is installed in system Python. To initialize from the canonical checkout:

```powershell
& '.\src\etl\CineKros.Translator\bootstrap\bootstrap.ps1' `
  -PythonPath 'C:\path\to\python312\python.exe' `
  -RuntimePath 'D:\Repozitorijum\CineKros\.local\runtime\translator-sr-v1' `
  -ModelPath 'D:\Repozitorijum\CineKros\database\data\models\opus-mt-en-sla\0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8'
```

The bootstrap verifies the public pinned revision before downloading. It refuses to overwrite an existing model/runtime path that it cannot verify. Model binaries and the venv remain in ignored local directories; `model-manifest.json` records actual artifact SHA-256 values and expected Git object IDs.

## CLI contract

Each option is a unique `--name value` pair. Paths must be absolute. Unknown options, duplicate options, blank/invalid data, and existing output paths fail. Outputs are built in a sibling staging directory and published by one directory rename.

```text
extract-tags --catalog <absolute-jsonl> --output-dir <new-absolute-dir>
propose --tags <absolute-json-array> --python <absolute-venv-python> --model-dir <absolute-pinned-model-dir> --checkpoint <absolute-jsonl> --output-dir <new-absolute-dir> [--max-items <1..6>]
qa --dictionary <absolute-dictionary-json> --output-dir <new-absolute-dir>
apply-review --dictionary <absolute-dictionary-json> --review <absolute-review-json> --output-dir <new-absolute-dir>
lock --dictionary <absolute-dictionary-json> --source-tags <absolute-json-array> --output-dir <new-absolute-dir>
```

Catalog input is UTF-8 JSONL with a `relevantTags` array of objects containing string `name` values. Extraction emits ordinal unique source tags and a deterministic source hash. Proposal input is a JSON string array. The Phase 2 smoke run remains checkpoint-compatible and unchanged. For the explicitly authorized Serbian Phase 3 POC only, proposal, review, and lock commands are bounded to at most 418 exact selected tags; the runner validates the supplied bounded keys and does not perform whole-catalog translation implicitly. Never use this allowance to translate the full production tag vocabulary.

`propose` appends each completed row only after validating its JSONL response and includes model/revision/target/decoding/runtime-lock/model-artifact/normalizer identity in the checkpoint header and every row. Compatible completed rows resume; failed rows retry; incompatible rows are not reused by English key. A truncated final record is dropped before appending. A corrupt header is backed up and only per-row identity-verified completed rows are retained. Cancellation kills only the child process tree started for that command.

The dictionary root and entry property order are fixed. The proposal output keeps the raw `machine` value and a separately normalized `sr` value. `apply-review` takes an explicit `decisions` array (`en`, `approvedSr`); it preserves `machine`, marks accepted values as `reviewed`, and sets `manualOverride` only when reviewed text differs. `lock` requires exact keys, nonblank normalized values and no unresolved entries, and emits canonical content hashing without timestamps or paths.

QA is versioned `tag-translation-qa-v1`, with versioned lists: the unchanged-English whitelist is `cgi`, `imax`, `pixar`, `disney`, `3d`, `2d`, `dvd`, `vhs`, `noir`, `imdb`; ambiguity seeds are `camp`, `gritty`, `quirky`, `twist`, `feel-good`, `mind-bending`. It reports eight review flags: empty/incomplete output, unchanged English outside the whitelist, excessive normalized length, non-Latin residue, raw controls/special tokens/malformed punctuation or the narrowly recognized `è`/`æ` encoding residue, Serbian collisions, possible lost number tokens/negation, and ambiguity seeds. Number preservation compares complete digit tokens (`7` does not match `17`). The `è`/`æ` check is a bounded translation-quality warning motivated by observed model output; it does not rewrite or transliterate the preserved machine text. A flag requests review; it does not reject or deduplicate a translation.

Ctrl+C cancels the active CLI token, allowing the owned Python process tree to terminate while completed checkpoint rows remain available for resume.

## Checks

```powershell
dotnet test --project tests/CineKros.Translator.Tests/CineKros.Translator.Tests.csproj
```

The focused suite uses only tiny fixtures and fakes. The real-model smoke is a separate explicit six-key run recorded under local planning reports; it is never part of ordinary tests.
