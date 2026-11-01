#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;
use tauri::{AppHandle, Manager, State};

struct Sidecar {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    next_id: u64,
}

#[derive(Default)]
struct AppState {
    sidecar: Mutex<Option<Sidecar>>,
}

impl Sidecar {
    fn request(&mut self, mut payload: Value) -> Result<Value, String> {
        let id = self.next_id;
        self.next_id += 1;
        payload["id"] = json!(id);

        let mut line = serde_json::to_string(&payload).map_err(|e| e.to_string())?;
        line.push('\n');

        self.stdin
            .write_all(line.as_bytes())
            .map_err(|e| format!("write to sidecar: {e}"))?;
        self.stdin
            .flush()
            .map_err(|e| format!("flush sidecar: {e}"))?;

        let mut response = String::new();
        let read = self
            .stdout
            .read_line(&mut response)
            .map_err(|e| format!("read from sidecar: {e}"))?;

        if read == 0 {
            return Err("sidecar exited unexpectedly".into());
        }

        serde_json::from_str(&response).map_err(|e| format!("invalid JSON from sidecar: {e}"))
    }

    fn handshake(&mut self) -> Result<Value, String> {
        let ping = self.request(json!({ "op": "ping" }))?;
        if ping["ok"] != json!(true) {
            return Err(format!("sidecar did not answer ping: {ping}"));
        }

        let collections = self.request(json!({ "op": "collections" }))?;
        Ok(json!({ "ping": ping, "collections": collections }))
    }
}

fn stop(sidecar: Option<Sidecar>) {
    if let Some(mut sidecar) = sidecar {
        let _ = sidecar.request(json!({ "op": "close" }));
        let _ = sidecar.child.kill();
        let _ = sidecar.child.wait();
    }
}

#[tauri::command]
async fn open_db(
    app: AppHandle,
    bin: Option<String>,
    db: String,
    readonly: bool,
    shared: bool,
    password: Option<String>,
) -> Result<Value, String> {
    let bin = match bin {
        Some(b) if !b.trim().is_empty() => b.trim().to_string(),
        _ => resolve_binary()?.to_string_lossy().into_owned(),
    };
    let password = password.filter(|value| !value.trim().is_empty());
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;

        let mut command = Command::new(&bin);
        command
            .arg(&db)
            .arg("--serve")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null());

        if readonly {
            command.arg("--readonly");
        }
        if shared {
            command.arg("--shared");
        }
        if let Some(password) = &password {
            command.env("LITEDBCLI_PASSWORD", password);
        } else {
            command.env_remove("LITEDBCLI_PASSWORD");
        }

        let mut child = command
            .spawn()
            .map_err(|e| format!("failed to start '{bin}': {e}"))?;

        let stdin = child.stdin.take().ok_or("sidecar has no stdin")?;
        let stdout = child.stdout.take().ok_or("sidecar has no stdout")?;

        let mut sidecar = Sidecar {
            child,
            stdin,
            stdout: BufReader::new(stdout),
            next_id: 1,
        };

        match sidecar.handshake() {
            Ok(info) => {
                let old = guard.replace(sidecar);
                drop(guard);
                stop(old);
                Ok(info)
            }
            Err(error) => {
                stop(Some(sidecar));
                Err(error)
            }
        }
    })
    .await
    .map_err(|e| e.to_string())?
}

#[tauri::command]
async fn run_query(
    app: AppHandle,
    sql: String,
    sort: Option<String>,
    dir: Option<String>,
) -> Result<Value, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;

        let sidecar = guard.as_mut().ok_or("database is not open")?;
        let mut request = json!({ "sql": sql, "limit": 1000 });
        if let Some(sort) = sort {
            request["sort"] = json!(sort);
        }
        if let Some(dir) = dir {
            request["dir"] = json!(dir);
        }
        sidecar.request(request)
    })
    .await
    .map_err(|e| e.to_string())?
}

#[tauri::command]
async fn save_doc(app: AppHandle, collection: String, doc: Value) -> Result<Value, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;

        let sidecar = guard.as_mut().ok_or("database is not open")?;
        sidecar.request(json!({ "op": "save", "collection": collection, "doc": doc }))
    })
    .await
    .map_err(|e| e.to_string())?
}

fn looks_like_connection_string(raw: &str) -> bool {
    if raw.contains(';') {
        return true;
    }
    let key = raw
        .split_once('=')
        .map(|(key, _)| key.trim().to_ascii_lowercase());
    matches!(
        key.as_deref(),
        Some("filename")
            | Some("password")
            | Some("connection")
            | Some("readonly")
            | Some("upgrade")
            | Some("initial size")
            | Some("collation")
            | Some("auto-rebuild")
    )
}

