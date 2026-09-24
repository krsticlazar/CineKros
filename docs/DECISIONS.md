# Technical Decisions

Current engineering baseline; implementation is not yet present. Model versions and data-dependent details remain unresolved.

| Area | Decision | Rationale |
| --- | --- | --- |
| Interaction | Stateless request/response | Movie search does not depend on conversation history |
| Stack | React/TypeScript/Vite, ASP.NET Core/C#, PostgreSQL/pgvector, Docker | Separate presentation, orchestration and catalog storage |
| Dataset | MovieLens Tag Genome 2021 only | One source for research descriptors and metadata |
| Parsing | Gemini structured output plus backend validation | The model interprets constraints; application code enforces them |
| Retrieval | Mandatory predicates, then exact cosine ranking | Similarity never overrides a hard constraint |
| Soft preferences | Remain in semantic text | Avoid invented numerical cutoffs |
| Missing metadata | Fails an active hard filter | Unknown values do not prove eligibility |
| Result count | Up to ten distinct films; partial notice for 1–9 | Avoid padding or relaxed constraints |
| Embeddings | Offline film vectors; compatible runtime query vector | Avoid repeated generation and mixed vector spaces |
| Enrichment | Cached TMDB metadata/posters; IMDb outbound links | Keep enrichment outside the recommendation request |
| Runtime data | Compact derived catalog | Exclude bulk supporting reviews, ratings and scores |
| Verification | Provider interfaces and synthetic fixtures | Check behavior without consuming live quotas |
| Interface | SR/EN and compact HTTP DTOs | Separate presentation from internal provider/storage data |

glmer/tagdl selection, supported filters, actor/director matching, semantic text and model/dimensions require documented data exploration before implementation. No similarity threshold or approximate index is enabled by default.

Academic evaluation compares semantic-only and hybrid retrieval. Its detailed protocol awaits academic confirmation; no query count, split or relevance metric is finalized.
