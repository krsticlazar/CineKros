# CineKros.E5.Generator

## Uloga

Offline CLI računa E5 document vektor za `semanticText` svakog filma u finalnom katalogu, lokalnim ONNX modelom. Čuva checkpoint i ponovo koristi samo kompatibilne rezultate; tek potpun skup objavljuje kao artefakt. Ne poziva Gemini.

## Mesto u toku

`[CineKros.Etl](../CineKros.Etl/README.md) → katalog → E5.Generator → artefakt → [VectorImporter](../CineKros.VectorImporter/README.md) → PostgreSQL`

[CineKros.Embedding](../../embedding/CineKros.Embedding/README.md) izvršava zajednički E5 profil; [Catalog.Importer](../CineKros.Catalog.Importer/README.md) obezbeđuje validaciju istog kataloga.

## Kako čitati kod

- [Program.cs](Program.cs) povezuje parsiranje opcija, `E5EmbeddingModel` i generator.
- `GeneratorArguments.Parse` u [GeneratorArguments.cs](GeneratorArguments.cs) definiše CLI ulaze: katalog, manifest, lokalni model, checkpoint, izlaz i batch veličinu.
- [DocumentVectorGenerator.cs](DocumentVectorGenerator.cs) proverava katalog, obrađuje batch-eve, nastavlja iz kompatibilnog checkpoint-a i objavljuje kompletan JSONL/manifest.
- `E5DocumentVectorSource` prilagođava `E5EmbeddingModel` interfejsu generatora.

Checkpoint smanjuje ponovljeni rad pri prekidu; manifest vezuje rezultat za ulazni katalog i E5 profil. Vektori filmova se računaju unapred, dok runtime računa samo vektor korisničkog upita.
