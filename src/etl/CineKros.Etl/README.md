# CineKros.Etl

## Uloga

Offline komandni alat priprema proverljive kataloge; nije deo obrade korisničkog zahteva. Polazi od lokalnih MovieLens/TagDL podataka, gradi semantički tekst, spaja odobrene TMDB detalje i objavljuje kataloge sa manifestima. TMDB se koristi samo u zasebnom `enrich-v2` toku.

## Tok podataka

`metadata i TagDL → metadata/semantic export → enrich-v2 → catalog-merge → finalni katalog`

Finalni katalog dalje koriste [Catalog.Importer](../CineKros.Catalog.Importer/README.md) za strukturisane podatke i [E5.Generator](../CineKros.E5.Generator/README.md) za offline film-vektore. Ovaj projekat ih ne poziva direktno.

## Kako čitati kod

- [Program.cs](Program.cs) razvrstava CLI komande i odbija zastareli `real-embed` režim. `fake-embed` služi samo za determinističke testne vektore.
- [MetadataExporter.cs](MetadataExporter.cs) proverava očekivane ulaze i broj redova, spaja odobreni podskup i piše JSONL sa manifestom.
- [SemanticExporter.cs](SemanticExporter.cs) dodaje odabrane TagDL signale u `semanticText`; [EnrichmentV2Runner.cs](EnrichmentV2Runner.cs) obogaćuje postojeće zapise i cache izvorima predviđenim za offline obradu.
- [CombinedCatalogExporter.cs](CombinedCatalogExporter.cs) proverava kompatibilnost i sastavlja finalni katalog.
- [RealDocumentEmbeddingRunner.cs](RealDocumentEmbeddingRunner.cs) i dalje čuva istorijski Gemini tok radi reproduktivnosti; CLI ga ne pokreće. Aktuelne dokument-vektore pravi E5 generator.

ETL unapred pravi verzionisane ulaze za bazu i pretragu. Backend pri korisničkom upitu ne zove TMDB, niti računa filmske vektore.

## Serbian search POC catalog tools

These additive commands are bounded to the frozen 150-movie / 418-tag Serbian POC. They validate the immutable B05a source identity, require absolute paths, and publish only into a new, absent output directory.

```powershell
dotnet run --project src/etl/CineKros.Etl/CineKros.Etl.csproj -- sr-poc-select --catalog <absolute-B05a-movies-catalog.jsonl> --output-dir <new-absolute-selection-dir>
dotnet run --project src/etl/CineKros.Etl/CineKros.Etl.csproj -- sr-poc-build --catalog <absolute-immutable-B05a-movies-catalog.jsonl> --dictionary <absolute-locked-418-tag-dictionary.json> --output-dir <new-absolute-catalog-dir>
dotnet run --project src/etl/CineKros.Etl/CineKros.Etl.csproj -- sr-poc-context --selected-source <absolute-selected-source.jsonl> --qa-report <absolute-qa-report.json> --output <new-absolute-context-packet.json>
```

`sr-poc-select` reads the canonical source and writes the deterministic selected source, tags, and selection manifest. `sr-poc-context` emits context for QA-flagged translation entries only. `sr-poc-build` validates the exact accepted POC dictionary and writes the bilingual 19-field catalog plus hash manifest. These are file-only preparation tools: they do not import into production or any database, generate vectors, call APIs/providers, or run Gemini. Outputs are research/engineering POC artifacts, not a production dictionary or production catalog.
