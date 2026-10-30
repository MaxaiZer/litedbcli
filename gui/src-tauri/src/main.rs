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
    bin: String,
    db: String,
    readonly: bool,
    shared: bool,
) -> Result<Value, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;

        stop(guard.take());

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
                *guard = Some(sidecar);
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
async fn run_query(app: AppHandle, sql: String, limit: Option<u32>) -> Result<Value, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state: State<AppState> = app.state();
        let mut guard = state
            .sidecar
            .lock()
            .map_err(|_| "sidecar state is poisoned".to_string())?;

        let sidecar = guard.as_mut().ok_or("database is not open")?;
        sidecar.request(json!({ "sql": sql, "limit": limit.unwrap_or(1000) }))
    })
    .await
    .map_err(|e| e.to_string())?
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
        .invoke_handler(tauri::generate_handler![open_db, run_query, close_db])
        .build(tauri::generate_context!())
        .expect("error while building tauri application");

    app.run(|app_handle, event| {
        if let tauri::RunEvent::Exit = event {
            let state: State<AppState> = app_handle.state();
            let taken = state
                .sidecar
                .lock()
                .ok()
                .and_then(|mut guard| guard.take());
            stop(taken);
        }
    });
}
