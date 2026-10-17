# CineKros V6 — korpus stvarne evaluacije (Phase 5)

**Datum i status:** 17.10.2026; stvarno lokalno V6 snimanje završeno (`CAPTURE_COMPLETE`), zaštićene DB provere prošle. Ovo je namerno odabrani upareni skup za tehničko poređenje, a ne novi nezavisni benchmark opšte uspešnosti. Ručna procena relevantnosti filmova još nije obavljena.

**Poreklo korpusa:** prethodni `korpus.md` sačuvan je bajt-po-bajt u `.local/planning/reports/v6-phase-05/baseline-v5/korpus.md` (SHA-256 `fc386b4a473f2d05391d9c79cd583a74af911e5c31abd1fa6c6d387081bd12af`). Novi zamrznuti korpus: `.local/planning/reports/v6-phase-05/session-01/corpus-v6.json` (SHA-256 `e0aacd5daeebd550eab2f2c6bda1fc07cf7a547af82b796c6bf0ac725e831766`); freeze: `f2553c6fa89114ac774834034d1f6b662ebe24f5831037cd6ab793a832bd58b0`.

**Okruženje:** MovieLens katalog, 9.730 filmova; `actor-v6-isolated`; `multilingual-e5-base-int8-onnx-v1` (768 dimenzija); samo read-only proba nad rehearsal bazom `cinekros_v6_rehearsal_20261015t2154z_4fd266aa`. Izvršeno je 36 zamrznutih slučajeva i 39 HTTP pokušaja: 3 ograničena ponavljanja zbog zabeleženih privremenih grešaka; canary nije slat. Krajnji DTO u tabelama uključuje serversku validaciju, normalizaciju i V6 jezički guard — nije sirov JSON koji je vratio Gemini.

Korpus sadrži 30 redovnih upita (15 EN/SR parova) i šest očekivanih alerta. Mismatch ili validan prazan rezultat nisu ponavljani. Referentni semantički tekstovi dopuštaju verne parafraze, ali ne i nove/opuštene tvrde filtere; izostavljeno i `null` neaktivno polje tretiraju se ekvivalentno. Preklapanje preporuka meri samo stabilnu ID podudarnost, ne relevantnost.

Sažeta parser ocena prema MAIN pregledu: PASS 34, MISMATCH 2, ERROR 0; ENG 15/15 PASS, SR 13/15 PASS; alerti 6/6 PASS. SQL trace evidencija je sačuvana; Nezavisna SQL revizija: PASS, 30/30 tragova verno prati stvarni validirani DTO; svih 24 aktivnih tvrdih filtera potvrđeno; hard-only redosled i vraćena podobnost prema metadata oracle-u PASS.

## P01 — hard-only

**Originalni upit EN:** `Show me Comedy films released from 2002 through 2012.`  **SR:** `Prikaži mi komedije snimljene od 2002. do zaključno 2012. godine.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":2002,"yearMax":2012,"genres":{"all":["Comedy"]}}`; SR `{"yearMin":2002,"yearMax":2012,"genres":{"all":["Comedy"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":2002,"yearMax":2012,"genres":{"all":["Comedy"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2002,"yearMax":2012,"genres":{"all":["Comedy"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=2002; yearMax:Integer=2012; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Comedy"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":2002,"yearMax":2012,"genres":{"all":["Comedy"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2002,"yearMax":2012,"genres":{"any":["Comedy"]}}},"languageCheck":"match"}` | **MISMATCH** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=2002; yearMax:Integer=2012; genresAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Comedy"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: P01-SR se strukturno razlikuje: singleton `Comedy` je `genres.any`, a freeze očekuje `genres.all`; MAIN beleži isto SQL značenje za jedan žanr. Ne menjati zamrznuto očekivanje.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Pirates of the Caribbean: The Curse of the Black Pearl (2003) | Pirates of the Caribbean: The Curse of the Black Pearl (2003) |
| 2 | Finding Nemo (2003) | Finding Nemo (2003) |
| 3 | Incredibles, The (2004) | Incredibles, The (2004) |
| 4 | Shaun of the Dead (2004) | Shaun of the Dead (2004) |
| 5 | Lost in Translation (2003) | Lost in Translation (2003) |
| 6 | Shrek 2 (2004) | Shrek 2 (2004) |
| 7 | Little Miss Sunshine (2006) | Little Miss Sunshine (2006) |
| 8 | Juno (2007) | Juno (2007) |
| 9 | Ice Age (2002) | Ice Age (2002) |
| 10 | Hangover, The (2009) | Hangover, The (2009) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Pirates of the Caribbean: The Curse of the Black Pearl (2003); Finding Nemo (2003); Incredibles, The (2004); Shaun of the Dead (2004); Lost in Translation (2003); Shrek 2 (2004); Little Miss Sunshine (2006); Juno (2007); Ice Age (2002); Hangover, The (2009). Preklapanje nije ocena relevantnosti.

## P02 — hard-only

