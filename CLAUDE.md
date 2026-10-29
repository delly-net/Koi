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
  storage, PAM-backed system account authentication, HttpOnly session cookie, health check,
  PTY-backed online terminal over WebSocket, and static hosting of the built SPA from `wwwroot`
  with history-mode fallback.
- `ui/` — Vue 3 + TypeScript + Element Plus SPA built with Vite (`package.json`,
  `package-lock.json`): router guard, Pinia auth store, login page, post-login shell, and a
  multi-tab xterm.js terminal page.
- `icon/` — application icons; `ui/public/koi_48.png` is a copy used by the UI.

## Layout

- `api/` — backend. Entry point `Program.cs`; `Auth/` (PAM authenticator, session service,
  middleware, endpoints), `Data/` (EF Core context and entities), `Hosting/` (frontend static
  files and SPA fallback), `Terminal/` (PTY P/Invoke, privileged helper process, session registry,
  WebSocket endpoint),
  `appsettings*.json`.
- `ui/` — frontend. Entry `src/main.ts`; `src/api/` (HTTP wrapper and terminal protocol types),
  `src/config/` (runtime config), `src/stores/` (Pinia), `src/router/`, `src/views/`,
  `src/components/terminal/` (xterm.js pane).
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

To preview the production bundle served by the backend, copy `ui/dist/*` into `api/wwwroot/` and
run the API (the directory is gitignored). `deploy/linux/ui-build.sh` performs the same copy into
the publish directory `wwwroot`.

`deploy/linux/publish.sh` copies the publish output into `/opt/delly-net/Koi`; `deploy/linux/install.sh`
(run as root) registers that directory as the `koi.service` systemd unit — `WorkingDirectory` is the
program directory so `koi.db` and `wwwroot` resolve there — then enables and starts/restarts it. The
unit runs as root because `pam_unix` needs privileged access to the account database; terminal
children do not inherit it — they drop to the logged-in user inside the PTY.

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
  `Koi:Auth:AllowedOrigins` (Development allows the Vite dev server). WebSocket handshakes are GET
  requests and must call `SameOriginMiddleware.IsAllowedOrigin` themselves.
- Never `fork` a managed process on Linux. After `fork` the .NET runtime keeps only the calling
  thread (GC, threadpool, diagnostics are gone), so any managed code — including the P/Invoke
  return trampoline — may fault, which showed up as SIGSEGV (exit code 139) when the PTY child was
  created with `forkpty` from managed code. Ordering or pre-warming does not make it safe. Terminal
  children are therefore created by `Process.Start` (native fork/exec):
  - the main process allocates the PTY master with `posix_openpt` + `grantpt` + `unlockpt` +
    `ptsname_r`, marks the master `FD_CLOEXEC`, and sets the initial `TIOCSWINSZ`;
  - it then re-launches *this same binary* with the hidden first argument `--koi-pty-helper`
    (`PtyHelper.ArgumentName`), passing slave path, uid, gid, user name, home and the shell
    command line. `Program.cs` diverts on that argument before `WebApplication.CreateBuilder`, so
    the helper never starts Kestrel;
  - the helper (a normal, unforked .NET process) opens the slave, `setsid`, `TIOCSCTTY`, drops
    privileges (`initgroups` → `setgid` → `setuid`, `initgroups` strictly before `setuid`),
    `chdir`s home, checks `access(shell, X_OK)`, writes one readiness byte (`PtyHelper.ReadyByte`)
    to the redirected stdout, `dup2`s the slave onto fds 0/1/2, restores `SIGPIPE` and the signal
    mask, then `execve`s the shell with a clean environment;
  - the parent waits up to 5 s for the readiness byte. No byte means failure: the helper exits
    with a documented code (`PtyHelper.*FailedExitCode`: 111 setgid, 112 initgroups, 113 setuid,
    120 slave open, 121 setsid, 122 dup2, 127 exec) which is mapped to a Chinese message.
  Consequences to preserve: the helper must keep running as the service account (uid/gid drop
  requires privilege); `export DOTNET_EnableDiagnostics=0` is set for the helper; exit status and
  reaping come from `System.Diagnostics.Process` (`HasExited` / `ExitCode` / `WaitForExit`) rather
  than `waitpid`, so do not reintroduce `waitpid` or `_exit` in the parent.
