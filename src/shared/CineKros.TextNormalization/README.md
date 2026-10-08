# CineKros.TextNormalization

Dependency-free .NET 10 normalization for Serbian Cyrillic text that must be represented in Serbian Latin.

`SerbianLatinNormalizer.Normalize(string)` applies NFC, maps the 30 Serbian Cyrillic letters, collapses Unicode whitespace runs to one ASCII space, trims, and applies NFC again. It preserves case, existing Latin text and diacritics, punctuation, digits, and unrelated characters. It does not fold case, spell-check, parse entities, or transliterate other alphabets. Null input throws `ArgumentNullException`; empty or whitespace-only input returns an empty string.

For `љ`, `њ`, and `џ`, lower case maps to `lj`, `nj`, and `dž`. Uppercase source letters map to title-case digraphs (`Lj`, `Nj`, `Dž`) in mixed-case words and all-uppercase digraphs (`LJ`, `NJ`, `DŽ`) in all-uppercase words. A word is a contiguous run of Unicode letters; punctuation, digits, whitespace, and other nonletters end that run.

The contract identifier is `serbian-latin-nfc-whitespace-v1`.
