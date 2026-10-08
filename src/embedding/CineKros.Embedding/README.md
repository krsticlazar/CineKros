# CineKros.Embedding

## Role

Local .NET inference for locked E5 ONNX profiles. The default constructor remains the legacy `e5-base-v2-int8-onnx-v1` profile and preserves its historical batching, text formatting, and fingerprints. The explicit `multilingual-e5-base-int8-onnx-v1` profile is opt-in only; it uses the locked XLM-R tokenizer, NFC normalization, and one unpadded graph sequence per inference call. It does not call Gemini or a database.

## Callers

- [E5.Generator](../../etl/CineKros.E5.Generator/README.md) poziva document putanju pri offline izgradnji kataloških vektora.
- API koristi query putanju pri runtime pretrazi; evaluation alat je koristi pri merenju retrieval režima. Oba toka moraju odgovarati istom profilu i fingerprint-u.

## Contracts

- [E5EmbeddingModel.cs](E5EmbeddingModel.cs) proverava kontrolne sume modela i tokenizer-a, zatim inicijalizuje ONNX inferenciju.
- `EmbedDocument` / `EmbedDocuments` use the passage prefix; `EmbedQuery` uses the query prefix. Both profiles preserve the end token when truncating at 512 tokens and apply masked mean pooling plus L2 normalization.
- Target-profile collections remain source-compatible but execute one document at a time, in order, with batch size one and no padding. The legacy default keeps its original batched execution.
- `ProfileFingerprint` identifies the immutable profile descriptor, including the target inference-shape policy. `ComputeDocumentFingerprint` preserves the legacy payload and uses NFC-normalized target text.

Separate query and passage paths are part of the E5 contract. Production activation remains a separate application decision; this library only exposes explicit profile construction.