/// Legacy recents could contain a full connection string, including a password.
/// Keep only the file name and whether a password was present.
fn sanitize_path(raw: &str) -> (String, bool) {
    let raw = raw.trim();
    if !looks_like_connection_string(raw) {
        return (raw.to_string(), false);
    }

    let mut filename = None;
    let mut has_password = false;
    for part in raw.split(';') {
        let part = part.trim();
        if part.is_empty() {
            continue;
        }
        let Some((key, value)) = part.split_once('=') else {
            continue;
        };
        match key.trim().to_ascii_lowercase().as_str() {
            "filename" => filename = Some(value.trim().to_string()),
            "password" => has_password = !value.trim().is_empty(),
            _ => {}
        }
    }

    (filename.unwrap_or_default(), has_password)
}

fn normalize_recent(raw: Vec<Value>) -> Vec<Value> {
    let mut recent: Vec<Value> = Vec::new();
    for entry in raw {
        let raw_path = entry
            .get("path")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let (path, inferred_password) = sanitize_path(raw_path);
        if path.is_empty() || recent.iter().any(|e| e["path"] == json!(path)) {
            continue;
        }

        let readonly = entry
            .get("readonly")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let shared = entry
            .get("shared")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let has_password = entry
            .get("hasPassword")
            .and_then(Value::as_bool)
            .unwrap_or(false)
            || inferred_password;
        let opened_at = entry
            .get("openedAt")
            .and_then(Value::as_str)
            .map(str::to_string)
            .unwrap_or_else(|| chrono::Utc::now().to_rfc3339());

        recent.push(json!({
            "path": path,
            "openedAt": opened_at,
            "readonly": readonly,
            "shared": shared,
            "hasPassword": has_password
        }));
        if recent.len() == 10 {
            break;
        }
    }
    recent
}

fn push_recent(
    recent: &mut Vec<Value>,
    path: &str,
    readonly: bool,
    shared: bool,
    has_password: bool,
) {
    let (path, inferred_password) = sanitize_path(path);
    if path.is_empty() {
        return;
    }
    let has_password = has_password || inferred_password;
    let now = chrono::Utc::now().to_rfc3339();
    recent.retain(|entry| entry["path"] != json!(path));
    recent.insert(
        0,
        json!({
            "path": path,
            "openedAt": now,
            "readonly": readonly,
            "shared": shared,
            "hasPassword": has_password
        }),
    );
    recent.truncate(10);
}

fn default_binary_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "litedbcli.exe"
    } else {
        "litedbcli"
    }
}

