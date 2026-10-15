# CineKros frontend

React, TypeScript, and Vite client for the stateless film search flow. `src/App.tsx` builds the request and handles the response; `src/apiConfiguration.ts` validates the optional API origin.

## Request and configuration

The client sends `POST /api/recommendations` with JSON shaped as `{ "language": "sr" | "en", "message": "..." }`. When `VITE_API_BASE_URL` is unset or empty, the client uses the relative `/api/recommendations` path. Set it to an absolute API origin when the frontend and API use different origins; the value cannot contain a path, query, fragment, or credentials. HTTPS is required except for HTTP loopback origins in development. This is a public, build-time Vite variable, so changing it requires rebuilding the frontend.

The API separately controls cross-origin access with `CINEKROS_ALLOWED_ORIGINS`, a comma-separated list of exact frontend origins. No cross-origin access is granted when it is unset, and credentials are not enabled. HTTPS is required outside Development; Development also permits HTTP loopback origins.

During Vite development, `/api` is proxied to the local backend at `http://127.0.0.1:5179`.

## Response handling

The client accepts business alerts, technical errors, or a movie response containing at most ten distinct movies and `{ count, partial }` metadata. It checks HTTP status, required fields, known alert/error codes, IMDb and poster URL formats, duplicate IMDb URLs, and consistency between `count`, `partial`, and the returned list before displaying results. A 1–9 movie response is shown as partial; a no-results alert returns the user to the prompt. Movie cards link to IMDb in a new tab. Serbian and English UI strings are selected independently of the film metadata; the request carries the user's selected language.

## Local commands

Run `npm ci`, then use `npm run dev`, `npm run build`, `npm run lint`, or `npm test` from this directory.
