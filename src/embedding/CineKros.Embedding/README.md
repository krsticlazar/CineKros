# CineKros.Embedding

## Uloga

.NET biblioteka koja lokalno izvršava pinovani E5-base-v2 INT8 ONNX model. Daje 768-dimenzionalne, normalizovane vektore za tekst filma ili semantički deo upita. Ne poziva Gemini ni bazu.

## Pozivaoci

- [E5.Generator](../../etl/CineKros.E5.Generator/README.md) poziva document putanju pri offline izgradnji kataloških vektora.
- API koristi query putanju pri runtime pretrazi; evaluation alat je koristi pri merenju retrieval režima. Oba toka moraju odgovarati istom profilu i fingerprint-u.

## Kako čitati kod

- [E5EmbeddingModel.cs](E5EmbeddingModel.cs) proverava kontrolne sume modela i tokenizer-a, zatim inicijalizuje ONNX inferenciju.
- `EmbedDocument` / `EmbedDocuments` formatiraju tekst prefiksom `passage:`, a `EmbedQuery` prefiksom `query:`. `RunBatch` tokenizuje, skraćuje do 512 tokena, izvršava model i primenjuje maskirano usrednjavanje token-vektora (mean pooling) i L2 normalizaciju.
- `ProfileFingerprint` identifikuje konfiguraciju modela, formatiranje, dimenziju i obradu vektora; `ComputeDocumentFingerprint` identifikuje ulaz pojedinačnog filma.

Odvojene document/query putanje deo su E5 ugovora. Zbog toga API koristi ovu biblioteku za upit, dok se filmovi embed-uju samo u offline generatoru.
