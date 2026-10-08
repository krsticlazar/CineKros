# CineKros.TextNormalization.Tests

Focused MSTest coverage for the shared Serbian Latin normalizer, including all 30 Serbian Cyrillic letters, digraph casing and boundaries, NFC, whitespace, preserved Latin and non-Serbian text, null/empty behavior, and idempotence.

Run with `dotnet test tests/CineKros.TextNormalization.Tests/CineKros.TextNormalization.Tests.csproj` from the repository root. The project uses the repository's MSTest.Sdk 4.4.0 and Microsoft.Testing.Platform conventions and has no project dependency beyond the normalizer library.