**Originalni upit EN:** `I want films that are both Action and Adventure, released after 2006 and before 2019.`  **SR:** `Želim filmove koji su i akcioni i avanturistički, snimljene posle 2006. i pre 2019. godine.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}`; SR `{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=2007; yearMax:Integer=2018; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Action","Adventure"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2007,"yearMax":2018,"genres":{"all":["Action","Adventure"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=2007; yearMax:Integer=2018; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Action","Adventure"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Iron Man (2008) | Iron Man (2008) |
| 2 | Avatar (2009) | Avatar (2009) |
| 3 | Dark Knight Rises, The (2012) | Dark Knight Rises, The (2012) |
| 4 | Avengers, The (2012) | Avengers, The (2012) |
| 5 | Star Trek (2009) | Star Trek (2009) |
| 6 | Harry Potter and the Deathly Hallows: Part 1 (2010) | Harry Potter and the Deathly Hallows: Part 1 (2010) |
| 7 | The Hunger Games (2012) | The Hunger Games (2012) |
| 8 | Iron Man 2 (2010) | Iron Man 2 (2010) |
| 9 | Skyfall (2012) | Skyfall (2012) |
| 10 | Pirates of the Caribbean: At World's End (2007) | Pirates of the Caribbean: At World's End (2007) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Iron Man (2008); Avatar (2009); Dark Knight Rises, The (2012); Avengers, The (2012); Star Trek (2009); Harry Potter and the Deathly Hallows: Part 1 (2010); The Hunger Games (2012); Iron Man 2 (2010); Skyfall (2012); Pirates of the Caribbean: At World's End (2007). Preklapanje nije ocena relevantnosti.

## P03 — hard-only

**Originalni upit EN:** `Find Horror or Thriller films lasting at least 90 and at most 112 minutes.`  **SR:** `Pronađi horore ili trilere koji traju najmanje 90, a najviše 112 minuta.`

**Očekivani tvrdi uslovi:** EN `{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}`; SR `{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: runtimeMin:Integer=90; runtimeMax:Integer=112; genresAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Horror","Thriller"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"runtimeMin":90,"runtimeMax":112,"genres":{"any":["Horror","Thriller"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: runtimeMin:Integer=90; runtimeMax:Integer=112; genresAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Horror","Thriller"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Usual Suspects, The (1995) | Usual Suspects, The (1995) |
| 2 | Fargo (1996) | Fargo (1996) |
| 3 | Sixth Sense, The (1999) | Sixth Sense, The (1999) |
| 4 | Terminator, The (1984) | Terminator, The (1984) |
| 5 | Kill Bill: Vol. 1 (2003) | Kill Bill: Vol. 1 (2003) |
| 6 | Reservoir Dogs (1992) | Reservoir Dogs (1992) |
| 7 | Mission: Impossible (1996) | Mission: Impossible (1996) |
| 8 | Léon: The Professional (a.k.a. The Professional) (Léon) (1994) | Léon: The Professional (a.k.a. The Professional) (Léon) (1994) |
| 9 | Snatch (2000) | Snatch (2000) |
| 10 | Psycho (1960) | Psycho (1960) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Usual Suspects, The (1995); Fargo (1996); Sixth Sense, The (1999); Terminator, The (1984); Kill Bill: Vol. 1 (2003); Reservoir Dogs (1992); Mission: Impossible (1996); Léon: The Professional (a.k.a. The Professional) (Léon) (1994); Snatch (2000); Psycho (1960). Preklapanje nije ocena relevantnosti.

## P04 — hard-only

**Originalni upit EN:** `Show me films originally in French, released before 1999.`  **SR:** `Prikaži mi filmove izvorno na francuskom jeziku, snimljene pre 1999. godine.`

**Očekivani tvrdi uslovi:** EN `{"yearMax":1998,"originalLanguage":"fr"}`; SR `{"yearMax":1998,"originalLanguage":"fr"}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMax":1998,"originalLanguage":"fr"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMax":1998,"originalLanguage":"fr"}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMax:Integer=1998; originalLanguage:Text=fr; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMax":1998,"originalLanguage":"fr"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMax":1998,"originalLanguage":"fr"}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMax:Integer=1998; originalLanguage:Text=fr; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Léon: The Professional (a.k.a. The Professional) (Léon) (1994) | Léon: The Professional (a.k.a. The Professional) (Léon) (1994) |
| 2 | Fifth Element, The (1997) | Fifth Element, The (1997) |
| 3 | Femme Nikita, La (Nikita) (1990) | Femme Nikita, La (Nikita) (1990) |
| 4 | City of Lost Children, The (Cité des enfants perdus, La) (1995) | City of Lost Children, The (Cité des enfants perdus, La) (1995) |
| 5 | Delicatessen (1991) | Delicatessen (1991) |
| 6 | Three Colors: Red (Trois couleurs: Rouge) (1994) | Three Colors: Red (Trois couleurs: Rouge) (1994) |
| 7 | Three Colors: Blue (Trois couleurs: Bleu) (1993) | Three Colors: Blue (Trois couleurs: Bleu) (1993) |
| 8 | Red Violin, The (Violon rouge, Le) (1998) | Red Violin, The (Violon rouge, Le) (1998) |
| 9 | Three Colors: White (Trzy kolory: Bialy) (1994) | Three Colors: White (Trzy kolory: Bialy) (1994) |
| 10 | Professional, The (Le professionnel) (1981) | Professional, The (Le professionnel) (1981) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Léon: The Professional (a.k.a. The Professional) (Léon) (1994); Fifth Element, The (1997); Femme Nikita, La (Nikita) (1990); City of Lost Children, The (Cité des enfants perdus, La) (1995); Delicatessen (1991); Three Colors: Red (Trois couleurs: Rouge) (1994); Three Colors: Blue (Trois couleurs: Bleu) (1993); Red Violin, The (Violon rouge, Le) (1998); Three Colors: White (Trzy kolory: Bialy) (1994); Professional, The (Le professionnel) (1981). Preklapanje nije ocena relevantnosti.

## P05 — hard-only

**Originalni upit EN:** `I want Sci-Fi films from 1994 onward, no longer than 124 minutes, rated at least 8 out of 10.`  **SR:** `Želim SF filmove od 1994. godine nadalje, u trajanju od najviše 124 minuta, sa ocenom najmanje 8 od 10.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}`; SR `{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}},"languageCheck":"match"}` | **PASS** | 200 / n=4 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=1994; runtimeMax:Integer=124; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Sci-Fi"]; ratingMin:Numeric=4; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1994,"runtimeMax":124,"genres":{"all":["Sci-Fi"]},"ratingMin":4,"ratingOperator":"gte"}},"languageCheck":"match"}` | **PASS** | 200 / n=4 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=1994; runtimeMax:Integer=124; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Sci-Fi"]; ratingMin:Numeric=4; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 4, EN 4.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Eternal Sunshine of the Spotless Mind (2004) | Eternal Sunshine of the Spotless Mind (2004) |
| 2 | WALL·E (2008) | WALL·E (2008) |
| 3 | Moon (2009) | Moon (2009) |
| 4 | Neon Genesis Evangelion: The End of Evangelion (Shin seiki Evangelion Gekijô-ban: Air/Magokoro wo, kimi ni) (1997) | Neon Genesis Evangelion: The End of Evangelion (Shin seiki Evangelion Gekijô-ban: Air/Magokoro wo, kimi ni) (1997) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 4/10 (I/n); nSR=4, nEN=4. Zajednički filmovi prema stvarnom redosledu SR odgovora: Eternal Sunshine of the Spotless Mind (2004); WALL·E (2008); Moon (2009); Neon Genesis Evangelion: The End of Evangelion (Shin seiki Evangelion Gekijô-ban: Air/Magokoro wo, kimi ni) (1997). Preklapanje nije ocena relevantnosti.

