# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Koi is a Linux web UI ("Koi Linux Web UI"): browser-based administration of a Linux host with

- authentication against Linux system accounts
- multiple concurrent online terminals
- file management (move / copy / paste)
- multi-file online text editing

## Repository state

Both projects are scaffolded and the login path is implemented.

- `api/` — ASP.NET Core Minimal API on .NET 10 (`Koi.Api.csproj`): EF Core + SQLite session
  storage, PAM-backed system account authentication, HttpOnly session cookie, health check.
- `ui/` — Vue 3 + TypeScript + Element Plus SPA built with Vite (`package.json`,
  `package-lock.json`): router guard, Pinia auth store, login page, post-login shell.
- `icon/` — application icons; `ui/public/koi_48.png` is a copy used by the UI.

## Layout

- `api/` — backend. Entry point `Program.cs`; `Auth/` (PAM authenticator, session service,
  middleware, endpoints), `Data/` (EF Core context and entities), `appsettings*.json`.
- `ui/` — frontend. Entry `src/main.ts`; `src/api/` (HTTP wrapper), `src/stores/` (Pinia),
  `src/router/`, `src/views/`.
- `icon/` — application icons (48 / 256 / full size).

## Commands

Backend, from `api/`:

- `dotnet build`
- `dotnet run` — uses `Properties/launchSettings.json` (Development, `http://localhost:5000`)
- `dotnet test` — no test project yet

Frontend, from `ui/`:

- `npm install`
- `npm run dev` — Vite on port 5173, proxies `/api` to `http://127.0.0.1:5000`
- `npm run build` — runs `vue-tsc -b` then `vite build`, output in `ui/dist`
- `npm run preview`

Toolchain installed on the current dev machine: .NET SDK 10.0.401, Node v22.23.2, npm 10.9.8.
TypeScript is pinned to 5.9 because `vue-tsc` 3.x does not yet support TypeScript 7.

## Architecture constraints

- The target platform is Linux. Login verifies against PAM via P/Invoke (`libpam.so.0`); on other
  platforms the login endpoint returns `503`. The terminal feature needs PTYs (POSIX), so those
  paths cannot be fully exercised on the Windows 11 dev machine — verify them on a Linux host.
- `pam_unix` generally needs privileges to read the system account database (root or a setuid
  helper). Deployment must grant controlled access to the service account; do not run the backend
  as root by default.
- Session model: HttpOnly cookie (`koi_session`, `SameSite=Lax`) + server-side session in SQLite.
  Only the SHA-256 hash of the token is stored. WebSocket endpoints must reuse this session check.
- Same-origin enforcement runs on unsafe HTTP methods; additional origins come from
  `Koi:Auth:AllowedOrigins` (Development allows the Vite dev server).
- API contract: `GET /api/health`, `POST /api/auth/login`, `POST /api/auth/logout`,
  `GET /api/auth/me`; errors use ProblemDetails.
- Runtime state lives in `api/koi.db` (SQLite, created with `EnsureCreated` — no migrations yet).
  The file is gitignored. `hamster.db` entries in `api/.gitignore` are template leftovers.
- The UI is a separate SPA communicating with the API over HTTP and WebSocket. As terminal-session
  and file-operation endpoints are added, keep their contracts documented.

## Knowledge base

This repository is tracked in the EAZY.RAG project "Koi项目" (project ID 9); its execution specs,
notes, and dependency docs live there rather than in the repo. The MCP connection is configured in
`.mcp.json`, which is gitignored.
