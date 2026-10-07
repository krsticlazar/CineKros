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
