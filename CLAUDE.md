# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Koi is a Linux web UI ("Koi Linux Web UI"): browser-based administration of a Linux host with

- authentication against Linux system accounts
- multiple concurrent online terminals
- file management (move / copy / paste)
- multi-file online text editing

## Repository state

The repository is currently a **skeleton**. It contains `README.md`, `LICENSE`, `icon/`, and the
`api/` and `ui/` directories; `api/` and `ui/` hold only their `.gitignore` files. There is no
solution, project file, or package manifest yet — verify what has actually been scaffolded before
assuming any build command works.

## Planned layout

- `api/` — backend, ASP.NET Core Minimal API on .NET 10
- `ui/` — frontend, Vue 3 + TypeScript + Element Plus (Vite-based, per `ui/.gitignore`)
- `icon/` — application icons (48 / 256 / full size)

## Commands

No project-specific scripts exist yet. Once the projects are scaffolded, the conventional entry
points will be:

Backend, from `api/`:

- `dotnet build`
- `dotnet run` (or `dotnet run --project <api>.csproj` from the repo root)
- `dotnet test`

Frontend, from `ui/`:

- `npm install`
- `npm run dev`
- `npm run build`

Check `package.json` once it exists for the actual script names (including tests).

Toolchain installed on the current dev machine: .NET SDK 10.0.401, Node v22.23.2, npm 10.9.8.

## Architecture constraints

- The target platform is Linux. Login verifies against the Linux system account database and the
  terminal feature needs PTYs (POSIX), so those paths cannot be fully exercised on the Windows 11
  dev machine — verify them on a Linux host.
- The UI is a separate SPA communicating with the API over HTTP and WebSocket. As terminal-session
  and file-operation endpoints are added, keep their contracts documented.
- `api/.gitignore` already excludes a SQLite database (`hamster.db`), indicating local persistence
  of runtime state is planned.

## Knowledge base

This repository is tracked in the EAZY.RAG project "Koi项目" (project ID 9); its execution specs,
notes, and dependency docs live there rather than in the repo. The MCP connection is configured in
`.mcp.json`, which is gitignored.
