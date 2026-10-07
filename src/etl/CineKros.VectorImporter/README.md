# CineKros.VectorImporter

## Uloga

Ovaj CLI proverava i uvozi kompletan E5 document-vector artefakt u PostgreSQL `movie_embeddings`. Ne računa embeddings i ne prihvata parcijalni artefakt kao konačan.

## Mesto u toku

`[E5.Generator](../CineKros.E5.Generator/README.md) → artefakt + katalog → VectorImporter → movie_embeddings → API pretraga`

Pre uvoza kataloga, pokreni [Database.Migrator](../CineKros.Database.Migrator/README.md); kataloge uvozi [Catalog.Importer](../CineKros.Catalog.Importer/README.md). API poredi profil i pretražuje vektore, ne generiše ih.

## Kako čitati kod

- [Program.cs](Program.cs) zahteva `--catalog` i `--artifact`, učitava artefakt kroz validator i koristi `DATABASE_CONNECTION_STRING`.
- [VectorArtifact.cs](VectorArtifact.cs) proverava manifest, broj, dimenziju i sadržaj vektora.
- [VectorImporter.cs](VectorImporter.cs) proverava da identiteti odgovaraju već uvezenom katalogu, zatim ih upisuje transakciono i beleži profil. Identično ponavljanje služi za verifikaciju.

Ove provere čuvaju vezu između kataloga i embedding profila koju runtime pretraga očekuje.
