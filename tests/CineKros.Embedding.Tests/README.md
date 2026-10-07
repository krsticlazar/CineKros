# CineKros.Embedding.Tests

## Čemu služi

Unit i lokalni model testovi za `CineKros.Embedding`. Proveravaju da učitavanje, tokenizacija i E5 format daju stabilan profil, a da pooling i normalizacija proizvode odgovarajuće vektore. Proveravaju i odbijanje izmenjenih model artefakata.

## Gde se uklapa u CineKros

`CineKros.Embedding → CineKros.Embedding.Tests`. Test projekat direktno referencira samo embedding biblioteku; ne treba mu Gemini ni PostgreSQL.

## Najvažniji delovi

- `E5EmbeddingTests.cs` — pinned asset/fingerprint provere, masking pri pooling-u, L2 normalizacija i ONNX izvršavanje u testnom profilu.

## Šta bih rekao profesorki

> „Ovi testovi proveravaju samu matematičko-tehničku granicu modela: isti model i tokenizer moraju dati kompatibilne, normalizovane vektore, a ne samo broj od 768 vrednosti.“
