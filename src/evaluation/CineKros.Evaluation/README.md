# CineKros.Evaluation

Standalone CLI for repeatable parser and retrieval checks. It reads a query-set JSON file and writes JSON/Markdown reports. Retrieval uses the legacy E5 constructor and non-language search overload, so this harness is not a full bilingual V5 production evaluation and makes no promise to evaluate the full production database. Reports describe only the selected cases and actual configured dataset; they are not, by themselves, academic evaluation results.

```powershell
dotnet run --project src/evaluation/CineKros.Evaluation -- `
  --queries <query-set.json> --output <report-base> --mode retrieval --case-ids <comma-separated-ids>
```

The command writes `<report-base>.json` and `<report-base>.md`.

Retrieval mode requires explicit case IDs, `DATABASE_CONNECTION_STRING`, and `CINEKROS_E5_MODEL_DIR`; it queries the configured catalog and local E5 runtime. Optional `--judgments <file>` supplies human relevance judgments. It is a read/search path and writes reports only.

Parser mode makes live Gemini requests. It requires `--mode parser --live-parser --max-live-calls <positive-count>` and `GEMINI_API_KEY`; this cap limits provider calls. It cannot be run in a fixture-only mode. Keep methodology, query selection, human scoring, and interpretation separate from the tool output.
