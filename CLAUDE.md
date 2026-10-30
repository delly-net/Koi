# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Koi is a Linux web UI ("Koi Linux Web UI"): browser-based administration of a Linux host with

- authentication against Linux system accounts
- multiple concurrent online terminals
- file management (move / copy / paste)
- multi-file online text editing

## Repository state

Both projects are scaffolded, and login, online terminals, file management and online text editing
are implemented.

- `api/` — ASP.NET Core Minimal API on .NET 10 (`Koi.Api.csproj`): EF Core + SQLite session
  storage, PAM-backed system account authentication, HttpOnly session cookie, health check,
  PTY-backed online terminal over WebSocket, privilege-dropping file management and UTF-8 text
  read/write over REST, and static hosting of the built SPA from `wwwroot` with history-mode
  fallback.
- `ui/` — Vue 3 + TypeScript + Element Plus SPA built with Vite (`package.json`,
  `package-lock.json`): router guard, Pinia auth/editor stores, login page, post-login shell, a
  multi-tab xterm.js terminal page, a file manager page and a multi-tab text editor page.
- `icon/` — application icons; `ui/public/koi_48.png` is a copy used by the UI.

The file manager keeps directory-level actions in the toolbar and collapses every row operation
(open / edit / download / rename / copy / cut / delete) into a single `操作` dropdown per row, so
the action column stays narrow. Directories are opened by clicking the entry name or choosing
`打开`; files listed in the configured text associations are opened in the text editor by clicking
the name or choosing `打开` / `编辑文本`; other files are downloaded via `下载` (double-click no
longer opens entries).

Inside the single `文件管理` workspace tab the file manager is itself tabbed: every inner tab
(`FileBrowserPane` in `ui/src/components/files/`) browses an independent directory and keeps its own
path, listing, selection and loading state. The `+` button opens a new tab at the account home
directory, and a directory row's `操作` menu offers `在新页签打开`. The copy/cut clipboard is a
Pinia store (`ui/src/stores/filesClipboard.ts`) shared by all file tabs, so entries copied or cut in
one tab can be pasted into the directory of another; a successful cut-paste clears it, closing a
tab keeps it, and login/logout reset it. Inner tabs are in-memory and are lost when the workspace
`文件管理` tab is closed.

## Layout

- `api/` — backend. Entry point `Program.cs`; `Auth/` (PAM authenticator, session service,
  middleware, endpoints), `Data/` (EF Core context and entities), `Hosting/` (frontend static
  files and SPA fallback), `Terminal/` (PTY P/Invoke, privileged helper process, session registry,
  WebSocket endpoint), `Files/` (path validation, file helper process, operation service, REST
  endpoints), `appsettings*.json`.
