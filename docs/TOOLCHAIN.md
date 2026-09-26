# Project Layout and Toolchain — Foundation v1

Status: approved 2026-09-24. This document pins the foundation for contract fixtures and fake-backed frontend/backend work. It does not install packages or create projects by itself. Database, ETL and live AI integration packages are pinned separately before those components are implemented.

## Layout

| Purpose | Path |
| --- | --- |
| .NET solution | `CineKros.slnx` |
| SDK/test policy | `global.json` |
| ASP.NET Core API | `src/backend/CineKros.Api/CineKros.Api.csproj` (`CineKros.Api`) |
| API tests | `tests/CineKros.Api.Tests/CineKros.Api.Tests.csproj` (`CineKros.Api.Tests`) |
| React application | `src/frontend/` |
| Public contract fixtures | `Testiranje/fixtures/api/` |
| Fixture validator | `scripts/validate-api-fixtures.mjs`, root `package.json` and `package-lock.json` |
| Later ETL console/tests | `database/etl/CineKros.Etl/`, `tests/CineKros.Etl.Tests/` |

Keep one API project and one frontend package; do not add empty architecture layers or a router/UI framework for the initial foundation. The [public HTTP contract](API_CONTRACT.md) is the frontend/backend boundary. Small parser, embedding and search interfaces belong in the backend only when the corresponding behavior is specified. The initial API uses deterministic fake providers in tests and makes no Gemini, TMDB or database call.

## .NET and test packages

Use the installed .NET SDK `10.0.400` and target `net10.0`. The root `global.json` pins the exact SDK and Microsoft Testing Platform:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "disable"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  },
  "msbuild-sdks": {
    "MSTest.Sdk": "4.4.0"
  }
}
```

Use the built-in ASP.NET Core `web` template for the minimal API. The API project needs no external NuGet package for its fake-only foundation. The test project uses `<Project Sdk="MSTest.Sdk">` with the version supplied by `global.json`, plus an exact `Microsoft.AspNetCore.Mvc.Testing` `10.0.12` reference for in-memory HTTP tests. Its project references `CineKros.Api`. Every .NET project sets `RestorePackagesWithLockFile=true`; commit generated `packages.lock.json`. After first restore, verify with `dotnet restore --locked-mode`, `dotnet build` and `dotnet test`. Do not add or upgrade a package to resolve a failure without revising this specification.

This choice follows Microsoft's [SDK pinning](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json), [MSTest SDK](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-getting-started), [integration-test host](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) and [NuGet lock](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files) guidance. Exact package identities are [MSTest.Sdk 4.4.0](https://www.nuget.org/packages/MSTest.Sdk/4.4.0) and [Microsoft.AspNetCore.Mvc.Testing 10.0.12](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing/10.0.12).

## Node packages

The verified local runtime is Node `24.21.0` with npm `11.12.1`. Both package manifests use `"engines": { "node": ">=24 <25" }`, exact direct dependency versions without `^` or `~`, and committed `package-lock.json` files. Subsequent installations use `npm ci`.

The root package is private and exists only for public-contract fixtures. Its dev dependencies are `ajv` `8.17.1` and `ajv-formats` `3.0.1`. The validator uses Ajv's Draft 2020-12 mode plus formats to check accepted schema-valid and schema-invalid fixtures. Backend HTTP tests check transport rules, duplicate JSON keys, Unicode trimming/counting and catalogue-level invariants that JSON Schema alone cannot express.

Create the frontend from official `create-vite` `9.2.1` with the `react-ts` template, then replace its version ranges with these exact pins:

| Kind | Package | Version |
| --- | --- | --- |
| Runtime | `react`, `react-dom` | `19.3.0` each |
| Build | `vite` | `8.3.0` |
| Build | `@vitejs/plugin-react` | `6.1.1` |
| Build | `typescript` | `6.0.2` |
| Types | `@types/node` | `24.13.3` |
| Types | `@types/react`, `@types/react-dom` | `19.3.0` each |
| Lint | `oxlint` | `1.85.0` |
| Tests | `vitest` | `5.0.1` |
| Tests | `jsdom` | `30.1.1` |
| Tests | `@testing-library/react` | `16.3.3` |
| Tests | `@testing-library/dom` | `10.4.2` |
| Tests | `@testing-library/user-event` | `14.6.7` |
| Tests | `@testing-library/jest-dom` | `7.0.1` |

Use scripts `dev`, `build`, `lint`, `test` (`vitest run`) and `preview`. Initial verification is `npm ci`, `npm run build`, `npm run lint`, `npm test`. The pinned generator and template are from [Vite](https://vite.dev/guide/); the selected [Vite](https://www.npmjs.com/package/vite), [React](https://www.npmjs.com/package/react), [Vitest](https://vitest.dev/guide/migration/) and [Testing Library](https://www.npmjs.com/package/%40testing-library/react) versions/peer requirements were checked on 2026-09-24. No worker chooses a newer package during scaffolding.

## Local foundation behavior

- Frontend development server: port `5173`. Backend development server: loopback HTTP port `5179`. Vite proxies `/api` to `http://127.0.0.1:5179`, so no broad development CORS policy is needed. These are local-only addresses.
- The backend owns secrets. ASP.NET Core does not automatically read `.env`; fake-backed foundation tests need no local credentials. A later launcher will import only documented keys from the ignored `.env` into its child backend process, without printing values. The loader grammar is fixed before live adapters; no secret is placed in Vite/client variables or fixtures.
- Fake-only foundation requests have no provider/network retries. Propagate cancellation through application interfaces. External timeout/retry budgets remain a provider gate.
- Diagnostic logs may contain a request correlation ID, contract revision, stage duration and stable error code. They must not contain raw queries, parser output, embeddings, API keys or connection strings. Academic experiment logging is designed later.
- Keep the existing SVG logo as the frontend source asset. A specific visual sheet is approved before UI implementation. No live API, database setup or academic evaluation is implied by this foundation.
