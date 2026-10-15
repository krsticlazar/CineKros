# CineKros.TextNormalization

Dependency-free .NET 10 library for converting Serbian Cyrillic text to Serbian Latin while preserving useful text distinctions. In the bilingual API flow, the normalizer is applied to Serbian semantic queries before query embedding; English queries are passed through without Serbian transliteration.

`SerbianLatinNormalizer.Normalize(string)` applies Unicode NFC, maps the 30 Serbian Cyrillic letters, collapses each run of Unicode whitespace to one ASCII space, trims, and applies NFC again. It preserves case, existing Latin text and diacritics, punctuation, digits, and unrelated characters. It does not fold case, spell-check, parse entities, or transliterate other alphabets.

For `љ`, `њ`, and `џ`, lowercase maps to `lj`, `nj`, and `dž`. Uppercase source letters map to title-case digraphs (`Lj`, `Nj`, `Dž`) in mixed-case words and all-uppercase digraphs (`LJ`, `NJ`, `DŽ`) in all-uppercase words. A word is a contiguous run of Unicode letters; punctuation, digits, whitespace, and other nonletters end that run.

The contract identifier is `serbian-latin-nfc-whitespace-v1`. Null input throws `ArgumentNullException`; empty and whitespace-only strings return an empty string.