- `ui/` — frontend. Entry `src/main.ts`; `src/api/` (HTTP wrapper, terminal protocol and file API
  types), `src/config/` (runtime config incl. text file associations), `src/stores/` (Pinia auth,
  workspace tab and editor tab state), `src/router/`, `src/views/` (`WorkspaceView.vue` shell plus
  the home / terminal / files / editor panels), `src/components/terminal/` (xterm.js pane),
  `src/components/files/` (per-directory `FileBrowserPane` used by the file manager's inner tabs).
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
- API contract: `GET /api/health` (includes `pamAvailable` / `terminalAvailable` /
  `fileAvailable`), `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`,
  `GET /api/terminal/ws`, and the `/api/files/*` group described below; errors use ProblemDetails.
  Unmatched `/api/**` paths return a 404 ProblemDetails and never fall back to the SPA.
- Terminal protocol (`GET /api/terminal/ws`, WebSocket JSON text frames, camelCase): client sends
  `{"type":"input","data":...}`, `{"type":"resize","cols","rows"}`, `{"type":"ping"}`; the server
  sends `ready` (sessionId/userName/pid/cols/rows), `output` (UTF-8 decoded text), `exit` (code),
  `error` (message) and `pong`. Authentication is the session cookie (401); the handshake is then
  Origin-checked explicitly (403) because it is a GET and therefore bypasses
  `SameOriginMiddleware`; non-Linux or disabled returns 503.
- File management runs every filesystem operation in a privilege-dropping helper process
  (`Files/FileHelper.cs`, hidden argument `--koi-file-helper`), for the same reason terminals do:
  the service runs as root for PAM, so the main process must never read or write user files
  itself. The parent launches the same binary with `Process.Start` (never `fork`), writes one
  framed request (4-byte little-endian length + UTF-8 JSON header) to stdin, and reads one framed
  response from stdout; uploads (`upload`, `writetext`) append the raw file bytes to stdin after
  the header, downloads (`download`, `readtext`) append the raw file bytes to stdout after the
  header. The helper reads the request, then runs
  `initgroups` → `setgid` → `setuid` → `chdir("/")` (order mandatory) and executes exactly one
  operation. Failure exit codes: 111 setgid, 112 initgroups, 113 setuid, 120 unreadable request,
  127 unsupported platform.
- File path policy: every path must be absolute, at most 4096 characters, free of NUL/control
  characters, normalized with `Path.GetFullPath`, resolved through symlinks (`File.ResolveLinkTarget`)
  and contained in `Koi:Files:AllowedRoots`. The default is `["/"]` — whole-filesystem visibility is
  an explicit product decision; the real boundary is the dropped user's OS permissions. Names are
  limited to 255 characters and may not contain `/`. Deletion refuses `/` and configured roots;
  symlinks are deleted/copied as links and never traversed by recursive copy.
- File API (`/api/files`, session cookie required; non-Linux or `Koi:Files:Enabled=false` → 503;
  account missing from `getpwnam_r` → 403): `GET /roots`, `GET /list?path=`,
  `POST /mkdir` `{path}`, `POST /touch` `{path}`, `POST /rename` `{path,newName}`,
  `POST /delete` `{paths,recursive}`, `POST /copy` / `POST /move`
  `{sources,target,overwrite}`, `POST /upload?path=&overwrite=` (multipart field `file`),
  `GET /download?path=`, `GET /text?path=` and `POST /text` `{path,content,expectedModifiedAt,hasBom}`.
  Errors are ProblemDetails: 400 invalid path/request or non-UTF-8 text, 403 permission or
  outside roots, 404 missing, 409 conflict (external modification or non-empty directory),
  413 too large, 504 helper timeout. Listing is capped by `MaxListEntries` and reports
  `truncated`. `GET /text` returns `{path,content,modifiedAt,size,hasBom}`; `POST /text` returns
  `{ok,modifiedAt,size}` and maps the helper's `conflict` code to 409.
- Online text editing is layered on the same helper: `readtext` reuses the download framing
  (header + raw bytes) and `writetext` reuses the upload framing. Only UTF-8 is supported
  (optional UTF-8 BOM is detected and preserved; invalid UTF-8 or NUL bytes → 400). Reads are
  capped by `Koi:Files:MaxTextFileBytes`; when the helper's header reports a larger size the
  parent kills the helper before draining, so the response is a 413 rather than a timeout.
  Saves are optimistic-concurrency writes: the request carries the `modifiedAt` observed at read
  time, the helper compares it with the current mtime and returns `conflict` (409) on mismatch;
  `expectedModifiedAt=0` forces an overwrite. The helper writes a temp file in the same directory,
  copies the original Unix mode onto it, then atomically renames it over the target (symlinks are
  resolved and their target is replaced, never the link itself).
- File config keys: `Koi:Files:Enabled`, `AllowedRoots`, `MaxUploadBytes` (default 100 MiB;
  `Program.cs` raises the Kestrel request-body limit and `FormOptions.MultipartBodyLengthLimit`
  accordingly), `MaxListEntries`, `MaxTextFileBytes` (online text read/write cap, default 5 MiB),
  `HelperTimeoutSeconds` (metadata and text read/write ops, 120 s),
  `ListTimeoutSeconds` (directory listing, 300 s), `TransferTimeoutSeconds` (delete/copy/move,
  1800 s), `StreamIdleTimeoutSeconds` (upload/download with no data flowing, 120 s) and
  `StreamMaxSeconds` (upload/download total cap, 0 = unlimited). Uploads are written to a temp file
  in the destination directory and renamed into place, so an interrupted upload leaves no partial
  target.
- File helper framing has two separate limits: requests (main → helper) are capped at 64 KiB,
  responses (helper → main) at 32 MiB because a directory listing carries every entry in one JSON
  frame. Never stop reading a frame without terminating the helper: if the parent rejects an
  oversized length and simply waits for exit, the helper blocks writing the unread payload into the
  pipe and the operation only "finishes" when the timeout fires (this used to surface as a spurious
  504 when loading directories with a few hundred entries). Upload and download timeouts are
  idle-based and re-armed on every chunk, so a slow but active client is not killed by a wall clock.
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
- The UI is a separate SPA communicating with the API over HTTP and WebSocket. Keep any new
  endpoint and protocol contracts documented here.
- Frontend layout: after login the app is a single workspace shell (`views/WorkspaceView.vue`,
  route `/`) whose top bar owns the logo + title, the panel tabs and the user name + logout, so the
  tab row no longer takes a separate line and the content area gains its height. The tabs are flat
  (non-card) text tabs inside that bar. The `el-tabs` there is navigation only — its panes are
  empty and `.el-tabs__content` is hidden; the four modules mount in the sibling `.panels`
  container, keyed by `stores/workspace.ts` (`home` / `terminal` / `files` / `editor`). `首页` is
  the fixed entry tab and cannot be closed; the others are opened on demand (home cards, or opening
  a text file from the file manager) and can be closed, and closing the active tab returns to 首页.
  Tab state is in-memory only: it is not reflected in the URL, a browser refresh lands on 首页, and
  logout resets it. A panel is mounted while its key is in `workspace.tabs` (`v-if`) and hidden with
  `v-show` when inactive, so switching tabs preserves module state (terminal WS/PTY, current
  directory, editor tabs) while closing a tab unmounts its panel and therefore closes that
  terminal. `/terminal`, `/files` and `/editor` are no longer routes — they fall through the
  catch-all to `/`. `TerminalView` still passes `active` to `TerminalPane` so a hidden terminal
  is refitted and focused when its tab becomes visible.
- Terminal pane sizing (`components/terminal/TerminalPane.vue`): the element FitAddon measures
  (`.terminal-host`) must stay free of padding — `proposeDimensions()` only subtracts the padding of
  `.xterm` itself, so host padding is counted as usable height, one row is over-estimated and the
  bottom row ends up clipped to half a line. The 8 px inset therefore lives on `.terminal-pane`.
  `fitTerminal()` runs `trimClippedRows()` after `fitAddon.fit()`: it compares the rendered
  `.xterm-screen` height with the `.xterm` client height and drops rows (never adds) until nothing
  overflows, and the pane is refitted once `document.fonts.ready` resolves because font metrics set
  the cell height. Hidden panes (size < 24 px) are still skipped.
- The 首页 panel (`views/HomeView.vue`) shows three module cards whose entry buttons share one
  style: all `type="primary"`, full width of the card body, and pinned to the card bottom
  (`p { flex: 1 }` plus a column flex body) so the three buttons sit on the same horizontal line
  even though the descriptions differ in length. Keep new home cards on the same pattern.
- Frontend runtime config: `ui/public/conf/setting.json` (`baseUrl` = backend root without `/api`,
  empty string = same origin) is loaded in `src/main.ts` before the router is installed. A missing
  or invalid file falls back to same origin without blocking startup. After `npm run build`, edit
  `ui/dist/conf/setting.json` to retarget the backend without rebuilding. When the backend serves
  the SPA, this file is deliberately sent with `no-store` so edits take effect immediately.
  `fileAssociations.text` in the same file lists the extension/name rules (`".txt"`, `".md"`,
  `"dockerfile"`, …) that the UI treats as text: such files open in the editor from the row menu
  `打开` / `编辑文本` or by clicking the name. Missing or invalid rules fall back to a built-in
  list; an explicit empty array means “no text files”. Production deploys replace
  `dist/conf/setting.json` with `deploy/linux/conf/setting.json`, so keep both copies in sync.
- The text editor (`ui/src/views/EditorView.vue`, the `editor` workspace tab) keeps its tabs in
  `src/stores/editor.ts`, so switching workspace tabs preserves unsaved content; a browser refresh
  loses it and `beforeunload` only warns. Saves check the `modifiedAt` returned by the read, and a 409
  lets the user overwrite (`expectedModifiedAt=0`) or reload. The editor is a plain monospace
  textarea (no new npm dependency); large files near `MaxTextFileBytes` may feel slow.

## Knowledge base

This repository is tracked in the EAZY.RAG project "Koi项目" (project ID 9); its execution specs,
notes, and dependency docs live there rather than in the repo. The MCP connection is configured in
`.mcp.json`, which is gitignored.
