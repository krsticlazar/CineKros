# CineKros evaluation harness

This local CLI reads a proposed Phase D query set and writes JSON and Markdown reports to the caller-selected output base path. It does not load `.env`, alter production search behavior, or write to the catalog.

Retrieval mode requires process environment variables `DATABASE_CONNECTION_STRING` and `CINEKROS_E5_MODEL_DIR`:

```powershell
dotnet run --project src/evaluation/CineKros.Evaluation -- --mode retrieval --queries .local/planning/evaluation/proposed_queries_v1.json --case-ids Q001,Q003 --output .local/planning/runs/retrieval-01
```

Optional human judgments are a JSON object keyed by query ID, then MovieLens ID, with integer scores from 0 to 3, for example `{"Q001":{"1":3,"2":1}}`. They remain nullable per returned movie until every result for that case is judged.

Parser mode is a separate explicit live operation. It requires `--live-parser --max-live-calls N`, a positive cap no larger than the parser-evaluable case count, and process-only `GEMINI_API_KEY`:

```powershell
dotnet run --project src/evaluation/CineKros.Evaluation -- --mode parser --queries .local/planning/evaluation/proposed_queries_v1.json --output .local/planning/runs/parser-01 --live-parser --max-live-calls 1
```

Parser mode records validated checklist and canonical hard-field comparisons; semantic-query literal equality is exploratory. Failures are stored as sanitized alert codes and are not counted as retrieval failures. Neither mode chooses a default output path.
