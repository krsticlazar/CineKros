# CineKros.Evaluation.Tests

## Čemu služi

Unit/fixture testovi za `CineKros.Evaluation`. Proveravaju čitanje predloženih upita, izbor režima, odvojeno poređenje parsera i retrieval metrika, kao i oblik izveštaja. Ne predstavljaju finalnu akademsku evaluaciju niti automatski pozivaju Gemini.

## Gde se uklapa u CineKros

`CineKros.Evaluation → CineKros.Evaluation.Tests`. Test projekat direktno referencira evaluation CLI; njegove testne kalkulacije se vrše nad kontrolisanim primerima.

## Najvažniji delovi

- `EvaluationLogicTests.cs` — hard-filter compliance (uključujući null i striktni rating), parser checklist poređenja, predložene human-score formule i serijalizaciju izveštaja.

## Šta bih rekao profesorki

> „Ovi testovi potvrđuju da evaluacioni alat računa ono što kaže da računa. Posebno odvajaju greške razumevanja upita od grešaka retrieval-a, dok finalni eksperimentalni protokol ostaje posebna odluka.“
