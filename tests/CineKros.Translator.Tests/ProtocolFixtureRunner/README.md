# ProtocolFixtureRunner

Small child-process fixture used by `CineKros.Translator.Tests` to exercise the translator's JSON-lines subprocess contract. It reads one request per input line and can emit valid, malformed, incomplete, duplicate, or extra output; it also supports controlled failure, timeout, cancellation, and grandchild-process cases. It is test infrastructure only and does not load the real model or call external services.