## P06 — hard-only

**Originalni upit EN:** `Find Drama films originally in English with a rating greater than 4 out of 5.`  **SR:** `Pronađi drame izvorno na engleskom jeziku sa ocenom većom od 4 od 5.`

**Očekivani tvrdi uslovi:** EN `{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}`; SR `{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Drama"]; ratingMin:Numeric=4; originalLanguage:Text=en; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Drama"]},"ratingMin":4,"originalLanguage":"en","ratingOperator":"gt"}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Drama"]; ratingMin:Numeric=4; originalLanguage:Text=en; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Shawshank Redemption, The (1994) | Shawshank Redemption, The (1994) |
| 2 | Forrest Gump (1994) | Forrest Gump (1994) |
| 3 | Pulp Fiction (1994) | Pulp Fiction (1994) |
| 4 | Fight Club (1999) | Fight Club (1999) |
| 5 | Schindler's List (1993) | Schindler's List (1993) |
| 6 | Lord of the Rings: The Return of the King, The (2003) | Lord of the Rings: The Return of the King, The (2003) |
| 7 | Godfather, The (1972) | Godfather, The (1972) |
| 8 | American Beauty (1999) | American Beauty (1999) |
| 9 | Dark Knight, The (2008) | Dark Knight, The (2008) |
| 10 | Saving Private Ryan (1998) | Saving Private Ryan (1998) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Shawshank Redemption, The (1994); Forrest Gump (1994); Pulp Fiction (1994); Fight Club (1999); Schindler's List (1993); Lord of the Rings: The Return of the King, The (2003); Godfather, The (1972); American Beauty (1999); Dark Knight, The (2008); Saving Private Ryan (1998). Preklapanje nije ocena relevantnosti.

## P07 — semantic-only

**Originalni upit EN:** `I want a quiet film about a friendship that changes over many years and the small choices that shape a life.`  **SR:** `Želim tih film o prijateljstvu koje se menja tokom mnogo godina i malim odlukama koje oblikuju život.`

**Očekivani tvrdi uslovi:** EN `{}`; SR `{}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"a quiet film about friendship changing over many years and small choices shaping a life"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"a quiet film about a friendship that changes over many years and the small choices that shape a life"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=English; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"tih film o prijateljstvu koje se menja tokom mnogo godina i malim odlukama koje oblikuju život"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"tih film o prijateljstvu koje se menja tokom mnogo godina i malim odlukama koje oblikuju život"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=Serbian; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Short Film About Love, A (Krótki film o milosci) (1988) | 10 Items or Less (2006) |
| 2 | It's Kind of a Funny Story (2010) | Walking and Talking (1996) |
| 3 | Walking and Talking (1996) | Chosen, The (1981) |
| 4 | Small Faces (1996) | Little Rascals, The (1994) |
| 5 | Short Film About Killing, A (Krótki film o zabijaniu) (1988) | Dinner with Friends (2001) |
| 6 | Life (1999) | Short Film About Love, A (Krótki film o milosci) (1988) |
| 7 | 10 Items or Less (2006) | Shrink (2009) |
| 8 | Conversations with Other Women (2005) | Small Faces (1996) |
| 9 | Married Life (2007) | Myth of Fingerprints, The (1997) |
| 10 | Small Soldiers (1998) | Not One Less (Yi ge dou bu neng shao) (1999) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 4/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Short Film About Love, A (Krótki film o milosci) (1988); Walking and Talking (1996); Small Faces (1996); 10 Items or Less (2006). Preklapanje nije ocena relevantnosti.

## P08 — semantic-only

**Originalni upit EN:** `I want a tense story about loneliness and isolation aboard a distant space station.`  **SR:** `Želim napetu priču o usamljenosti i izolaciji na udaljenoj svemirskoj stanici.`

