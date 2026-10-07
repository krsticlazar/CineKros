# CineKros.Evaluation

## Uloga

Samostalni CLI za ponovljive, istraživačke provere parsera i retrieval-a; nije deo web zahteva. Čita unapred definisane slučajeve i zapisuje JSON i Markdown izveštaj. Sam izveštaj niti prisustvo opcionih ljudskih ocena ne predstavlja završenu akademsku evaluaciju: metodologija, uzorak i analiza moraju biti posebno utvrđeni.

## Dva režima

- `retrieval` proverava strukturisanu, semantičku i hibridnu pretragu nad lokalnom bazom uz E5 query vektor. Zahteva eksplicitne ID-jeve slučajeva, dostupnu bazu/model i odgovarajuće podatke. Ovo je tehničko merenje trenutnog toka.
- `parser` poredi Gemini izlaz sa očekivanim checklist/DTO vrednostima. CLI zahteva `--live-parser` i `--max-live-calls`; to su stvarni provider pozivi, ne suvi probni režim.

`CliOptions.Parse` u [Program.cs](Program.cs) proverava CLI argumente, izbor slučajeva, izlazne putanje i eksplicitno odobrenje live parser poziva.

## Kako čitati kod

- [EvaluationModels.cs](EvaluationModels.cs) opisuje slučajeve, očekivane rezultate i izveštaj.
- [EvaluationLogic.cs](EvaluationLogic.cs) validira query set, proverava hard-filter usklađenost i računa metrike samo kada postoje potrebne ljudske ocene.
- [EvaluationRunner.cs](EvaluationRunner.cs) izvršava retrieval slučajeve preko `MovieSearchRepository` i [CineKros.Embedding](../../embedding/CineKros.Embedding/README.md); `EvaluationReports.Write` čuva izlaze.
- `Program` bira režim i sastavlja rezultate. Parser putanja poziva Gemini samo uz eksplicitne CLI zastavice.

Ovaj alat daje dokaz o ponašanju konkretnog skupa slučajeva i konfiguracije. Ne zamenjuje planiranu akademsku evaluaciju, ljudsko ocenjivanje niti zaključke o opštoj kvaliteti sistema.
