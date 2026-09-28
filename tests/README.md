# Tests

The .NET test projects are included in `CineKros.slnx`. Run them after locked restore and build:

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet test CineKros.slnx --no-restore
```

The active HTTP contract examples and isolated validator are in `fixtures/`. From the repository root, run `npm ci --prefix tests/fixtures` once, then `npm run check:fixtures --prefix tests/fixtures`.

Routine checks use fixtures, fakes or small synthetic data and do not require live Gemini requests. Live provider checks and academic evaluation are separate activities; no evaluation results are claimed here.
