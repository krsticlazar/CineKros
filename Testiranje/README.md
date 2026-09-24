# Research Evidence and Reproducibility

This directory is the readable evidence area for the thesis. It does not imply any experiments have run.

| Directory | Content policy |
| --- | --- |
| `logs/` | Raw logs stay local/ignored; only the README is tracked |
| `fixtures/` | Small reviewed synthetic/sanitized API and provider fixtures |
| `experiments/` | Reviewed protocol, query/configuration definitions, seeds and run instructions |
| `results/` | Small reviewed evaluation tables; large/raw output stays in local data |
| `reports/` | Human-readable findings, limitations, and verification summaries |

MovieLens Tag Genome 2021 was confirmed by Lazar on 2026-09-24. Evaluation methodology is still TODO after consultation. Automatic-only evaluation, 60 queries, a 20/40 split, Tag-proxy nDCG@10, assessors and a third diagnostic configuration are proposals, not frozen requirements.

At least semantic-only and hybrid configurations must eventually be compared under documented, comparable conditions. Keep ordinary tests quota-free. Live smoke tests, embedding jobs and evaluations must be deliberate and bounded.

Never commit secrets, arbitrary personal queries, raw provider logs, or bulk datasets. Store large replay data/vectors locally under `../database/data/` and commit reviewed descriptions/hashes/configuration where appropriate. CSV/JSON evidence is intentionally not blanket-ignored: inspect content and size before staging.