**Očekivani tvrdi uslovi:** EN `{}`; SR `{}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"a tense story about loneliness and isolation on a distant space station"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"tense story about loneliness and isolation aboard a distant space station"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=English; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"napeta priča o usamljenosti i izolaciji na udaljenoj svemirskoj stanici"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"napeta priča o usamljenosti i izolaciji na udaljenoj svemirskoj stanici"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=Serbian; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Far and Away (1992) | Station Agent, The (2003) |
| 2 | Voices of a Distant Star (Hoshi no koe) (2003) | Distant (Uzak) (2002) |
| 3 | District 9 (2009) | Elling (2001) |
| 4 | Somewhere (2010) | Tenant, The (Locataire, Le) (1976) |
| 5 | Morons From Outer Space (1985) | Voices of a Distant Star (Hoshi no koe) (2003) |
| 6 | Distant (Uzak) (2002) | Kitchen Stories (Salmer fra kjøkkenet) (2003) |
| 7 | Astronaut's Wife, The (1999) | Castaway on the Moon (Kimssi pyoryugi) (2009) |
| 8 | Storytelling (2001) | May (2002) |
| 9 | Walking and Talking (1996) | Carriers (2009) |
| 10 | Lights in the Dusk (Laitakaupungin valot) (2006) | Ikiru (1952) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 2/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Voices of a Distant Star (Hoshi no koe) (2003); Distant (Uzak) (2002). Preklapanje nije ocena relevantnosti.

## P09 — hard-only

**Originalni upit EN:** `Show me a film starring Tilda Swinton.`  **SR:** `Prikaži mi film u kojem glumi Tilda Swinton.`

**Očekivani tvrdi uslovi:** EN `{"actors":{"all":["Tilda Swinton"]}}`; SR `{"actors":{"all":["Tilda Swinton"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"actors":{"all":["Tilda Swinton"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"actors":{"all":["Tilda Swinton"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: actorsAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Tilda Swinton"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"actors":{"all":["Tilda Swinton"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"actors":{"all":["Tilda Swinton"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: actorsAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Tilda Swinton"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Chronicles of Narnia: The Lion, the Witch and the Wardrobe, The (2005) | Chronicles of Narnia: The Lion, the Witch and the Wardrobe, The (2005) |
| 2 | Curious Case of Benjamin Button, The (2008) | Curious Case of Benjamin Button, The (2008) |
| 3 | Adaptation (2002) | Adaptation (2002) |
| 4 | Moonrise Kingdom (2012) | Moonrise Kingdom (2012) |
| 5 | Burn After Reading (2008) | Burn After Reading (2008) |
| 6 | Beach, The (2000) | Beach, The (2000) |
| 7 | Michael Clayton (2007) | Michael Clayton (2007) |
| 8 | Broken Flowers (2005) | Broken Flowers (2005) |
| 9 | We Need to Talk About Kevin (2011) | We Need to Talk About Kevin (2011) |
| 10 | Orlando (1992) | Orlando (1992) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Chronicles of Narnia: The Lion, the Witch and the Wardrobe, The (2005); Curious Case of Benjamin Button, The (2008); Adaptation (2002); Moonrise Kingdom (2012); Burn After Reading (2008); Beach, The (2000); Michael Clayton (2007); Broken Flowers (2005); We Need to Talk About Kevin (2011); Orlando (1992). Preklapanje nije ocena relevantnosti.

## P10 — semantic-only

**Originalni upit EN:** `I want a film directed by Denis Villeneuve.`  **SR:** `Želim film koji je režirao Denis Villeneuve.`

**Očekivani tvrdi uslovi:** EN `{}`; SR `{}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"a film directed by Denis Villeneuve"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"a film directed by Denis Villeneuve"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=English; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"film koji je režirao Denis Villeneuve"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{},"semanticQuery":"film koji je režirao Denis Villeneuve"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=Serbian; params: no hard-filter parameters; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Incendies (2010) | Maelström (2000) |
| 2 | Prisoners (2013) | Incendies (2010) |
| 3 | Maelström (2000) | Prisoners (2013) |
| 4 | Good Work (Beau travail) (1999) | Videodrome (1983) |
| 5 | To Have, or Not (En avoir (ou pas)) (1995) | Good Work (Beau travail) (1999) |
| 6 | Chimes at Midnight (Campanadas a medianoche) (1965) | Pépé le Moko (1937) |
| 7 | That Obscure Object of Desire (Cet obscur objet du désir) (1977) | 11'09"01 - September 11 (2002) |
| 8 | Separation, The (Séparation, La) (1994) | Place Beyond the Pines, The (2012) |
| 9 | Django Unchained (2012) | Dallas Buyers Club (2013) |
| 10 | Whiteout (2009) | Assignment, The (1997) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 4/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Incendies (2010); Prisoners (2013); Maelström (2000); Good Work (Beau travail) (1999). Preklapanje nije ocena relevantnosti.

## P11 — hybrid

**Originalni upit EN:** `I want Crime films released after 1999, under 130 minutes, originally in Italian, with a rating of at least 7. Focus on loyalty, betrayal, and a difficult moral choice.`  **SR:** `Želim kriminalističke filmove snimljene posle 1999. godine, kraće od 130 minuta, izvorno na italijanskom jeziku, sa ocenom najmanje 7. Naglasak neka bude na odanosti, izdaji i teškom moralnom izboru.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"}`; SR `{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"},"semanticQuery":"loyalty, betrayal, and a difficult moral choice"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"},"semanticQuery":"Focus on loyalty, betrayal, and a difficult moral choice."},"languageCheck":"match"}` | **PASS** | 200 / alert `NO_RESULTS`; n=0 | eligible-first; SQL language=English; params: yearMin:Integer=2000; runtimeMax:Integer=129; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Crime"]; ratingMin:Numeric=3.5; originalLanguage:Text=it; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"},"semanticQuery":"odanost, izdaja i težak moralni izbor"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":2000,"runtimeMax":129,"genres":{"all":["Crime"]},"ratingMin":3.5,"originalLanguage":"it","ratingOperator":"gte"},"semanticQuery":"kriminalistički filmovi sa naglaskom na odanosti, izdaji i teškom moralnom izboru"},"languageCheck":"match"}` | **MISMATCH** | 200 / alert `NO_RESULTS`; n=0 | eligible-first; SQL language=Serbian; params: yearMin:Integer=2000; runtimeMax:Integer=129; genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Crime"]; ratingMin:Numeric=3.5; originalLanguage:Text=it; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Oba upita imaju validan kanonski query, SQL trace i HTTP 200 `NO_RESULTS`; to nisu parser alerti. MAIN je za P11-SR označio ponavljanje tvrdog uslova `Crime` u `semanticQuery` kao semantičko curenje tvrdog filtera.

**Broj stvarnih filmova:** SR 0, EN 0.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
Za oba jezika nema filmova: validni HTTP 200 `NO_RESULTS` odgovori; tabela namerno nema izmišljene redove.

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** I=0 (0 zajedničkih); uporedivost N/A — oba upita imaju validan prazan rezultat. Zajednički filmovi prema stvarnom redosledu SR odgovora: nema zajedničkih filmova. Preklapanje nije ocena relevantnosti.

## P12 — hybrid

**Originalni upit EN:** `Show me Animation or Fantasy films released from 1988 through 2011, lasting between 80 and 125 minutes inclusive, originally in Japanese. I prefer a gentle sense of wonder and a journey toward independence.`  **SR:** `Prikaži mi animirane ili fantastične filmove snimljene od 1988. do zaključno 2011. godine, u trajanju od 80 do zaključno 125 minuta, izvorno na japanskom jeziku. Više mi odgovaraju blag osećaj čudesnog i put ka samostalnosti.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"}`; SR `{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"},"semanticQuery":"a gentle sense of wonder and a journey toward independence"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"},"semanticQuery":"a gentle sense of wonder and a journey toward independence"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=English; params: yearMin:Integer=1988; yearMax:Integer=2011; runtimeMin:Integer=80; runtimeMax:Integer=125; genresAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Animation","Fantasy"]; originalLanguage:Text=ja; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"},"semanticQuery":"blag osećaj čudesnog i put ka samostalnosti"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1988,"yearMax":2011,"runtimeMin":80,"runtimeMax":125,"genres":{"any":["Animation","Fantasy"]},"originalLanguage":"ja"},"semanticQuery":"blag osećaj čudesnog i put ka samostalnosti"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=Serbian; params: yearMin:Integer=1988; yearMax:Integer=2011; runtimeMin:Integer=80; runtimeMax:Integer=125; genresAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Animation","Fantasy"]; originalLanguage:Text=ja; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Whisper of the Heart (Mimi wo sumaseba) (1995) | Spirited Away (Sen to Chihiro no kamikakushi) (2001) |
| 2 | Akira Kurosawa's Dreams (Dreams) (1990) | Whisper of the Heart (Mimi wo sumaseba) (1995) |
| 3 | Spirited Away (Sen to Chihiro no kamikakushi) (2001) | Evangelion: 1.0 You Are (Not) Alone (Evangerion shin gekijôban: Jo) (2007) |
| 4 | Ponyo (Gake no ue no Ponyo) (2008) | Howl's Moving Castle (Hauru no ugoku shiro) (2004) |
| 5 | After Life (Wandafuru raifu) (1998) | Tekkonkinkreet (Tekkon kinkurîto) (2006) |
| 6 | Little Nemo: Adventures in Slumberland (1992) | Akira Kurosawa's Dreams (Dreams) (1990) |
| 7 | Millennium Actress (Sennen joyû) (2001) | Kiki's Delivery Service (Majo no takkyûbin) (1989) |
| 8 | Grave of the Fireflies (Hotaru no haka) (1988) | After Life (Wandafuru raifu) (1998) |
| 9 | Howl's Moving Castle (Hauru no ugoku shiro) (2004) | Ponyo (Gake no ue no Ponyo) (2008) |
| 10 | Mind Game (2004) | Millennium Actress (Sennen joyû) (2001) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 7/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Whisper of the Heart (Mimi wo sumaseba) (1995); Akira Kurosawa's Dreams (Dreams) (1990); Spirited Away (Sen to Chihiro no kamikakushi) (2001); Ponyo (Gake no ue no Ponyo) (2008); After Life (Wandafuru raifu) (1998); Millennium Actress (Sennen joyû) (2001); Howl's Moving Castle (Hauru no ugoku shiro) (2004). Preklapanje nije ocena relevantnosti.

