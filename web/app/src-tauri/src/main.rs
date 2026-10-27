// CalcBJ as a desktop app: the web interface (web/app, built in "desktop" mode) in a window of its own, with native
// dialogs for opening and saving scenario files. Everything, the engine included, runs on this computer.

// No console window next to the app on Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::path::Path;

use serde::Serialize;
use tauri::WebviewWindow;
use tauri_plugin_dialog::DialogExt;

#[derive(Serialize)]
struct OpenedFile {
    name: String,
    text: String,
}

fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

/// Asks where to save the scenario and writes it there; the chosen file name, or None when cancelled.
#[tauri::command]
async fn save_scenario(window: WebviewWindow, name: String, text: String) -> Result<Option<String>, String> {
    let Some(path) = window
        .dialog()
        .file()
        .set_parent(&window)
        .add_filter("CalcBJ scenario", &["bjev"])
        .set_file_name(name)
        .blocking_save_file()
    else {
        return Ok(None);
    };
    let path = path.into_path().map_err(|e| e.to_string())?;
    std::fs::write(&path, text).map_err(|e| format!("{}: {e}", path.display()))?;
    Ok(Some(file_name(&path)))
}

/// Asks for a scenario file and reads it; None when cancelled.
#[tauri::command]
async fn open_scenario(window: WebviewWindow) -> Result<Option<OpenedFile>, String> {
    let Some(path) = window
        .dialog()
        .file()
        .set_parent(&window)
        .add_filter("CalcBJ scenario", &["bjev", "xml"])
        .blocking_pick_file()
    else {
        return Ok(None);
    };
    let path = path.into_path().map_err(|e| e.to_string())?;
    let text = std::fs::read_to_string(&path).map_err(|e| format!("{}: {e}", path.display()))?;
    // Files written with a byte order mark would otherwise not parse as XML.
    let text = text.strip_prefix('\u{feff}').map(str::to_owned).unwrap_or(text);
    Ok(Some(OpenedFile { name: file_name(&path), text }))
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![save_scenario, open_scenario])
        .run(tauri::generate_context!())
        .expect("CalcBJ could not start");
}