- Terminal sessions live in memory (`TerminalSessionManager`): disconnecting destroys the PTY and
  child, logout closes that user's terminals, host shutdown reaps them all. Concurrency is capped by
  `Koi:Terminal:MaxSessionsPerUser` / `MaxSessionsTotal`. Config keys: `Koi:Terminal:Enabled`,
  `Shell` (empty = account login shell), `ShellArguments`, `MaxInputBytes`, `DefaultCols`,
  `DefaultRows`, `CloseTimeoutSeconds`. `PosixPty` relies on `AllowUnsafeBlocks` (only that file
  uses pointers) and calls `posix_openpt`/`grantpt`/`unlockpt`/`ptsname_r` directly from libc —
  no `forkpty`, no libutil, no C toolchain on the build host.
- API contract: `GET /api/health` (includes `pamAvailable` / `terminalAvailable`),
  `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`, `GET /api/terminal/ws`;
  errors use ProblemDetails. Unmatched `/api/**` paths return a 404 ProblemDetails and never fall
  back to the SPA.
- Terminal protocol (`GET /api/terminal/ws`, WebSocket JSON text frames, camelCase): client sends
  `{"type":"input","data":...}`, `{"type":"resize","cols","rows"}`, `{"type":"ping"}`; the server
  sends `ready` (sessionId/userName/pid/cols/rows), `output` (UTF-8 decoded text), `exit` (code),
  `error` (message) and `pong`. Authentication is the session cookie (401); the handshake is then
  Origin-checked explicitly (403) because it is a GET and therefore bypasses
  `SameOriginMiddleware`; non-Linux or disabled returns 503.
- Static hosting: `Koi:StaticFiles:RootPath` (default `wwwroot`, relative paths resolve against the
  content root) and `Koi:StaticFiles:Enabled` (default `true`). `UseKoiStaticFiles()` runs before
  the same-origin and session middleware so assets bypass the session lookup. When `index.html` is
  absent the app logs a warning and serves only the API. `MapKoiFallbacks()` maps `/api/{**path}`
  to 404 and returns `index.html` for GET/HEAD requests whose path has no extension, which supports
  Vue Router history mode while missing `.js`/`.css` files still return 404. `index.html` and
  `conf/setting.json` are served `no-cache`; `/assets/**` gets a one-year immutable cache.
- Runtime state lives in `api/koi.db` (SQLite, created with `EnsureCreated` — no migrations yet).
  The file is gitignored. `hamster.db` entries in `api/.gitignore` are template leftovers.
- The SQLite provider does not translate `DateTimeOffset` comparisons, so `KoiDbContext`
  maps every `DateTimeOffset` property to UTC ticks (`long`) via `UtcTicksConverter`
  (`ConfigureConventions`). Do not remove it, and do not add per-property conversions that
  bypass it. Changing the storage format leaves existing rows unreadable: delete `koi.db`
  after such a change (sessions are ephemeral and users simply log in again).
- The UI is a separate SPA communicating with the API over HTTP and WebSocket. As terminal-session
  and file-operation endpoints are added, keep their contracts documented.
- Frontend runtime config: `ui/public/conf/setting.json` (`baseUrl` = backend root without `/api`,
  empty string = same origin) is loaded in `src/main.ts` before the router is installed. A missing
  or invalid file falls back to same origin without blocking startup. After `npm run build`, edit
  `ui/dist/conf/setting.json` to retarget the backend without rebuilding. When the backend serves
  the SPA, this file is deliberately sent with `no-store` so edits take effect immediately.

## Knowledge base

This repository is tracked in the EAZY.RAG project "Koi项目" (project ID 9); its execution specs,
notes, and dependency docs live there rather than in the repo. The MCP connection is configured in
`.mcp.json`, which is gitignored.