fn find_on_path(name: &str) -> Option<std::path::PathBuf> {
    let paths = std::env::var_os("PATH")?;
    for dir in std::env::split_paths(&paths) {
        let candidate = if dir.as_os_str().is_empty() {
            std::path::PathBuf::from(name)
        } else {
            dir.join(name)
        };
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// Resolve the litedbcli binary: env LITEDBCLI_BIN -> PATH -> folder of the
/// GUI executable (litedbcli placed next to the GUI binary).
fn resolve_binary() -> Result<std::path::PathBuf, String> {
    let _ = std::env::current_exe();

    let name = default_binary_name();

    if let Ok(env) = std::env::var("LITEDBCLI_BIN") {
        let env = env.trim();
        if !env.is_empty() {
            let path = std::path::PathBuf::from(env);
            if path.is_file() {
                return Ok(path);
            }
            if let Some(found) = find_on_path(env) {
                return Ok(found);
            }
        }
    }

    if let Some(found) = find_on_path(name) {
        return Ok(found);
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let candidate = dir.join(name);
            if candidate.is_file() {
                return Ok(candidate);
            }
        }
    }

    Err(format!(
        "cannot find '{name}': set LITEDBCLI_BIN, put it on PATH, or place {name} next to the GUI binary"
    ))
}

#[tauri::command]
fn binary_path() -> Result<String, String> {
    Ok(resolve_binary()?.to_string_lossy().into_owned())
}

fn state_file(app: &AppHandle) -> Result<std::path::PathBuf, String> {
    let dir = app
        .path()
        .app_data_dir()
        .map_err(|e| format!("cannot resolve app data dir: {e}"))?;
    Ok(dir.join("state.json"))
}

fn read_recent(file: &std::path::Path) -> Vec<Value> {
    let parsed = std::fs::read_to_string(file)
        .ok()
        .and_then(|raw| serde_json::from_str::<Vec<Value>>(&raw).ok())
        .unwrap_or_default();
    normalize_recent(parsed)
}

fn write_recent(file: &std::path::Path, recent: &[Value]) -> Result<(), String> {
    if let Some(dir) = file.parent() {
        std::fs::create_dir_all(dir).map_err(|e| format!("cannot create state dir: {e}"))?;
    }
    let out = serde_json::to_string_pretty(recent).map_err(|e| e.to_string())?;
    let tmp = file.with_extension("json.tmp");
    std::fs::write(&tmp, &out).map_err(|e| format!("cannot write state file: {e}"))?;
    std::fs::rename(&tmp, file).map_err(|e| format!("cannot move state file: {e}"))?;
    Ok(())
}

#[tauri::command]
fn recent_list(app: AppHandle) -> Result<Vec<Value>, String> {
    let file = state_file(&app)?;
    let recent = read_recent(&file);
    if file.exists() {
        let _ = write_recent(&file, &recent);
    }
    Ok(recent)
}

#[tauri::command]
fn remember_recent(
    app: AppHandle,
    path: String,
    readonly: bool,
    shared: bool,
    has_password: bool,
) -> Result<Vec<Value>, String> {
    let file = state_file(&app)?;
    let mut recent = read_recent(&file);
    push_recent(&mut recent, &path, readonly, shared, has_password);
    write_recent(&file, &recent)?;
    Ok(recent)
}

#[tauri::command]
fn forget_recent(app: AppHandle, path: String) -> Result<Vec<Value>, String> {
    let file = state_file(&app)?;
    let (path, _) = sanitize_path(&path);
    let mut recent = read_recent(&file);
    recent.retain(|entry| entry["path"] != json!(path));
    write_recent(&file, &recent)?;
    Ok(recent)
}

#[tauri::command]
fn clear_recent(app: AppHandle) -> Result<Vec<Value>, String> {
    let file = state_file(&app)?;
    write_recent(&file, &[])?;
    Ok(Vec::new())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recent_list_dedups_and_caps() {
        let mut recent: Vec<Value> = Vec::new();
        for path in ["a.db", "b.db", "a.db", "c.db", "b.db"] {
            push_recent(&mut recent, path, false, false, false);
        }
        let paths: Vec<&str> = recent.iter().map(|v| v["path"].as_str().unwrap()).collect();
        assert_eq!(paths, vec!["b.db", "c.db", "a.db"]);
        for entry in &recent {
            assert!(entry["openedAt"].is_string());
            assert!(entry["readonly"].as_bool().is_some());
            assert!(entry["shared"].as_bool().is_some());
            assert!(entry["hasPassword"].as_bool().is_some());
        }
    }

    #[test]
    fn recent_list_caps_at_ten() {
        let mut recent: Vec<Value> = Vec::new();
        for i in 0..15 {
            push_recent(&mut recent, &format!("db{}.db", i), false, false, false);
        }
        assert_eq!(recent.len(), 10);
        assert_eq!(recent[0]["path"], json!("db14.db"));
        assert_eq!(recent[9]["path"], json!("db5.db"));
    }

    #[test]
    fn connection_strings_are_sanitized() {
        let mut recent: Vec<Value> = Vec::new();
        push_recent(
            &mut recent,
            "Filename=/tmp/secret.db;Password=hunter2;Connection=Shared",
            true,
            true,
            false,
        );
        assert_eq!(recent.len(), 1);
        assert_eq!(recent[0]["path"], json!("/tmp/secret.db"));
        assert_eq!(recent[0]["hasPassword"], json!(true));
        assert_eq!(recent[0]["readonly"], json!(true));
        assert_eq!(recent[0]["shared"], json!(true));
        assert!(!recent[0].to_string().contains("hunter2"));
    }

    #[test]
    fn normalize_migrates_legacy_recents() {
        let recent = normalize_recent(vec![
            json!({
                "path": "Filename=/tmp/old.db;Password=secret",
                "readonly": false,
                "shared": true
            }),
            json!({
                "path": "/tmp/old.db",
                "readonly": true,
                "shared": false,
                "hasPassword": false
            }),
            json!({ "path": "/tmp/plain.db" }),
            json!({ "path": "" }),
        ]);

        assert_eq!(recent.len(), 2);
        assert_eq!(recent[0]["path"], json!("/tmp/old.db"));
        assert_eq!(recent[0]["hasPassword"], json!(true));
        assert_eq!(recent[0]["shared"], json!(true));
        assert_eq!(recent[1]["path"], json!("/tmp/plain.db"));
        assert_eq!(recent[1]["hasPassword"], json!(false));
        assert!(!recent[0].to_string().contains("secret"));
    }
}

#[tauri::command]
async fn close_db(app: AppHandle) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;
        stop(guard.take());
        Ok(())
    })
    .await
    .map_err(|e| e.to_string())?
}

fn main() {
    let app = tauri::Builder::default()
        .manage(AppState::default())
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            open_db,
            run_query,
            save_doc,
            close_db,
            remember_recent,
            recent_list,
            forget_recent,
            clear_recent,
            binary_path
        ])
        .build(tauri::generate_context!())
        .expect("error while building tauri application");

    app.run(|app_handle, event| {
        if let tauri::RunEvent::Exit = event {
            let state: State<AppState> = app_handle.state();
            let taken = state.sidecar.lock().ok().and_then(|mut guard| guard.take());
            stop(taken);
        }
    });
}
