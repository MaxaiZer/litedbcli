# litedbcli

Command line tool for managing [LiteDB](https://github.com/litedb-org/LiteDB)
databases, with JSON and NDJSON-server modes for GUI/agent integration.

# Build

```bash
dotnet build litedbcli.slnx
```

# Native binary (AOT)

The project is configured for Native AOT: the published executable contains
the runtime and needs no .NET installation.

```bash
dotnet publish litedbcli/litedbcli.csproj -c Release -r linux-x64 -o dist/linux-x64
```

Replace `linux-x64` with `win-x64`, `osx-x64` or `osx-arm64` as needed. Native
AOT cannot cross-compile between operating systems, so each target must be
built on its own OS (or in CI). On Linux the result is a ~6 MB executable;
the accompanying `.dbg` file holds debug symbols and is not needed at runtime.

# GUI

`gui/` contains a Tauri v2 desktop app that drives `litedbcli --serve` as a
sidecar process over NDJSON. It has no frontend build step (one `index.html`,
no npm) and no other runtime dependency than WebKitGTK.

```bash
cd gui/src-tauri && cargo build
./target/debug/litedb-gui
```

See `gui/README.md` for details.

<img width="1078" height="537" alt="image" src="https://github.com/user-attachments/assets/08e8253a-a49e-419f-b679-573bf39dd97f" />

# Usage

```text
litedbcli <connection> [command] [options]

Options:
  --json            Print one JSON object instead of text
  --serve           Read NDJSON requests from stdin (implies --json)
  --limit <n>       Max rows returned (default: 1000)
  --readonly        Open database in read-only mode
  --shared          Use Connection=Shared (safe for multiple processes)
  --direct          Use Connection=Direct (default, fastest)
  --password <pwd>  Database password
  --help            Show this help
```

`<connection>` is either a database file path or a full LiteDB connection
string:

```bash
litedbcli test.db "show tables"
litedbcli 'Filename=test.db;Password=secret;Connection=shared' "show tables"
```

A path containing `=` is treated as a file name unless it contains `;` or
starts with a known connection-string key.

## One-shot command

Runs a single command and exits with a non-zero exit code on error.

```bash
litedbcli test.db "show tables"
litedbcli test.db "select $ from Coils limit 1 offset 4"
```

Note: in LiteDB `SELECT *` is an expression that returns one document with an
array value, it does not select all fields. Use `SELECT $` to get whole
documents.

## JSON mode

```bash
litedbcli test.db --json "select $ from Users limit 10"
```

Query result:

```json
{
  "ok": true,
  "type": "query",
  "collection": "Users",
  "columns": ["_id", "name", "age"],
  "count": 2,
  "truncated": false,
  "rows": [
    {"_id": {"$oid": "6ae460a8482dbc0d59f6a859"}, "name": "Bob", "age": 41},
    {"_id": {"$oid": "6ae460a884e9f30d431376f9"}, "name": "Ann", "age": 30}
  ],
  "elapsed_ms": 8.18
}
```

`columns` is the union of keys across returned rows, in first-seen order.
BSON types are preserved using LiteDB extended JSON (`$oid`, `$date`,
`$binary`, `$numberLong`, `$numberDecimal`, ...). `truncated` is true when the
result had more rows than `--limit`.

Collection list:

```json
{"ok":true,"type":"collections","collections":["Users"],"count":1}
```

Insert/update/delete/pragma result:

```json
{"ok":true,"type":"result","value":1,"elapsed_ms":10.56}
```

Error (exit code 1):

```json
{"ok":false,"error":"Unexpected token `foo` in position 1.","type":"LiteException"}
```

## Server mode (NDJSON)

Keeps the database open and reads one JSON request per line from stdin,
writing one JSON response per line to stdout. A failed request does not stop
the server.

```bash
litedbcli test.db --serve
```

```json
{"id":1,"op":"ping"}
{"id":2,"sql":"show tables"}
{"id":3,"sql":"select $ from Users","limit":50}
{"id":4,"op":"collections"}
{"id":5,"op":"close"}
```

Responses echo `id` when supplied. Unknown `op` or invalid JSON produce
`{"ok":false,...}`; `op":"close"` replies with `{"type":"bye"}` and exits.

## Interactive mode

```bash
litedbcli test.db
```

```text
> show tables
Users
Total collections: 1

> select $ from Users;
```

## Connection modes

- `--direct` (default): one LiteDB engine keeps the data file open. Fastest,
  but intended for a single process. On Unix file sharing is not enforced by
  the OS, so two Direct writers can corrupt a database.
- `--shared`: opens and closes the engine around every operation, serialized
  through a named mutex. Safe for several processes, noticeably slower.
- `--readonly`: opens the data file with read access only. Read commands work;
  write commands are rejected by the CLI before reaching LiteDB.

Do not mix Direct and Shared writers on the same file. A GUI that may coexist
with other tools should use `--shared`; a single-process studio can use
`--direct`.

## Known limitations

- `ObjectId('...')`, `ISODate('...')`, `BINData('...')` literals in
  `insert ... values` are rejected by the LiteDB SQL parser.
- Text (non-JSON) output still uses tab-separated columns and a header taken
  from the first row; prefer `--json` for machine consumption.
- Passwords passed via `--password` are visible in the process list.
