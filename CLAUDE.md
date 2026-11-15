# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Koi is a Linux web UI ("Koi Linux Web UI"): browser-based administration of a Linux host with

- authentication against Linux system accounts
- multiple concurrent online terminals
- file management (move / copy / paste)
- multi-file online text editing
- Linux systemd service management (state, lifecycle, boot enablement, logs)

## Repository state

Both projects are scaffolded, and login, online terminals, file management and online text editing
are implemented.

- `api/` — ASP.NET Core Minimal API on .NET 10 (`Koi.Api.csproj`): EF Core + SQLite session
  storage, PAM-backed system account authentication, HttpOnly session cookie, health check,
  PTY-backed online terminal over WebSocket, privilege-dropping file management and UTF-8 text
  read/write over REST (file settings stored in the SQLite generic settings table and edited by
  root through a settings drawer), docker-CLI-backed container/image management restricted to configured
  groups, systemctl-backed service management (list/detail/lifecycle/enablement/journalctl logs)
  restricted to configured groups, apt/dnf/yum/pacman/zypper-backed application management
  (installed/search/updates, a SQLite installed-application cache with paginated status
  filtering, background jobs for install/remove/upgrade/refresh, and software source management)
  restricted to configured groups, Linux `/proc`-backed server status
  (CPU/memory/disk/network) for all signed-in users, and static hosting of the built SPA from
  `wwwroot` with history-mode fallback.
- `ui/` — Vue 3 + TypeScript + Element Plus SPA built with Vite (`package.json`,
  `package-lock.json`): router guard, Pinia auth/editor stores, login page, post-login shell, a
  multi-tab xterm.js terminal page, a file manager page with a root-only settings drawer, a multi-tab text editor page, a
  container manager page, a systemd service manager page, an application manager page with a
  live job-log dialog, and a homepage server-status panel refreshed every 5 seconds.
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

The `文件管理` page top-right toolbar carries a circular `设置` gear (`Setting`,
`aria-label="文件管理设置"`) **only to root**: on mount the page silently calls
`GET /api/files/settings`; success shows the button, any failure (403 for non-root, 503 on
non-Linux) hides it without an error toast. The click opens a 480 px `el-drawer` editing every
file setting — enable switch, allowed roots as a multi-select/creatable list of absolute paths,
upload/list/online-text limits (byte values edited in MiB) and the five timeouts. `保存` sends
`PUT /api/files/settings`, shows `设置已保存并立即生效`, updates the local snapshot and silently
refreshes the home directory; saving `Enabled=false` keeps the open tabs but shows a persistent
`文件管理不可用` alert (also shown when the initial `GET /api/files/roots` returns 503), cleared
when the setting is re-enabled. Settings live in the SQLite
generic settings table (`Feature = files`).

## Layout

- `api/` — backend. Entry point `Program.cs`; `Auth/` (PAM authenticator, session service,
  middleware, endpoints), `Configuration/` (Linux external config loading and auto-creation),
  `Data/` (EF Core context and entities), `Hosting/` (frontend static files and SPA fallback),
  `Terminal/` (PTY P/Invoke, privileged helper process, session registry, WebSocket endpoint),
  `Files/` (path validation, settings service/validator, file helper process, operation service,
  REST endpoints),
  `Docker/` (docker CLI runner, group authorization guard, REST endpoints),
  `Services/` (systemctl/journalctl runner, group authorization guard, REST endpoints),
  `Packages/` (package-manager backends, command runner, in-memory job manager, source-file
  service, group authorization guard, REST endpoints),
  `Monitor/` (Linux `/proc` and statvfs status collector, REST endpoint), `appsettings*.json`.
