# CineKros frontend

React/TypeScript/Vite frontend for the CineKros film-search flow.

API poziv je u `src/frontend/src/App.tsx` (`submitHttp`), a formiranje endpoint URL-a u `src/frontend/src/apiConfiguration.ts`. Frontend šalje `{ language, message }` backend-u na `POST /api/recommendations`; nema zasebnog API klijenta.

The default submit path sends `{ language, message }` as JSON to `POST /api/recommendations`. During development, Vite proxies `/api` to the local backend at `http://127.0.0.1:5179`. Run `npm ci`, then `npm run dev`, `npm run build`, `npm run lint`, or `npm test`.

Set the public, build-time `VITE_API_BASE_URL` to an absolute API origin when the frontend and API are hosted on different origins, for example `https://api.example.com`; changing it requires rebuilding the frontend. The value must not include a path, query, fragment, or credentials. HTTPS is required outside development; development also permits HTTP loopback origins. When unset or empty, the frontend keeps the relative `/api/recommendations` endpoint.

Configure the API process with `CINEKROS_ALLOWED_ORIGINS`, a comma-separated list of exact frontend origins, for example `https://cinekros.example.com,https://admin.example.com`. Origins are HTTPS outside Development; development also permits HTTP loopback origins. When unset, no cross-origin access is granted. The policy does not allow credentials.
