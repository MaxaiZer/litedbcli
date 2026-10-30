#!/usr/bin/env bash
# NDJSON server-mode smoke test: save round-trip + readonly guard.
# Usage: scripts/test_serve.sh [path-to-litedbcli]
set -euo pipefail

BIN="${1:-dist/linux-x64/litedbcli}"
command -v "$BIN" >/dev/null 2>&1 || BIN="$(cd "$(dirname "$0")/.." && realpath "$BIN")"

WORK="$(mktemp -d)"
DB="$WORK/test.litedb"
FIFO="$WORK/in"
OUT="$WORK/out.log"
REQ="$WORK/req.ndjson"
trap 'rm -rf "$WORK"' EXIT

send() { # $1 = json line; waits for the reply
  printf '%s\n' "$1" >&9
  sleep 0.6
}

expect() { # $1 = substring that must appear in the last reply line
  local line
  line="$(tail -n 1 "$OUT")"
  if [[ "$line" != *"$1"* ]]; then
    echo "FAIL: expected substring '$1' in: $line" >&2
    exit 1
  fi
}

start_serve() { # $1 = extra flags
  rm -f "$FIFO"; mkfifo "$FIFO"
  : >"$OUT"
  # shellcheck disable=SC2086
  "$BIN" "$DB" --serve ${1:-} <"$FIFO" >"$OUT" 2>&1 &
  SIDECAR=$!
  exec 9>"$FIFO"
}

# ---- writable database: save round-trip ----
rm -f "$DB"
start_serve

send '{"sql":"insert into t values {\"_id\":\"u1\",\"name\":\"old\",\"meta\":{\"$numberLong\":\"42\"}}"}'
expect '"ok":true'

send '{"sql":"select $ from t limit 10"}'
expect '"name":"old"'

send '{"op":"save","collection":"t","doc":{"_id":"u1","name":"new","meta":{"$numberLong":"43","when":{"$date":"2024-01-02T03:04:05.678Z"},"bin":{"$binary":"AQID+g=="}}}}'
expect '"updated":true'

send '{"sql":"select $ from t limit 10"}'
expect '"name":"new"'
tail -n 1 "$OUT" | grep -q '\$numberLong' && echo 'ok: extended types survived' || { echo 'FAIL: extended types lost' >&2; exit 1; }

send '{"op":"save","collection":"t","doc":{"name":"nope"}}'
expect '"ok":false'

send '{"op":"close"}'
exec 9>&-
wait "$SIDECAR" 2>/dev/null || true
echo "ok: serve save round-trip"

# ---- readonly database: writes rejected ----
# (readonly open refuses to create the file, so seed it first)
rm -f "$DB"
start_serve
send '{"sql":"insert into t values {\"_id\":\"u1\",\"name\":\"x\"}"}'
expect '"ok":true'
send '{"op":"close"}'
exec 9>&-
wait "$SIDECAR" 2>/dev/null || true

start_serve --readonly
send '{"sql":"insert into t values {\"_id\":\"u2\",\"name\":\"y\"}"}'
expect '"ok":false'

send '{"op":"save","collection":"t","doc":{"_id":"u1","name":"y"}}'
expect '"ok":false'

send '{"op":"close"}'
exec 9>&-
wait "$SIDECAR" 2>/dev/null || true
echo "ok: readonly guard"