## P13 — hard-only

**Originalni upit EN:** `Movies released from 1990 through 2015 starring both Tom Hanks and Tim Allen.`  **SR:** `Филмови снимљени од 1990. до закључно 2015. године у којима глуме и Tom Hanks и Tim Allen.`

**Očekivani tvrdi uslovi:** EN `{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}`; SR `{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=3 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=1990; yearMax:Integer=2015; actorsAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Tom Hanks","Tim Allen"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"yearMin":1990,"yearMax":2015,"actors":{"all":["Tom Hanks","Tim Allen"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=3 | hard-only; SQL language=not applicable (hard-only ordering); params: yearMin:Integer=1990; yearMax:Integer=2015; actorsAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Tom Hanks","Tim Allen"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 3, EN 3.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Toy Story (1995) | Toy Story (1995) |
| 2 | Toy Story 2 (1999) | Toy Story 2 (1999) |
| 3 | Toy Story 3 (2010) | Toy Story 3 (2010) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 3/10 (I/n); nSR=3, nEN=3. Zajednički filmovi prema stvarnom redosledu SR odgovora: Toy Story (1995); Toy Story 2 (1999); Toy Story 3 (2010). Preklapanje nije ocena relevantnosti.

## P14 — hybrid

**Originalni upit EN:** `Crime movies starring either Brad Pitt or George Clooney, excluding both Julia Roberts and Matt Damon. I prefer clever schemes and a tense atmosphere.`  **SR:** `Криминалистички филмови у којима глуми или Brad Pitt или George Clooney, без Julia Roberts и Matt Damon. Више ми одговарају домишљати планови и напета атмосфера.`

**Očekivani tvrdi uslovi:** EN `{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}}`; SR `{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}},"semanticQuery":"clever schemes and a tense atmosphere"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}},"semanticQuery":"clever schemes and a tense atmosphere"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=English; params: genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Crime"]; actorsAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Brad Pitt","George Clooney"]; actorsExclude:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Julia Roberts","Matt Damon"]; query vector present; payload omitted; order: me.embedding <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}},"semanticQuery":"domišljati planovi i napeta atmosfera"},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"genres":{"all":["Crime"]},"actors":{"any":["Brad Pitt","George Clooney"],"exclude":["Julia Roberts","Matt Damon"]}},"semanticQuery":"domišljati planovi i napeta atmosfera"},"languageCheck":"match"}` | **PASS** | 200 / n=10 | eligible-first; SQL language=Serbian; params: genresAll:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Crime"]; actorsAny:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Brad Pitt","George Clooney"]; actorsExclude:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Julia Roberts","Matt Damon"]; query vector present; payload omitted; order: me.embedding_sr <=> @queryVector ASC, e.movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Killing Them Softly (2012) | Killing Them Softly (2012) |
| 2 | Counselor, The (2013) | O Brother, Where Art Thou? (2000) |
| 3 | Spy Game (2001) | Spy Game (2001) |
| 4 | Burn After Reading (2008) | True Romance (1993) |
| 5 | Welcome to Collinwood (2002) | Counselor, The (2013) |
| 6 | Good Night, and Good Luck. (2005) | Fight Club (1999) |
| 7 | Assassination of Jesse James by the Coward Robert Ford, The (2007) | Assassination of Jesse James by the Coward Robert Ford, The (2007) |
| 8 | O Brother, Where Art Thou? (2000) | Snatch (2000) |
| 9 | Fight Club (1999) | Welcome to Collinwood (2002) |
| 10 | Fantastic Mr. Fox (2009) | Out of Sight (1998) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 7/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Killing Them Softly (2012); Counselor, The (2013); Spy Game (2001); Welcome to Collinwood (2002); Assassination of Jesse James by the Coward Robert Ford, The (2007); O Brother, Where Art Thou? (2000); Fight Club (1999). Preklapanje nije ocena relevantnosti.

