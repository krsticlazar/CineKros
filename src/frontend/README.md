# CineKros frontend

Fake-backed React/TypeScript/Vite frontend for the CineKros film-search flow.

The default submit path sends `{ language, message }` as JSON to `POST /api/recommendations`. During development, Vite proxies `/api` to the local backend at `http://127.0.0.1:5179`. `src/mockApi.ts` is a synthetic fixture for frontend-only use and is not the runtime default. Run `npm ci`, then `npm run dev`, `npm run build`, `npm run lint`, or `npm test`.
