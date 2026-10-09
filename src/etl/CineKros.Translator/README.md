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
propose --tags <absolute-json-array> --python <absolute-venv-python> --model-dir <absolute-pinned-model-dir> --checkpoint <absolute-jsonl> --output-dir <new-absolute-dir> [--max-items <1..993>]
qa --dictionary <absolute-dictionary-json> --output-dir <new-absolute-dir>
apply-review --dictionary <absolute-dictionary-json> --review <absolute-review-json> --output-dir <new-absolute-dir>
lock --dictionary <absolute-dictionary-json> --source-tags <absolute-json-array> --output-dir <new-absolute-dir>
lock-full --candidate <absolute-raw-993-candidate> --baseline <absolute-pinned-418-baseline> --proposals <absolute-575-proposal-dictionary> --source-tags <absolute-993-array> --catalog <absolute-protected-9730-jsonl> --review <absolute-main-review-json> --output-dir <new-immutable-release-dir>
```

Catalog input is UTF-8 JSONL with a `relevantTags` array of objects containing string `name` values. Extraction emits ordinal unique source tags and a deterministic source hash. Proposal input is a JSON string array. The Phase 2 six-phrase smoke behavior remains checkpoint-compatible and unchanged. Default proposal behavior remains conservatively bounded to 418 keys. For the explicitly authorized Phase 7 dictionary development run, an explicit `--max-items 575` permits exactly the 575 missing source keys; the proposal runner rejects more than 575 in one process and never silently truncates. Lock validation accepts at most the complete 993-key canonical dictionary. Counts do not waive exact-key, checkpoint-identity, preservation, or review requirements.

`propose` appends each completed row only after validating its JSONL response and includes model/revision/target/decoding/runtime-lock/model-artifact/normalizer identity in the checkpoint header and every row. Compatible completed rows resume; failed rows retry; incompatible rows are not reused by English key. A truncated final record is dropped before appending. A corrupt header is backed up and only per-row identity-verified completed rows are retained. Cancellation kills only the child process tree started for that command.

The dictionary root and entry property order are fixed. The proposal output keeps the raw `machine` value and a separately normalized `sr` value. `apply-review` takes an explicit `decisions` array (`en`, `approvedSr`); it preserves `machine`, marks accepted values as `reviewed`, and sets `manualOverride` only when reviewed text differs. `lock` requires exact keys, nonblank normalized values and no unresolved entries, and emits canonical content hashing without timestamps or paths.

QA is versioned `tag-translation-qa-v1`, with versioned lists: the unchanged-English whitelist is `cgi`, `imax`, `pixar`, `disney`, `3d`, `2d`, `dvd`, `vhs`, `noir`, `imdb`; ambiguity seeds are `camp`, `gritty`, `quirky`, `twist`, `feel-good`, `mind-bending`. It reports eight review flags: empty/incomplete output, unchanged English outside the whitelist, excessive normalized length, non-Latin residue, raw controls/special tokens/malformed punctuation or the narrowly recognized `è`/`æ` encoding residue, Serbian collisions, possible lost number tokens/negation, and ambiguity seeds. Number preservation compares complete digit tokens (`7` does not match `17`). The `è`/`æ` check is a bounded translation-quality warning motivated by observed model output; it does not rewrite or transliterate the preserved machine text. A flag requests review; it does not reject or deduplicate a translation.

Ctrl+C cancels the active CLI token, allowing the owned Python process tree to terminate while completed checkpoint rows remain available for resume.

## Checks

```powershell
dotnet test --project tests/CineKros.Translator.Tests/CineKros.Translator.Tests.csproj
```

The focused suite uses only tiny fixtures and fakes. The real-model smoke is a separate explicit six-key run recorded under local planning reports; it is never part of ordinary tests.

## Full Phase 7 release path

lock-full is an additive release path; it does not relax ordinary lock or its strict ReadDictionary property checks. It accepts only the frozen 993-entry candidate, 418-entry baseline, 575-entry proposal, canonical source-tag set and protected catalog identities. The hash-bound MAIN review uses schema sr-phase-07-main-review-v1 and explicit per-key dispositions (corrected, accepted_unchanged, ambiguous_general, or retained_baseline) with rationale and confidence. Every initial QA/context flag must be dispositioned, and all 418 baseline keys require explicit retained_baseline decisions with their exact existing Serbian value. Baseline entry values/statuses are kept, while their complete source document and provenance extensions are copied losslessly to review-provenance.json. New proposals preserve raw machine output; approved new entries are reviewed, and unflagged/unreviewed new entries are auto_pass only when final QA is clear. Corrections that create an uncovered final QA flag stop the release.

The lock-full path has a separate strict raw reader for the full candidate: it validates the five standard entry fields and permits only the two declared Phase 6T baseline extensions, previousAccepted and correctionProvenance, on the pinned baseline rows. Those 24 original extensions remain on the corresponding final dictionary entries and are also retained in the baseline provenance sidecar. No arbitrary unknown property is accepted. The ordinary strict ReadDictionary remains unchanged and will reject those extension-bearing entries rather than silently dropping them.

The full path writes only a new immutable release directory containing the standard five-field dictionary, its content hash, review provenance, QA report, deterministic manifest and an in-memory catalog compatibility report. Compatibility validation visits all 9,730 source movies in source order and maps each selected English tag to Serbian while retaining the original English key, movie metadata and selected-tag score; no catalog or bilingual catalog is written, and genres are not incorporated into Serbian tag text. Phase 8 remains a separate task.