- `ui/` — frontend. Entry `src/main.ts`; `src/api/` (HTTP wrapper, terminal protocol, file API,
  docker API, service API and package API types), `src/config/` (runtime config incl. text file
  associations), `src/stores/` (Pinia auth, workspace tab and editor tab state), `src/router/`,
  `src/views/` (`WorkspaceView.vue` shell plus the home / terminal / files / editor / docker /
  service / package panels),
  `src/components/terminal/` (xterm.js pane), `src/components/files/` (per-directory
  `FileBrowserPane` used by the file manager's inner tabs), `src/components/system/`
  (`ServerStatus.vue`, the homepage status cards and 5-second polling).
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
program directory so `koi.db` and `wwwroot` resolve there — then enables and starts/restarts it. It
also pre-creates `/etc/koi/appsettings.json` from the program directory copy without overwriting an
existing file. The unit runs as root because `pam_unix` needs privileged access to the account
database; terminal children do not inherit it — they drop to the logged-in user inside the PTY.

`deploy/linux/package.sh [all|deb|rpm] [-p publish dir] [-o output dir] [dir]` packages the
publish output (default `/project/delly-net/publish/Koi/files`) into `koi-<yyyyMMddHHmmss>.deb`
and `.rpm` in `<publish dir>/../packages`; the package version is the same timestamp. A positional
directory containing the `koi` executable is the publish dir, otherwise it is the output dir; the
script re-execs under `bash` when invoked with `sh` (Debian's `dash`). Both packages install to
`/opt/delly-net/Koi` plus `/etc/systemd/system/koi.service` (content must stay in sync with
`install.sh`), enable and start the service, pre-seed `/etc/koi/appsettings.json` (kept as a
conffile / `%config(noreplace)` so upgrades preserve local edits), and never touch `koi.db`. The deb
is built with `dpkg-deb` when present and otherwise assembled from `ar` + `tar`; rpm needs
`rpmbuild` (a missing tool skips that format and exits non-zero). The deb `Depends` lists the ICU
runtime packages by release (`libicu76 | libicu74 | libicu72 | …`, overridable with
`KOI_DEB_ICU_DEPENDS`) instead of `libicu | libicu-dev`: there is no `libicu` virtual package, so
that alternative always resolved to `libicu-dev` and pulled development headers onto runtime hosts.

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
  `MaxSessionsPerUser` / `MaxSessionsTotal`.
- Terminal settings follow the application-management pattern (`TerminalSettingsService`,
  `api/Terminal/TerminalSettingsService.cs`): they are **not** read from `appsettings.json`
  (`Koi:Terminal` was removed). Defaults live in `TerminalOptions`; the runtime source is the SQLite
  generic settings table (`Feature = terminal`) and root edits them in the terminal page's settings
  drawer. `GET/PUT /api/terminal/settings` require a session and uid 0 (resolved through
  `getpwnam_r`, not a name comparison), and deliberately skip the `Enabled`/platform checks so a
  disabled or broken configuration can be repaired. Non-root gets 403. On first start, when no
  `terminal` row exists, `TerminalSettingsService.LoadAsync` seeds the row from the legacy
  `Koi:Terminal` configuration section (the `/etc/koi/appsettings.json` overlay still counts) or from
  the code defaults; after that the section is ignored. The option set is `Enabled`, `Shell` (empty =
  account login shell), `ShellArguments`, `MaxSessionsPerUser`, `MaxSessionsTotal`, `MaxInputBytes`,
  `DefaultCols`, `DefaultRows`, `CloseTimeoutSeconds`. Saves replace the `Current` snapshot
  atomically and raise `Changed`; the WebSocket endpoint takes the snapshot per connection and
  `TerminalSessionManager.TryCreate` reads it per session, so nothing may cache
  `IOptions<TerminalOptions>` (that would make saves ineffective). Saved values apply to new
  sessions only. `PosixPty` relies on `AllowUnsafeBlocks` (only that file uses pointers) and calls
  `posix_openpt`/`grantpt`/`unlockpt`/`ptsname_r` directly from libc — no `forkpty`, no libutil, no C
  toolchain on the build host.
- API contract: `GET /api/health` (includes `pamAvailable` / `terminalAvailable` /
  `fileAvailable` / `dockerAvailable` / `systemMonitorAvailable` / `serviceAvailable` /
  `packageAvailable`), `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`,
  `GET /api/terminal/ws`, `GET/PUT /api/terminal/settings`, `GET/PUT /api/files/settings`,
  `GET /api/system/status`, and the `/api/files/*`, `/api/docker/*`, `/api/services/*` and
  `/api/packages/*` groups described below;
  errors use ProblemDetails. Unmatched `/api/**` paths return a 404 ProblemDetails and never fall
  back to the SPA.
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
  and contained in the configured `AllowedRoots` (stored in the `files` settings row). The default is `["/"]` — whole-filesystem visibility is
  an explicit product decision; the real boundary is the dropped user's OS permissions. Names are
  limited to 255 characters and may not contain `/`. Deletion refuses `/` and configured roots;
  symlinks are deleted/copied as links and never traversed by recursive copy.
- File API (`/api/files`, session cookie required; non-Linux or `Enabled=false` in the file
  settings → 503; account missing from `getpwnam_r` → 403): `GET /roots`, `GET /list?path=`,
  `POST /mkdir` `{path}`, `POST /touch` `{path}`, `POST /rename` `{path,newName}`,
  `POST /delete` `{paths,recursive}`, `POST /copy` / `POST /move`
  `{sources,target,overwrite}`, `POST /upload?path=&overwrite=` (multipart field `file`),
  `GET /download?path=`, `GET /text?path=`, `POST /text` `{path,content,expectedModifiedAt,hasBom}`,
  `GET /settings` and `PUT /settings`
  `{enabled,allowedRoots,maxUploadBytes,maxListEntries,maxTextFileBytes,helperTimeoutSeconds,
  listTimeoutSeconds,transferTimeoutSeconds,streamIdleTimeoutSeconds,streamMaxSeconds}`.
  The settings endpoints are root-only (uid 0 resolved through `getpwnam_r`; non-root → 403) and
  deliberately skip the `Enabled`/platform checks so a disabled or broken configuration can be
  repaired. Other errors are ProblemDetails: 400 invalid path/request or non-UTF-8 text, 403
  permission or outside roots, 404 missing, 409 conflict (external modification or non-empty
  directory), 413 too large, 504 helper timeout. Listing is capped by `MaxListEntries` and reports
  `truncated`. `GET /text` returns `{path,content,modifiedAt,size,hasBom}`; `POST /text` returns
  `{ok,modifiedAt,size}` and maps the helper's `conflict` code to 409.
- Online text editing is layered on the same helper: `readtext` reuses the download framing
  (header + raw bytes) and `writetext` reuses the upload framing. Only UTF-8 is supported
  (optional UTF-8 BOM is detected and preserved; invalid UTF-8 or NUL bytes → 400). Reads are
  capped by `MaxTextFileBytes` in the file settings; when the helper's header reports a larger size the
  parent kills the helper before draining, so the response is a 413 rather than a timeout.
  Saves are optimistic-concurrency writes: the request carries the `modifiedAt` observed at read
  time, the helper compares it with the current mtime and returns `conflict` (409) on mismatch;
  `expectedModifiedAt=0` forces an overwrite. The helper writes a temp file in the same directory,
  copies the original Unix mode onto it, then atomically renames it over the target (symlinks are
  resolved and their target is replaced, never the link itself).
- File settings are **not** read from `appsettings.json` (`Koi:Files` was removed): the
  `FileOptions` code defaults are the fallback, and the runtime source is the SQLite generic
  settings table (`Feature = files`) through `FileSettingsService`. `GET/PUT
  /api/files/settings` require a session and uid 0 (resolved through `getpwnam_r`), deliberately
  skip the `Enabled`/platform checks so a disabled or broken configuration can be repaired, and
  non-root gets 403. On first start, when no `files` row exists, `FileSettingsService.LoadAsync`
  seeds the row from the legacy `Koi:Files` section (the `/etc/koi/appsettings.json` overlay still
  counts) or from the code defaults; after that the section is ignored. The option set is
  `Enabled`, `AllowedRoots` (default `["/"]`; 1–64 absolute paths, each ≤4096 characters and free
  of control characters), `MaxUploadBytes` (default 100 MiB, ceiling 512 MiB), `MaxListEntries`
  (5000, ceiling 100000), `MaxTextFileBytes` (online text read/write cap, default 5 MiB, ceiling
  64 MiB), `HelperTimeoutSeconds` (metadata and text read/write ops, 120 s),
  `ListTimeoutSeconds` (directory listing, 300 s), `TransferTimeoutSeconds` (delete/copy/move,
  1800 s), `StreamIdleTimeoutSeconds` (upload/download with no data flowing, 120 s) and
  `StreamMaxSeconds` (upload/download total cap, 0 = unlimited). Saves replace the `Current`
  snapshot atomically and raise `Changed`; every consumer reads it per request/operation, so
  nothing may cache `IOptions<FileOptions>` (that would make saves ineffective). Kestrel
  `MaxRequestBodySize` and `FormOptions.MultipartBodyLengthLimit` are fixed at startup to the
  512 MiB ceiling plus 1 MiB overhead; the per-request limit is the current `MaxUploadBytes`, so
  raising it within the ceiling needs no restart. Uploads are written to a temp file in the
  destination directory and renamed into place, so an interrupted upload leaves no partial target.
- File helper framing has two separate limits: requests (main → helper) are capped at 64 KiB,
  responses (helper → main) at 32 MiB because a directory listing carries every entry in one JSON
  frame. Never stop reading a frame without terminating the helper: if the parent rejects an
  oversized length and simply waits for exit, the helper blocks writing the unread payload into the
  pipe and the operation only "finishes" when the timeout fires (this used to surface as a spurious
  504 when loading directories with a few hundred entries). Upload and download timeouts are
  idle-based and re-armed on every chunk, so a slow but active client is not killed by a wall clock.
- Docker management runs the docker CLI from the main process (`Docker/DockerCommandRunner.cs`)
  through `Process.Start` + `ArgumentList` — never a shell, so user input is never concatenated
  into a command line. Only a fixed whitelist of subcommands executes (`ps`, `inspect`, `logs`,
  `stats`, `images`, `pull`, `tag`, `image inspect`, `rmi`, `image prune`, `rm`,
  `start`/`stop`/`restart`/`pause`/`unpause`). stdout/stderr are read concurrently with a byte cap (`MaxOutputBytes` /
  `MaxLogBytes`); hitting the cap kills the process tree so the child cannot block on an unread
  pipe, and the step timeout does the same. `DOCKER_CLI_HINTS=false`, `LANG=C` and `LC_ALL=C` are
  set for stable output, and container/image list, logs and stats are parsed from
  `--format '{{json .}}'` output.
- Docker authorization: every `/api/docker/**` endpoint requires a session and membership in
  `Koi:Docker:AllowedGroups` (default `["docker"]`); membership is resolved with
  `id -nG -- <user>` so NSS/LDAP groups count, and non-members get 403. The exception is uid 0
  (root): it is resolved through `getpwnam_r` and always allowed, even when `AllowedGroups` is
  empty — root already equals the host administrator, and the list only constrains ordinary
  accounts. This bypass runs after the enabled/platform/docker-availability checks. Access to the Docker
  socket is equivalent to host root, so the service account must be trusted and the UI states
  this explicitly. Read endpoints are safe; pull/prune/remove are available to every allowed user
  (there is no finer-grained role).
- Docker API (`/api/docker`, session cookie required; non-Linux, `Koi:Docker:Enabled=false` or a
  missing docker executable → 503; non-member → 403): `GET /status`, `GET /containers`,
  `GET /containers/{id}`, `POST /containers/{id}/{start|stop|restart|pause|unpause}`,
  `DELETE /containers/{id}?force=&volumes=`, `GET /containers/{id}/logs?tail=&timestamps=`,
  `GET /stats`, `GET /images`, `POST /images/pull` `{reference}`, `POST /images/tag`
  `{source,target}`, `POST /images/prune`, `DELETE /images/{id}?force=`. Container ids/names are
  validated against `^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$` and image references against
  `^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,254}$`. Errors: 400 invalid input, 403 docker permission,
  404 missing container/image, 409 conflict (image in use or existing tag), 413 output too large,
  503 daemon or CLI unavailable, 504 timeout. Image deletion defaults to a plain `docker rmi`; when it returns
  409 the UI asks for a second confirmation and retries with `force=true` (`docker rmi -f`). The
  backend answers the common image conflicts with Chinese details instead of raw CLI stderr
  (referenced by multiple repositories, used by a running container, used by a stopped
  container); forced deletion removes every repository tag of that image, and an image used by a
  running container cannot be removed even with `-f`. `docker logs` is a snapshot bounded by
  `LogTailDefault`/`LogTailMax`, and pull runs with `--quiet`.
  `POST /images/tag` runs `docker tag` only after `docker image inspect` confirms the target
  reference does not exist; an existing target (even one pointing at the same image) is rejected
  with 409 and has no force path, so the user must remove the old tag first. The source is
  normally the image ID, which also lets `<none>` rows be tagged, and only local references are
  created (no push). The inspect/tag pair is not atomic: a tag created in that window can still be
  overwritten.
- Docker config keys: `Koi:Docker:Enabled`, `DockerPath` (empty = auto-detect
  `/usr/bin/docker`, `/usr/local/bin/docker`, `/bin/docker`), `AllowedGroups`,
  `CommandTimeoutSeconds` (60), `PullTimeoutSeconds` (600), `MaxOutputBytes` (4 MiB),
  `MaxLogBytes` (2 MiB), `LogTailDefault` (500), `LogTailMax` (5000). The deployment account (the
  `koi.service` user) must be a member of the configured group or the module returns 403 for
  everyone.
- Service management (`api/Services/`, `/api/services/**`) runs the systemctl CLI from the main
  process through `Process.Start` + `ArgumentList` — never a shell — with a fixed whitelist
  (`list-units`, `list-unit-files`, `show`, `start`, `stop`, `restart`, `reload`, `enable`,
  `disable`; `journalctl` only for logs). `LANG=C`, `LC_ALL=C` and `SYSTEMD_COLORS=0` keep the
  output stable. `GET /api/services` merges `systemctl list-units --type=service --all --plain
  --no-legend` (runtime state + description; description is the remainder after the first four
  columns) with `systemctl list-unit-files --type=service --no-legend` (enablement state), so
  stopped and not-loaded installed units are listed too. `GET /api/services/{name}` parses
  `systemctl show --property=...`; because `systemctl show` exits 0 for a missing unit, 404 is
  decided by `LoadState=not-found`. `POST /api/services/{name}/{start|stop|restart|reload|enable|disable}`
  executes exactly one whitelisted action. `GET /api/services/{name}/logs?tail=` runs `journalctl
  -u <unit> -n <tail> -q --no-pager -o short-iso`, with `tail` clamped to
  `LogTailDefault`/`LogTailMax`. Unit names must match
  `^[A-Za-z0-9][A-Za-z0-9_.@:\x5c-]{0,200}\.service$` (no leading `-`, spaces, `/` or glob
  characters), so a name can never be interpreted as an option or pattern. Errors: 400 invalid
  unit/action, 403 permission or not in an allowed group, 404 unit not found, 409
  masked/unsupported operation, 413 output too large, 503 disabled/non-Linux/systemctl
  missing/systemd not running, 504 timeout.
- Service authorization mirrors Docker: every `/api/services/**` endpoint requires a session and
  membership in `Koi:Services:AllowedGroups` (default `["sudo","wheel"]`), resolved with
  `id -nG -- <user>` so NSS/LDAP groups count. uid 0 is always allowed even when the list is
  empty. The check order is Enabled → Linux → systemctl availability → session → account name →
  root → group list → membership. systemctl is equivalent to host root, so the service account
  must be trusted. The UI warns that stopping, restarting or disabling the Koi service itself
  interrupts the current page, but the backend deliberately does not block it.
- Service config keys: `Koi:Services:Enabled`, `SystemctlPath` / `JournalctlPath` (empty =
  auto-detect `/usr/bin/...`, `/bin/...`, then `PATH`), `AllowedGroups`, `CommandTimeoutSeconds`
  (120, above systemd's 90-second stop timeout), `MaxOutputBytes` (4 MiB), `MaxLogBytes` (2 MiB),
  `LogTailDefault` (500), `LogTailMax` (5000).
- Application management (`api/Packages/`, `/api/packages/**`) runs apt, dnf, yum, pacman or
  zypper from the main process through `Process.Start` + `ArgumentList` — never a shell, never a
  fixed `sh -c` wrapper. `PackageManagerDetector` uses the `Manager` value from the settings
  store (SQLite, see below) when set and otherwise probes in the order
  apt → dnf → yum → pacman → zypper using
  `/usr/bin`, `/bin`, `/usr/local/bin`, `/usr/sbin`, `/sbin` and then `PATH`. Each backend
  (`AptPackageManager`, `RpmPackageManager`, `PacmanPackageManager`, `ZypperPackageManager`)
  owns its command lines and parsers; package names must match
  `^[A-Za-z0-9][A-Za-z0-9+._:@/=~-]{0,199}$` so a name can never be read as an option.
  All commands set `LANG=C`, `LC_ALL=C`, `SYSTEMD_COLORS=0`, and for Debian-family commands
  `DEBIAN_FRONTEND=noninteractive`, `DEBCONF_NONINTERACTIVE_SEEN=true`,
  `APT_LISTCHANGES_FRONTEND=none`, `NEEDRESTART_MODE=a` plus `Dpkg::Options` forcing the local
  config file; zypper runs with `--non-interactive`, pacman with `--noconfirm`, dnf/yum with `-y`.
- Package query endpoints (session cookie required): `GET /status` returns the detected manager,
  version, whether the signed-in account is root, whether the service process is root
  (`canModify`) and whether source writes are supported; all endpoints read the live
  `PackageSettingsService.Current` snapshot, so saved settings take effect immediately;
  `GET /installed?query=&limit=` filters
  the installed list and caps it by `MaxInstalledEntries`; `GET /search?q=` returns at most 200
  local-index results; `GET /updates` lists upgradable packages (dnf/yum cannot report the
  current version, so it is empty). A missing manager, non-Linux host or `Enabled=false` returns
  503 ProblemDetails.
- Installed applications are cached in SQLite (`PackageCacheItems`, `api/Data/PackageCacheEntity.cs`):
  `Id`, `Name` (unique), `Version`, `Intro`, `Status` (`installed`/`removed`), `Manager`,
  `CreatedAt`, `UpdatedAt`, `RefreshedAt`. `GET /api/packages/cache?page=&pageSize=&status=&query=`
  reads the cache with server-side paging (`page` >= 1, `pageSize` clamped 1..100, default 20),
  `status` one of `all`/`installed`/`removed`, optional name filter (SQLite `LIKE`), returning
  `{manager,total,page,pageSize,status,lastRefreshedAt,packages:[{id,name,version,intro,status,updatedAt}]}`.
  `PUT /api/packages/cache/{id}` `{intro}` edits the intro (trimmed, max 500 characters; 404 for a
  missing record, 400 for an invalid id/body). `POST /api/packages/cache/refresh` synchronously runs
  the backend's `InstalledCommand` (bounded by `CommandTimeoutSeconds`/`MaxOutputBytes`) and upserts:
  new rows get `Intro` from the package-manager description (`dpkg-query ${binary:Summary}`,
  `rpm -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{SUMMARY}\n'`, zypper XML; pacman `-Q` carries no
  description so its intro starts empty), existing rows update only version/status/manager/timestamps
  and **never overwrite `Intro`**, and rows absent from the system become `removed` (records are kept
  and flip back to `installed` when the package reappears). It returns
  `{manager,total,added,updated,removed,refreshedAt}`. Refreshes are serialized with a semaphore;
  cache reads, refreshes and intro edits use the normal package authorization (no service-root
  requirement). `GET /installed` remains available for live, uncached queries.
- Package mutations are in-memory background jobs (`PackageJobManager`): `POST /api/packages/jobs`
  with `{action, packages}` where action is `install`, `remove`, `upgrade`, `upgrade-all` or
  `refresh`, returns 202 `{jobId, action, packages}`. Only one job may run at a time (otherwise
  409) because package managers hold database locks; `GET /jobs/{id}?offset=` returns an
  incremental log slice plus `nextOffset`, status (`running`/`succeeded`/`failed`/`cancelled`),
  exit code and truncation flag, and `POST /jobs/{id}/cancel` kills the process tree. Jobs are
  not persisted: a Koi restart loses them, and finished jobs expire after
  `JobRetentionMinutes`. Logs are capped by `MaxLogBytes`; hitting the cap terminates the command.
- Package authorization mirrors services: every `/api/packages/**` endpoint requires a session
  and membership in the allowed groups (default `["sudo","wheel"]`, stored in the settings table
  rather than `appsettings.json`), resolved with `id -nG -- <user>`; uid 0 is always allowed. The
  order is Enabled → Linux → manager availability → session → account name → root → group list →
  membership. In addition, jobs and source writes require the Koi service process itself to run
  as root (`PackageAccessGuard.IsServiceRoot`, resolved through `Environment.UserName` +
  `getpwnam_r`) and otherwise return 403, because package managers cannot change the system as an
  unprivileged service account. The settings endpoints are the exception: `GET/PUT
  /api/packages/settings` only allow uid 0 (`PackageAccessGuard.IsRootAccount`) and deliberately
  skip the Enabled/manager checks so an administrator can repair a disabled or misconfigured
  module; non-root gets 403.
- Source management (`PackageSourceService`) is complete for apt, dnf/yum and zypper and
  read-only for pacman, whose repositories live in `/etc/pacman.conf`. Apt reads
  `/etc/apt/sources.list`, `sources.list.d/*.list` and `*.sources` (deb822); dnf/yum reads
  `*.repo` INI sections from `/etc/yum.repos.d` (and `/etc/yum/repos.d`); zypper uses
  `zypper repos -u`, `addrepo`, `modifyrepo -e/-d` and `removerepo`. Added apt/dnf sources are
  written as `koi-<name>.list` / `koi-<name>.repo`; toggling an apt `.list` line comments or
  uncomments it, a deb822 stanza gets `Enabled: yes/no`, an rpm section gets `enabled=1/0`.
  File rewrites use a same-directory temp file, preserve the original Unix mode and atomically
  rename; symlinked source files are skipped. Empty `sources.list.d`/`yum.repos.d` files are
  deleted after the last entry is removed; `/etc/apt/sources.list` itself is never deleted.
  Added rpm sources default to `gpgcheck=1` unless the request sets `gpgCheck=false`; repository
  signing keys themselves are still maintained by the administrator. Source ids are SHA-256
  hashes of kind + file + normalized entry, so they survive enable/disable.
- Package source API: `GET /api/packages/sources` → `{manager, supported, readOnly, sources}`;
  `POST /api/packages/sources` `{name, uri, suite, components, gpgCheck}`; `POST
  /sources/{id}/enable|disable`; `DELETE /sources/{id}`. Repository URIs must be absolute
  http/https/ftp/file URLs; apt suites/components accept only letters, digits and `. / + _ ~ -`.
  Errors: 400 invalid input or unsupported write (pacman), 403 not in an allowed group/service
  not root, 404 missing source, 409 existing source or package-manager lock/dependency conflict,
  413 output too large, 502 repository/network failure, 503 disabled/non-Linux/manager missing,
  504 timeout.
- Package settings are **not** read from `appsettings.json` (`Koi:Packages` was removed): the
  `PackageOptions` code defaults are `Enabled=true`, `Manager=""` (auto-detect),
  `AllowedGroups=["sudo","wheel"]`, `CommandTimeoutSeconds=120`, `JobTimeoutSeconds=1800`,
  `MaxOutputBytes=4 MiB`, `MaxLogBytes=2 MiB`, `JobRetentionMinutes=30`,
  `MaxPackagesPerJob=50`, `MaxInstalledEntries=5000`. They are overridden by the SQLite generic
  settings table and edited through `GET/PUT /api/packages/settings` (root only; PUT accepts the
  full `PackageOptions` object, validates it server-side — manager whitelist, group-name format,
  numeric ranges — and returns the saved object). `PackageSettingsService` loads the row at
  startup, atomically replaces its `Current` snapshot on save and raises `Changed`, which makes
  `PackageManagerResolver` re-detect the backend; every consumer reads `Current`, so changes
  apply without restarting `koi.service`. `/api/health` exposes `packageAvailable` from the
  resolver.
- Server status (`api/Monitor/`, `GET /api/system/status`) is available to every signed-in user
  because it exposes only aggregate host metrics. It reads `/proc/stat` (CPU), `/proc/meminfo`
  (memory and swap), `/proc/loadavg`, `/proc/uptime`, `/proc/net/dev` (every interface except
  `lo`), `/proc/sys/kernel/osrelease`, counts numeric `/proc` directories for the process count,
  and gets disks from `DriveInfo` (statvfs). CPU and network are cumulative counters, so the
  singleton `SystemMonitorService` keeps the previous sample and computes deltas: the first
  request, or one arriving more than 30 s after the last sample, takes an internal 250 ms sample
  instead of averaging idle time, and snapshots are cached for 1 s so concurrent clients do not
  produce near-zero intervals. Percentages are clamped to 0..100. Pseudo filesystems (`tmpfs`,
  `devtmpfs`, `overlay`, `squashfs`, `proc`, `sysfs`, `cgroup*`, …) are filtered, unreadable
  mounts are skipped per drive, and at most 20 mounts are returned. `Koi:SystemMonitor:Enabled`
  (default `true`) or a non-Linux host returns 503, and a missing/unreadable `/proc` returns 503
  ProblemDetails. The collector never runs a shell or external command.
- Static hosting: `Koi:StaticFiles:RootPath` (default `wwwroot`, relative paths resolve against the
  content root) and `Koi:StaticFiles:Enabled` (default `true`). `UseKoiStaticFiles()` runs before
  the same-origin and session middleware so assets bypass the session lookup. When `index.html` is
  absent the app logs a warning and serves only the API. `MapKoiFallbacks()` maps `/api/{**path}`
  to 404 and returns `index.html` for GET/HEAD requests whose path has no extension, which supports
  Vue Router history mode while missing `.js`/`.css` files still return 404. `index.html` and
  `conf/setting.json` are served `no-cache`; `/assets/**` gets a one-year immutable cache.
- External configuration: on Linux the app overlays `/etc/koi/appsettings.json` on the bundled
  `appsettings.json` / `appsettings.{Environment}.json`. If the file is missing at startup it is
  created from the bundled copy (directory `0755`, file `0644`, temp file + atomic rename) and then
  loaded. The source is inserted after all JSON configuration files (including development user
  secrets) but before environment variables and command line, so those still win. Creation failure
  (for example a non-root `dotnet run`) only logs a warning; startup continues with the bundled
  defaults. Non-Linux skips the external file entirely. Keys omitted from `/etc` fall back to the
  bundled defaults, so the publish output must keep `appsettings.json`. `install.sh` and the
  deb/rpm packages pre-create the file; it is a deb conffile / rpm `%config(noreplace)` and is
  never overwritten by an upgrade. Reload-on-change is off because options are snapshotted at
  startup. See `api/Configuration/KoiExternalConfigurationExtensions.cs`. An old `/etc/koi/
  appsettings.json` may still contain `Koi:Packages`; it is no longer read and never overrides the
  database settings.
- Runtime state lives in `api/koi.db` (SQLite, created with `EnsureCreated` — no migrations yet).
  The file is gitignored. `hamster.db` entries in `api/.gitignore` are template leftovers. Since
  `EnsureCreated` does not add tables to an existing database, `KoiDbSchema.EnsureCompatibilityTables`
  runs idempotent `CREATE TABLE IF NOT EXISTS` / `CREATE UNIQUE INDEX IF NOT EXISTS` for the
  generic settings table and the `PackageCacheItems` installed-app cache after `EnsureCreated`;
  keep its DDL in sync with the EF model (or replace it with EF migrations).
- Generic feature settings live in `FeatureConfigs` (`api/Data/FeatureConfigEntity.cs`):
  `Id`, `Feature` (unique, e.g. `packages`), `ConfigJson`, `UpdatedAt`, `UpdatedBy`. Each
  feature owns one JSON row, so future modules can reuse the table and `FeatureConfigStore`
  (`api/Configuration/`) instead of adding configuration keys to `appsettings.json`.
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
  (non-card) text tabs inside that bar. A 1px, 24px-tall `#e4e7ed` divider (`.brand-divider`) sits
  between the brand area and the tabs with exactly 20px on each side; the top bar therefore has no
  flex `gap` — the divider margins and `.user { margin-left: 16px }` provide the spacing explicitly.
  The `el-tabs` there is navigation only — its panes are
  empty and `.el-tabs__content` is hidden; the seven modules mount in the sibling `.panels`
  container, keyed by `stores/workspace.ts` (`home` / `terminal` / `files` / `editor` /
  `docker` / `service` / `package`). `首页` is
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
- The 在线终端 page (`views/TerminalView.vue`) top-right toolbar carries a circular `设置` gear
  (`Setting`, `aria-label="终端设置"`) **only to root**: on mount the page silently calls
  `GET /api/terminal/settings`; success shows the button, any failure (403 for non-root, 503 on
  non-Linux) hides it without an error toast. The click opens a 480 px `el-drawer` editing every
  `TerminalOptions` value — enable switch, Shell path (empty = account login shell), Shell arguments
  as a multi-select/creatable list (one tag per argument), per-user/total session caps, default
  cols/rows, single-message input cap (edited in KiB and converted to bytes) and close timeout.
  `保存` sends `PUT /api/terminal/settings`, shows `设置已保存并立即生效`, and saved values apply to
  new terminal tabs only because already-open PTYs keep their settings; the drawer copy notes that
  settings live in the SQLite generic settings table.
- The 首页 panel (`views/HomeView.vue`) receives `:active` from the workspace and no longer shows
  the former “登录成功” `el-result` block. Its top is `components/system/ServerStatus.vue`: the
  server-name/kernel/uptime/process row (`.meta`, labelled `服务器名称`) sits directly below the
  panel title and *above* four status cards (CPU usage/cores/load, memory + swap, per-mount disk
  usage, network receive/send rates), refreshed every 5 seconds. Polling runs only while
  the home tab is active *and* `document.visibilityState === 'visible'`; tab switches and
  `visibilitychange` restart/stop the interval and fetch immediately on becoming visible; timers
  and listeners are cleaned up on unmount. Refreshes are silent: `.status-grid` carries no
  `v-loading`, so the previous snapshot stays visible and is replaced when the new one arrives;
  only the first load (no data yet) shows `el-skeleton`, and the `重试` button keeps its own
  `:loading` as explicit action feedback. A failed request keeps the last data and shows a
  persistent alert with `重试` instead of a toast. Below it the module entries are split into two
  groups driven by the `MODULE_GROUPS` array in `HomeView.vue`: **系统管理** (terminal / files /
  editor / service / package) and **软件管理** (docker). Each group renders a
  `section.module-group` with an `h2.module-group-title` (3 px primary bar + 16 px text) above its
  `.modules` grid. Each entry is a lightweight tile: a 40 px Element Plus icon above the entry
  name, no border by default and a primary-tinted highlight with a slight lift on hover/focus. The
  whole tile is the button (`role="button"`, `tabindex="0"`, `@click` plus `Enter` / `Space`) and
  calls `workspace.open(key)`; the description is not shown inline — it appears only as an
  `el-tooltip` on the icon. Element Plus teleports the tooltip to `body`, so its class is styled
  via `:global(.module-tip)` (`max-width: 260px`). `.module-group` carries the 1100 px max width
  (aligned with the status panel) and `.modules` uses `repeat(auto-fill, minmax(160px, 1fr))`, so
  tiles wrap on narrow screens and the single 软件管理 tile keeps its normal width (`auto-fit`
  would stretch it across the row). Keep new home entries on the same pattern: add label,
  description and icon to the module list of the matching group.
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
- Login page persistence (`ui/src/views/LoginView.vue` + `ui/src/utils/rememberedLogin.ts`): after a
  successful login the user name is always stored in `localStorage['koi.login.lastUserName']` and
  prefilled next time. The `记住密码` checkbox, when checked, stores the password as an encrypted
  JSON record in `localStorage['koi.login.rememberedPassword']`:
  `{ v: 1, alg: "AES-GCM", salt, iv, data }` using Web Crypto AES-GCM with a PBKDF2-SHA256 key
  (210000 iterations) derived from a built-in app passphrase plus a random per-record salt; it is
  decrypted on mount and prefilled (checkbox re-checked). A successful login with the checkbox
  unchecked deletes the stored record; logout deliberately keeps both keys. Because the key
  material also lives in the frontend bundle this is obfuscation-grade protection, not a secret —
  same-origin script can still decrypt it, so never call it end-to-end secrecy. `crypto.subtle`
  only exists in secure contexts (HTTPS or localhost); on plain HTTP the module falls back to
  XOR + Base64 with `alg: "xor"` so the feature still works. All `localStorage` access is
  best-effort and must never block login; malformed records are deleted instead of throwing.
- The text editor (`ui/src/views/EditorView.vue`, the `editor` workspace tab) keeps its tabs in
  `src/stores/editor.ts`, so switching workspace tabs preserves unsaved content; a browser refresh
  loses it and `beforeunload` only warns. Saves check the `modifiedAt` returned by the read, and a 409
  lets the user overwrite (`expectedModifiedAt=0`) or reload. The editor is a plain monospace
  textarea (no new npm dependency); large files near `MaxTextFileBytes` may feel slow.
- The 容器管理 tab (`ui/src/views/DockerView.vue`, workspace key `docker`) has three inner tabs:
  容器 / 镜像 / 系统. Data is fetched on mount and only on the manual `刷新` button — there is no
  polling timer; refreshing reloads the status plus the visible inner tab. Container rows collapse
  every operation (start / stop / restart / pause / unpause / logs / detail / delete) into one
  `操作` dropdown whose items are disabled according to `state`; logs open in a dialog with a tail
  selector (snapshot, not follow) and detail opens in a drawer fed by `docker inspect`. The image
  tab offers pull by reference, dangling-image prune and a per-row `操作` dropdown (`打标签` /
  `删除`). `打标签` takes a new reference and runs `docker tag` from the row's image ID (so
  `<none>` rows work too); the backend first runs `docker image inspect` on the target and rejects
  an existing tag with 409 instead of overwriting it, and there is no force override. Delete first runs without
  `-f`; a 409 conflict (multiple repository tags or container references) opens a `强制删除镜像`
  confirmation and retries with `force=true`, with a message that forced deletion removes all of the
  image's repository tags. Images used by a running container only show the guidance message (stop
  and delete the container first) because `-f` cannot remove them. The system tab shows `docker version` /
  `docker info` fields plus a `docker stats --no-stream` table. 403 (not in an allowed group) and
  503 (docker unavailable) render as persistent alerts instead of toasts.
- The 服务管理 tab (`ui/src/views/ServiceView.vue`, workspace key `service`) lists systemd services
  with name, description, runtime state (ActiveState/SubState), boot enablement (UnitFileState) and
  client-side keyword/state filters; data is fetched on mount and only on the manual `刷新` button.
  Every row operation lives in one `操作` dropdown (start / stop / restart / reload / enable
  autostart / disable autostart / logs / detail) whose items are disabled according to the current
  state. Stop, restart and disable ask for confirmation and warn that operating on the Koi service
  itself interrupts the current page. Logs open in a dialog with a tail selector
  (`journalctl` snapshot, not follow); detail opens in a drawer fed by `systemctl show`. 403 (not
  in an allowed group) and 503 (systemd unavailable) render as persistent alerts instead of toasts.
- The 应用管理 tab (`ui/src/views/PackageView.vue`, workspace key `package`) has four inner tabs:
  已安装 / 安装 / 更新 / 软件源. It fetches the manager status on mount, loads each data tab on
  first activation, and the manual `刷新` button reloads the status plus the active tab.
  已安装 reads the database cache with server-side paging (`el-pagination`, page sizes
  20/50/100), a status filter (全部 / 已安装 / 已卸载) and a debounced name filter; the toolbar
  `更新缓存` button calls `POST /api/packages/cache/refresh` synchronously and reports
  added/updated/removed counts. Each row shows name, version, intro and a status tag; `编辑简介`
  opens a 500-character textarea dialog saved through `PUT /api/packages/cache/{id}`, and `卸载` is
  enabled only for `installed` rows (also subject to the service-root rule). An empty cache shows
  guidance to press `更新缓存`; the page never rebuilds the cache implicitly, and passing an
  install/remove job only reloads the current cache page. 安装 offers keyword search plus a
  direct exact-name install input, with per-row `安装`; 更新 shows current/new versions (current
  version may be `—` for dnf/yum), a `系统更新` button for all upgrades and a per-row `升级`.
  软件源 lists name, URI, apt suite/components, enabled state, backing file and a Koi-created tag,
  with `添加软件源`, per-row `启用`/`禁用` and `删除`; the add dialog collects name/URI and, for
  apt, suite/components, and for dnf/yum a GPG-check switch. pacman renders the source tab as
  read-only. All package-changing actions confirm first and then open a job-log dialog that polls
  `GET /api/packages/jobs/{id}` every second, appends incremental log slices (capped in the
  browser), offers `取消任务` while running, and refreshes the active tab when the job finishes;
  closing the dialog while a job runs keeps it running but stops showing live output until it
  completes. 403 (not in an allowed group), 503 (non-Linux / no manager) and the
  `status.canModify === false` case (Koi service not running as root) render as persistent
  `el-alert` notices; action buttons are disabled while the service is not root or a job is
  running. The `刷新索引` toolbar action starts a `refresh` job. The top-right toolbar shows a
  circular settings icon (`Setting`, `aria-label="应用管理设置"`) **only to root**: on mount the
  page silently calls `GET /api/packages/settings`; success shows the button, any failure
  (403 for non-root, 503 on non-Linux) hides it without an error toast. The click opens a
  right-side `el-drawer` (480 px) that edits every `PackageOptions` value — enable switch,
  manager select, multi-select/creatable allowed groups, query/job timeouts, output/log caps
  (edited in MiB and converted to bytes), job retention, per-job package cap and installed-list
  cap. `保存` sends `PUT /api/packages/settings`, then shows `设置已保存并立即生效` and refreshes
  the status plus the active tab (saving `Enabled=false` therefore surfaces the 503 alert
  immediately). The drawer's copy notes that settings live in the SQLite generic settings table.

## Knowledge base

This repository is tracked in the EAZY.RAG project "Koi项目" (project ID 9); its execution specs,
notes, and dependency docs live there rather than in the repo. The MCP connection is configured in
`.mcp.json`, which is gitignored.
