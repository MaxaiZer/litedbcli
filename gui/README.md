# LiteDB GUI

Tauri v2 frontend for `litedbcli`. The GUI does not touch LiteDB directly:
it spawns `litedbcli <db> --serve` as a child process and talks to it over
NDJSON on stdin/stdout (see the protocol in the root `README.md`).

## Layout

- `frontend/index.html` — single-file UI (no build step, no npm)
- `src-tauri/` — Tauri backend; `src/main.rs` manages the sidecar lifecycle

## Build & run

Requires the published sidecar binary and WebKitGTK on the system:

```bash
dotnet publish litedbcli/litedbcli.csproj -c Release -r linux-x64 -o dist/linux-x64
cd gui/src-tauri && cargo build
./target/debug/litedb-gui
```

On Linux, launch with `WEBKIT_DISABLE_DMABUF_RENDERER=1` if WebKit fails to
render. The GUI locates the sidecar in this order: `LITEDBCLI_BIN` env,
`PATH`, then the folder of the GUI executable — so simply put
`litedbcli[.exe]` next to `litedb-gui` and it will be found.

## Opening a database

Everything is entered in the **Open database** dialog: file path, optional
password, read-only and shared mode. The header has an **Open recent** menu
and a chip for the currently open connection.

- Recent entries store the path and mode flags, never the password.
- A password is kept in memory for the current app session, so reopening the
  same recent connection does not ask again until the app restarts.
- The password is passed to the sidecar in `LITEDBCLI_PASSWORD`; it does not
  appear in the process argument list.
- Opening a database runs the new sidecar handshake first. If it fails, the
  previously opened database stays connected.
- **Edit connection** reopens the current file with different read-only/shared
  settings. The path is fixed; use **Open database…** for another file. The
  dialog's primary action is **Reconnect**, and a failed reconnect leaves the
  old connection active.

## Sidecar lifecycle

- `open_db` spawns the sidecar and does a `ping` + `collections` handshake.
- `run_query` forwards SQL with a row limit.
- `close_db` and window exit send `op:"close"`, then kill and reap the child,
  so no `litedbcli --serve` process is left behind.