## P15 — hard-only

**Originalni upit EN:** `Movies without Brad Pitt.`  **SR:** `Филмови без глумца Brad Pitt.`

**Očekivani tvrdi uslovi:** EN `{"actors":{"exclude":["Brad Pitt"]}}`; SR `{"actors":{"exclude":["Brad Pitt"]}}`. Prikaz izostavlja neaktivna `null` polja radi čitljivosti; puni zamrznuti DTO-i u `corpus-v6.json` ostaju neizmenjeni.

| Jezik | Očekivani kanonski DTO | Stvarni validirani kanonski DTO | Parser | HTTP / filmovi | Stvarni SQL filteri i selekcija |
|---|---|---|---|---:|---|
| EN | `{"type":"query","query":{"hardFilters":{"actors":{"exclude":["Brad Pitt"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"actors":{"exclude":["Brad Pitt"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: actorsExclude:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Brad Pitt"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |
| SR | `{"type":"query","query":{"hardFilters":{"actors":{"exclude":["Brad Pitt"]}}},"languageCheck":"match"}` | `{"type":"query","query":{"hardFilters":{"actors":{"exclude":["Brad Pitt"]}}},"languageCheck":"match"}` | **PASS** | 200 / n=10 | hard-only; SQL language=not applicable (hard-only ordering); params: actorsExclude:Array + Text (NpgsqlDbType numeric enum -2147483629)=["Brad Pitt"]; no query vector; order: rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC; limit=10; independent SQL audit PASS. |

SQL/DTO napomena: Puni SQL, tipizovani parametri, correlation ID i log opseg nalaze se u sirovim attempt zapisima. Nezavisna revizija je potvrdila vernost svih 30 SQL tragova stvarnom DTO-u i 24/24 aktivnih tvrdih filtera.

**Broj stvarnih filmova:** SR 10, EN 10.

| Pozicija | SR film (naziv i godina) | EN film (naziv i godina) |
|---:|---|---|
| 1 | Shawshank Redemption, The (1994) | Shawshank Redemption, The (1994) |
| 2 | Forrest Gump (1994) | Forrest Gump (1994) |
| 3 | Pulp Fiction (1994) | Pulp Fiction (1994) |
| 4 | Matrix, The (1999) | Matrix, The (1999) |
| 5 | Silence of the Lambs, The (1991) | Silence of the Lambs, The (1991) |
| 6 | Star Wars: Episode IV - A New Hope (1977) | Star Wars: Episode IV - A New Hope (1977) |
| 7 | Jurassic Park (1993) | Jurassic Park (1993) |
| 8 | Schindler's List (1993) | Schindler's List (1993) |
| 9 | Lord of the Rings: The Fellowship of the Ring, The (2001) | Lord of the Rings: The Fellowship of the Ring, The (2001) |
| 10 | Star Wars: Episode V - The Empire Strikes Back (1980) | Star Wars: Episode V - The Empire Strikes Back (1980) |

**Moja ručna evaluacija SR preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Moja ručna evaluacija EN preporuka:**

[Ostavljam prazno — naknadno ću lično napisati opšti komentar o svih 10 filmova.]

**Poređenje para:** 10/10 (I/n); nSR=10, nEN=10. Zajednički filmovi prema stvarnom redosledu SR odgovora: Shawshank Redemption, The (1994); Forrest Gump (1994); Pulp Fiction (1994); Matrix, The (1999); Silence of the Lambs, The (1991); Star Wars: Episode IV - A New Hope (1977); Jurassic Park (1993); Schindler's List (1993); Lord of the Rings: The Fellowship of the Ring, The (2001); Star Wars: Episode V - The Empire Strikes Back (1980). Preklapanje nije ocena relevantnosti.

## Očekivani alerti

| ID | Jezik | Originalni upit | Očekivani kanonski DTO | Stvarni validirani DTO | HTTP | SQL / E5 | Rezultat |
|---|---|---|---|---|---:|---|---|
| A01 | EN | `What will the weather be like in Belgrade tomorrow?` | `{"type":"alert","alertCode":"NOT_MOVIE_REQUEST","languageCheck":"match"}` | `{"type":"alert","alertCode":"NOT_MOVIE_REQUEST","languageCheck":"match"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |
| A02 | SR | `Želim film proizveden u Francuskoj.` | `{"type":"alert","alertCode":"UNSUPPORTED_REQUEST","languageCheck":"match"}` | `{"type":"alert","alertCode":"UNSUPPORTED_REQUEST","languageCheck":"match"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |
| A03 | SR | `I want a warm film about friendship and loyalty.` | `{"type":"alert","alertCode":"LANGUAGE_MISMATCH","languageCheck":"mismatch"}` | `{"type":"alert","alertCode":"LANGUAGE_MISMATCH","languageCheck":"mismatch"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |
| A04 | EN | `Желим напет филм о пријатељству и оданости.` | `{"type":"alert","alertCode":"LANGUAGE_MISMATCH","languageCheck":"mismatch"}` | `{"type":"alert","alertCode":"LANGUAGE_MISMATCH","languageCheck":"mismatch"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |
| A05 | SR | `Не желим филмове које је режирао Christopher Nolan.` | `{"type":"alert","alertCode":"UNSUPPORTED_REQUEST","languageCheck":"match"}` | `{"type":"alert","alertCode":"UNSUPPORTED_REQUEST","languageCheck":"match"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |
| A06 | EN | `movie` | `{"type":"alert","alertCode":"QUERY_UNCLEAR","languageCheck":"match"}` | `{"type":"alert","alertCode":"QUERY_UNCLEAR","languageCheck":"match"}` | 422 | 0 recommendation SQL; early-alert path (source/offline evidence; no runtime E5 counter claimed) | PASS |

## Sažetak

| Grupa | PASS | MISMATCH | ERROR | Napomena |
|---|---:|---:|---:|---|
| Redovni EN parser | 15/15 | 0 | 0 | Svih 15 zamrznutih EN slučajeva |
| Redovni SR parser | 13/15 | 2 | 0 | P01-SR i P11-SR |
| Očekivani alerti | 6/6 | 0 | 0 | SQL trace = 0 za svih šest |
| Stvarni SQL trace | 30/30 regularnih upita | — | — | Nezavisna SQL revizija: PASS, 30/30 tragova verno prati stvarni validirani DTO; svih 24 aktivnih tvrdih filtera potvrđeno; hard-only redosled i vraćena podobnost prema metadata oracle-u PASS. |

**Preklapanje po paru (I/10), sa stvarnim brojem filmova:**

| Par | Tip | nSR | nEN | I/10 | Zajednički filmovi (redosled SR) |
|---|---|---:|---:|---:|---|
| P01 | hard-only | 10 | 10 | 10/10 | Pirates of the Caribbean: The Curse of the Black Pearl (2003); Finding Nemo (2003); Incredibles, The (2004); Shaun of the Dead (2004); Lost in Translation (2003); Shrek 2 (2004); Little Miss Sunshine (2006); Juno (2007); Ice Age (2002); Hangover, The (2009) |
| P02 | hard-only | 10 | 10 | 10/10 | Iron Man (2008); Avatar (2009); Dark Knight Rises, The (2012); Avengers, The (2012); Star Trek (2009); Harry Potter and the Deathly Hallows: Part 1 (2010); The Hunger Games (2012); Iron Man 2 (2010); Skyfall (2012); Pirates of the Caribbean: At World's End (2007) |
| P03 | hard-only | 10 | 10 | 10/10 | Usual Suspects, The (1995); Fargo (1996); Sixth Sense, The (1999); Terminator, The (1984); Kill Bill: Vol. 1 (2003); Reservoir Dogs (1992); Mission: Impossible (1996); Léon: The Professional (a.k.a. The Professional) (Léon) (1994); Snatch (2000); Psycho (1960) |
| P04 | hard-only | 10 | 10 | 10/10 | Léon: The Professional (a.k.a. The Professional) (Léon) (1994); Fifth Element, The (1997); Femme Nikita, La (Nikita) (1990); City of Lost Children, The (Cité des enfants perdus, La) (1995); Delicatessen (1991); Three Colors: Red (Trois couleurs: Rouge) (1994); Three Colors: Blue (Trois couleurs: Bleu) (1993); Red Violin, The (Violon rouge, Le) (1998); Three Colors: White (Trzy kolory: Bialy) (1994); Professional, The (Le professionnel) (1981) |
| P05 | hard-only | 4 | 4 | 4/10 | Eternal Sunshine of the Spotless Mind (2004); WALL·E (2008); Moon (2009); Neon Genesis Evangelion: The End of Evangelion (Shin seiki Evangelion Gekijô-ban: Air/Magokoro wo, kimi ni) (1997) |
| P06 | hard-only | 10 | 10 | 10/10 | Shawshank Redemption, The (1994); Forrest Gump (1994); Pulp Fiction (1994); Fight Club (1999); Schindler's List (1993); Lord of the Rings: The Return of the King, The (2003); Godfather, The (1972); American Beauty (1999); Dark Knight, The (2008); Saving Private Ryan (1998) |
| P07 | semantic-only | 10 | 10 | 4/10 | Short Film About Love, A (Krótki film o milosci) (1988); Walking and Talking (1996); Small Faces (1996); 10 Items or Less (2006) |
| P08 | semantic-only | 10 | 10 | 2/10 | Voices of a Distant Star (Hoshi no koe) (2003); Distant (Uzak) (2002) |
| P09 | hard-only | 10 | 10 | 10/10 | Chronicles of Narnia: The Lion, the Witch and the Wardrobe, The (2005); Curious Case of Benjamin Button, The (2008); Adaptation (2002); Moonrise Kingdom (2012); Burn After Reading (2008); Beach, The (2000); Michael Clayton (2007); Broken Flowers (2005); We Need to Talk About Kevin (2011); Orlando (1992) |
| P10 | semantic-only | 10 | 10 | 4/10 | Incendies (2010); Prisoners (2013); Maelström (2000); Good Work (Beau travail) (1999) |
| P11 | hybrid | 0 | 0 | 0/10* | — |
| P12 | hybrid | 10 | 10 | 7/10 | Whisper of the Heart (Mimi wo sumaseba) (1995); Akira Kurosawa's Dreams (Dreams) (1990); Spirited Away (Sen to Chihiro no kamikakushi) (2001); Ponyo (Gake no ue no Ponyo) (2008); After Life (Wandafuru raifu) (1998); Millennium Actress (Sennen joyû) (2001); Howl's Moving Castle (Hauru no ugoku shiro) (2004) |
| P13 | hard-only | 3 | 3 | 3/10 | Toy Story (1995); Toy Story 2 (1999); Toy Story 3 (2010) |
| P14 | hybrid | 10 | 10 | 7/10 | Killing Them Softly (2012); Counselor, The (2013); Spy Game (2001); Welcome to Collinwood (2002); Assassination of Jesse James by the Coward Robert Ford, The (2007); O Brother, Where Art Thou? (2000); Fight Club (1999) |
| P15 | hard-only | 10 | 10 | 10/10 | Shawshank Redemption, The (1994); Forrest Gump (1994); Pulp Fiction (1994); Matrix, The (1999); Silence of the Lambs, The (1991); Star Wars: Episode IV - A New Hope (1977); Jurassic Park (1993); Schindler's List (1993); Lord of the Rings: The Fellowship of the Ring, The (2001); Star Wars: Episode V - The Empire Strikes Back (1980) |

Napomena: `0/10*` označava P11 sa nSR=0 i nEN=0; broj zajedničkih filmova je 0, ali uporedivost je N/A. Nula je ipak uključena kao 0 u sirovi prosek preko 15 parova.

Suma preklapanja je 101. Prosek preko svih 15 parova, uključujući validni prazni P11 kao 0: **6.7333/10** (n=15). Prosek preko 14 nepraznih uporedivih parova: **7.2143/10** (n=14). Devet parova su hard-only, šest semantic-only/hybrid; prosek nije ocena semantičkog kvaliteta niti filmske relevantnosti.

### Prazna tabela za Lazarovu kasniju ručnu procenu

Ova tabela ostavljena je prazna namerno; tehnički rezultati ne određuju kvalitet preporuka.

| Par | Moja opšta procena SR preporuka | Moja opšta procena EN preporuka |
|---|---|---|
| P01 |  |  |
| P02 |  |  |
| P03 |  |  |
| P04 |  |  |
| P05 |  |  |
| P06 |  |  |
| P07 |  |  |
| P08 |  |  |
| P09 |  |  |
| P10 |  |  |
| P11 |  |  |
| P12 |  |  |
| P13 |  |  |
| P14 |  |  |
| P15 |  |  |

## Evidence i ograničenja

Sirovi zahtevi/odgovori, validirani DTO-i, SQL trace-ovi, tipizovani parametri, log opsezi i tačne IMDb→MovieLens mapiranja sačuvani su u `.local/planning/reports/v6-phase-05/session-01/capture/`. MAIN-ov pregled stvarnih DTO-a i filmova je `.local/planning/reports/v6-phase-05/session-01/main-actual-assessment.json`; nezavisna SQL revizija Worker B završena je i prihvaćena od MAIN-a: sažetak je `.local/planning/reports/v6-phase-05/session-01/independent/technical-summary.md`, a detaljni nalazi `.local/planning/reports/v6-phase-05/session-01/independent/case-assessments.json` i `.local/planning/reports/v6-phase-05/session-01/independent/pair-overlap.json`. Read-only snimci produkcione i rehearsal baze pre/posle identični su; zaštićeni source/data pinovi nisu promenjeni, backend je ugašen, a tajne nisu prisutne u izlaznim dokazima.

Nema izmena aplikacionog koda, konfiguracije, baze, teze ili produkcionog sistema. Evaluacija ne odobrava cutover ni narednu fazu.