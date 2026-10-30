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
render. The sidecar path and the database path are editable fields in the
header; the defaults point at `dist/linux-x64/litedbcli` and a scratch DB.

## Sidecar lifecycle

- `open_db` spawns the sidecar and does a `ping` + `collections` handshake.
- `run_query` forwards SQL with a row limit.
- `close_db` and window exit send `op:"close"`, then kill and reap the child,
  so no `litedbcli --serve` process is left behind.
