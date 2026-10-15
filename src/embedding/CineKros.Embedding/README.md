# CineKros.Embedding

Local .NET ONNX inference for CineKros document and query embeddings. The library does not call Gemini or access a database. Offline tools use the document methods to prepare vectors; the API uses the query method during semantic requests.

## Profiles

`E5EmbeddingModel(string modelDirectory)` intentionally retains the historical English `e5-base-v2-int8-onnx-v1` default for source compatibility. It is not the profile selected by the current real API startup.

The real bilingual API explicitly constructs `E5EmbeddingModel` with `EmbeddingProfileDescriptor.MultilingualE5Base`. Its locked profile is:

- Model: `intfloat/multilingual-e5-base`, revision `d128750597153bb5987e10b1c3493a34e5a4502a`.
- Profile ID: `multilingual-e5-base-int8-onnx-v1`; output dimension: 768.
- ONNX artifact: `model_qint8_avx512_vnni.onnx`; tokenizer: the pinned XLM-R `tokenizer.json`.
- Query/document prefixes: `query: ` and `passage: `.
- Maximum input: 512 tokens. Inference uses the last hidden state, masked-mean pooling, then L2 normalization in float32.
- Multilingual-profile text uses Unicode NFC. Its graph uses a single unpadded sequence per inference call; document batches are processed sequentially with batch size one.

The descriptor contains pinned artifact SHA-256 values and a profile fingerprint over the model, revision, tokenizer/graph contracts, formatting, dimension, pooling, normalization, and metric. Construction validates required artifact hashes and graph inputs/outputs before inference. The API sets `CINEKROS_E5_MODEL_DIR` to the local model artifact directory and checks that the loaded profile fingerprint matches the selected descriptor.

Document and query formatting are separate parts of the embedding contract. Stored document vectors and runtime query vectors must use compatible profile fingerprints, dimensions, prefixes, tokenizer, pooling, and normalization. The API embeds only semantic queries at runtime; hard-filter-only searches do not invoke this library.

## Main entry points

- `EmbedDocument` and `EmbedDocuments` format input with the profile's passage prefix.
- `EmbedQuery` formats input with the profile's query prefix and returns one 768-dimensional vector for the multilingual profile.
- `EmbeddingProfileDescriptor.MultilingualE5Base` is the explicit bilingual runtime profile. The legacy profile remains available for historical callers and artifacts.

The .NET project pins its ONNX Runtime and tokenizer dependencies in the project and lock files. Model binaries are supplied separately through `CINEKROS_E5_MODEL_DIR`; they are not downloaded by the API at request time.
