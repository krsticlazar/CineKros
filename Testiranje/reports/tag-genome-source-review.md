# Tag Genome 2021 Source Review

Reviewed: 2026-09-24. This is a source-data inspection, not the academic recommendation evaluation. The raw MovieLens Tag Genome 2021 files remain under the ignored `database/data/raw/tag-genome-2021/` directory. Counts were derived from read-only streaming/JSONL inspection; large-file SHA-256 fingerprints below were recorded in the local extraction manifest and not recomputed during this review.

| Raw file | Records/rows | Distinct IDs / notes | Manifest SHA-256 |
| --- | ---: | --- | --- |
| `raw/metadata.json` | 84,661 | 84,661 unique `item_id` | `F2997F2E42D5A4E37E659F3813AD69D8DB6BA1E56249D1D6BF26AFEB5D66F22` |
| `raw/metadata_updated.json` | 84,661 | Same IDs and six common field values as `metadata.json`; omits `dateAdded` | `C40137F30CC167271599BF9E97FC7C6B59373D0DE5C0BDF7B06EC076D555D1BE` |
| `raw/tags.json` | 1,094 | 1,094 unique exact tag names and IDs | `6B14A52EDA53077FD4053F860E2A75FBD9D2C20E6812DC3BCDF1D3E217A681DD` |
| `scores/glmer.csv` | 10,551,655 data rows | 9,734 movies × 1,084 tags minus one pair | `6E34F84574DCC48ED67AF627206C1C27543173B249B4CED71AD694FCD2771054` |
| `scores/tagdl.csv` | 10,551,655 data rows | Same exact `(tag,item_id)` keys and order as Glmer | `8A890633BD43D16FD3092F34FA68B284ABEA47C861355A4F2E213BE061C1348A` |

Both CSVs have header `tag,item_id,score`, valid parsed values for every physical data row, and no duplicate movie/tag pair. The Cartesian product is 10,551,656; the sole absent pair in both files is `item_id=1`, tag `airplane`. The source README states 10,551,656 scores per file, one above the observed rows. No score was repaired or imputed.

The two score files cover the same 9,734 movie IDs and 1,084 exact tag names. Of these movie IDs, 9,730 have metadata; four do not. All 9,730 matched metadata rows have a nonempty IMDb ID, title and numeric `avgRating`; no exact IMDb ID is duplicated among them. Across all 84,661 metadata records, IMDb IDs are digit strings and average rating spans 0–5. Ten names in `tags.json` have no score rows. Among the matched scored movies, `directedBy` is exactly empty twice and whitespace-only once; `starring` is exactly empty 79 times.

A second read-only profile of the 9,730-record intersection found that 9,728 titles end directly in ` (YYYY)` and the two exceptions (`item_id` 89045 and 92214) have a trailing space after that suffix. All 9,730 suffix years lie in 1878–2026 after outer trimming. Every IMDb ID is exactly seven ASCII digits, 8,771 begin with zero, and no exact or numeric-equivalent collision was found. Matched `avgRating` values range from 0.88716 to 4.41985 on the MovieLens 0–5 scale. Person-name delimiter and normalization rules are still undecided; raw string diagnostics do not establish that comma splitting is always valid.

Glmer scores observed: minimum `0.00000617470550645649`, maximum `1`, no values outside 0–1. TagDL scores observed: minimum `-0.07844761`, maximum `1.1395855`, with 1,021,804 values below zero and 14,845 above one. Raw-score top-10 tag sets overlap by a mean of `5.331724` tags per movie across all 9,734 scored movies (minimum 0, maximum 10). This is a diagnostic comparison, not a relevance score.

The dataset's `predictions/performance_results_tenfolds.txt` reports ten author-run tag-prediction folds. Mean MAE is `0.846348` for TagDL and `0.876049` for Glmer; TagDL has lower MAE in all ten folds. These are results published with the source for tag prediction, not an independent evaluation of CineKros movie recommendations.

The [B1 engineering decision](../../docs/DECISIONS.md) selects `metadata_updated.json`, raw-score TagDL ordering and the 9,730-record source intersection for the initial catalogue. The four score-only IDs and absent pair are recorded as source anomalies. Later ETL must retain exact input hashes, explicit exclusion reasons and a reproducible field mapping. Semantic-text tag count, TMDB enrichment and final runtime catalogue size remain separate decisions.
