# litedbcli

Command Line tool for managing LiteDB database.

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

# Usage

## One-shot command (automation-friendly)

```bash
litedbcli <connectionArgs> <command>...
```

Runs a single command and exits with a non-zero exit code on error, so it
can be used in scripts and CI pipelines.

```bash
litedbcli test.db "show tables"
litedbcli test.db "select * from Coils limit 1 offset 4"
```

`<connectionArgs>` is either a database file path or a full LiteDB connection
string (for example when a password is required):

```bash
litedbcli 'Filename=test.db;Password=secret;Connection=shared' "show tables"
```

## Interactive mode

```bash
litedbcli <connectionArgs>
```

```text
> show tables
Coils
PPL
Users
Total collections: 3

> select * from Coils limit 1 offset 4;

[{"_id":{"$oid":"682f57589a69140b1bc73ea9"},"lotNo":"8d39de94-31b6-4e8e-8cc2-e8b473189c65","coilNo":"000005","ProductDescription":"Pasteries and Biscuits","Timestamp_qr":{"$date":"2025-05-22T16:56:56.7330000Z"},"deviceId_qr":"DESKTOP-78Z3269","nWeight":0.8,"image":"data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAOAOw==","tag":"M","Timestamp_nw":{"$date":"2025-05-24T11:55:04.1700000Z"},"pplId":{"$oid":"68334b625b82ba00aaa12db7"}}]
```
