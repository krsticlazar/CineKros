# CineKros.E5.Generator.Tests

## Čemu služi

Testira offline `CineKros.E5.Generator`. Većina provera koristi kontrolisani izvor vektora za deterministički checkpoint/resume; lokalni ONNX fixture proverava i stvaran 768D izlaz bez provider poziva.

## Gde se uklapa u CineKros

`CineKros.E5.Generator → CineKros.E5.Generator.Tests`. Test projekat direktno referencira generator; generator dalje referencira embedding biblioteku i validator kataloga.

## Najvažniji delovi

- `GeneratorTests.cs` — selektivni resume, očuvanje uspešnih zapisa, oporavak oštećenog checkpointa i odbijanje nekompatibilnog profila.
- Isti fajl proverava validaciju finalnog kataloga, determinističko objavljivanje, otkazivanje i granice CLI argumenata.

## Šta bih rekao profesorki

> „Generator ne mora da ponavlja hiljade već završenih vektora posle prekida. Testovi dokazuju da koristi samo kompatibilne checkpoint zapise i da nepotpun izlaz nikada ne proglasi finalnim.“
