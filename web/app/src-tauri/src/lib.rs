// CalcBJ as an app of its own: the web interface (web/app, built in "desktop", "android" or "ios" mode) in a native
// window, with the system's dialogs for opening and saving scenario files. Everything, the engine included, runs on
// the device. The desktop program (src/main.rs) and the phone apps (through mobile_entry_point) all start in run().

use serde::Serialize;
use tauri::WebviewWindow;

// Only the iPhone app uses it; tests build it everywhere.
#[cfg(any(target_os = "ios", test))]
mod names;

#[derive(Serialize)]
struct OpenedFile {
    name: String,
    text: String,
}

/// Files written with a byte order mark would otherwise not parse as XML.
fn without_bom(text: String) -> String {
    text.strip_prefix('\u{feff}').map(str::to_owned).unwrap_or(text)
}

/// Asks where to save the scenario and writes it there; the saved file's name, or None when cancelled.
#[tauri::command]
async fn save_scenario(window: WebviewWindow, name: String, text: String) -> Result<Option<String>, String> {
    files::save(&window, name, text)
}

/// Asks for a scenario file and reads it; None when cancelled.
#[tauri::command]
async fn open_scenario(window: WebviewWindow) -> Result<Option<OpenedFile>, String> {
    files::open(&window)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let builder = tauri::Builder::default().plugin(tauri_plugin_dialog::init());
    // On the phones the dialogs return links that only the fs plugin can open (see the mobile `files` below).
    #[cfg(mobile)]
    let builder = builder.plugin(tauri_plugin_fs::init());
    builder
        .invoke_handler(tauri::generate_handler![save_scenario, open_scenario])
        .run(tauri::generate_context!())
        .expect("CalcBJ could not start");
}

/// Windows, Mac and Linux: the dialogs return paths, which std::fs reads and writes.
#[cfg(desktop)]
mod files {
    use std::path::Path;

    use tauri::WebviewWindow;
    use tauri_plugin_dialog::DialogExt;

    use crate::{without_bom, OpenedFile};

    fn file_name(path: &Path) -> String {
        path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
    }

    pub fn save(window: &WebviewWindow, name: String, text: String) -> Result<Option<String>, String> {
        let Some(path) = window
            .dialog()
            .file()
            .set_parent(window)
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

    pub fn open(window: &WebviewWindow) -> Result<Option<OpenedFile>, String> {
        let Some(path) = window
            .dialog()
            .file()
            .set_parent(window)
            .add_filter("CalcBJ scenario", &["bjev", "xml"])
            .blocking_pick_file()
        else {
            return Ok(None);
        };
        let path = path.into_path().map_err(|e| e.to_string())?;
        let text = std::fs::read_to_string(&path).map_err(|e| format!("{}: {e}", path.display()))?;
        Ok(Some(OpenedFile { name: file_name(&path), text: without_bom(text) }))
    }
}

/// Android and iPhone. Their dialogs return links instead of paths: content:// links on Android, which only the
/// system's ContentResolver can open, and file:// links on the iPhone, which may need security-scoped access.
/// tauri-plugin-fs handles both. No dialog gets a file type filter: Android turns extensions into MIME types and has
/// none for .bjev, so .bjev files could not be picked, and on the iPhone files with an extension that no app declares
/// cannot be selected when a filter names it (plugins-workspace issue 3030). The page checks what it reads.
#[cfg(mobile)]
mod files {
    use tauri::{Manager, WebviewWindow};
    use tauri_plugin_dialog::{DialogExt, FilePath};
    use tauri_plugin_fs::FsExt;

    use crate::{without_bom, OpenedFile};

    /// The name of a picked or saved file. The iPhone's file:// links end in it. Android's content:// links mostly do
    /// not ("…/document/msf%3A1000000033"), so Tauri's path plugin asks the file's provider for its display name
    /// (PathPlugin.getFileNameFromUri, a ContentResolver query); None if even that fails.
    fn file_name(window: &WebviewWindow, path: &FilePath) -> Option<String> {
        match path {
            FilePath::Path(p) => p.file_name().map(|n| n.to_string_lossy().into_owned()),
            FilePath::Url(url) if url.scheme() == "file" => {
                url.to_file_path().ok()?.file_name().map(|n| n.to_string_lossy().into_owned())
            }
            FilePath::Url(url) => window.path().file_name(url.as_str()),
        }
    }

    pub fn open(window: &WebviewWindow) -> Result<Option<OpenedFile>, String> {
        // Android: the system's file picker (ACTION_GET_CONTENT). iPhone: the Files picker, which hands over a copy in
        // the app's own temporary folder.
        let Some(path) = window.dialog().file().blocking_pick_file() else {
            return Ok(None);
        };
        let name = file_name(window, &path).unwrap_or_else(|| "scenario.bjev".to_owned());
        let text = window.fs().read_to_string(path).map_err(|e| format!("{name}: {e}"))?;
        Ok(Some(OpenedFile { name, text: without_bom(text) }))
    }

    /// Android: the system's "Save to" screen (ACTION_CREATE_DOCUMENT), where the user picks the folder and may change
    /// the name; the file is then written through the link it returns.
    #[cfg(target_os = "android")]
    pub fn save(window: &WebviewWindow, name: String, text: String) -> Result<Option<String>, String> {
        use std::io::Write;

        // The plugin passes a filter value with a "/" on as the MIME type. With application/octet-stream Android keeps
        // the name as typed; with an XML type it would append ".xml" to "scenario.bjev".
        let Some(path) = window
            .dialog()
            .file()
            .add_filter("CalcBJ scenario", &["application/octet-stream"])
            .set_file_name(name.clone())
            .blocking_save_file()
        else {
            return Ok(None);
        };
        // The user may have changed the name, and Android adds " (1)" to a name already taken in that folder. Should
        // the provider not report the name, the requested one is the best guess.
        let saved = file_name(window, &path).unwrap_or(name);
        // Mode "wt" (write and truncate): when the user picks an existing file to replace, nothing of its old end may
        // remain after the new text.
        let mut options = tauri_plugin_fs::OpenOptions::new();
        options.write(true).truncate(true);
        let mut file = window.fs().open(path, options).map_err(|e| format!("{saved}: {e}"))?;
        file.write_all(text.as_bytes()).map_err(|e| format!("{saved}: {e}"))?;
        Ok(Some(saved))
    }

    /// iPhone: there is no save panel. The dialog plugin's stand-in (an export picker for a file it first creates
    /// empty in Documents) is unreliable to write to (plugins-workspace issues 1763, 1976 and 2089), so the scenario
    /// goes straight into the app's Documents folder, which Info.ios.plist shows in the Files app as
    /// On My iPhone > CalcBJ. A file of the same name there is replaced without asking.
    #[cfg(target_os = "ios")]
    pub fn save(window: &WebviewWindow, name: String, text: String) -> Result<Option<String>, String> {
        let name = crate::names::safe_file_name(&name);
        let folder = window.path().document_dir().map_err(|e| e.to_string())?;
        std::fs::create_dir_all(&folder).map_err(|e| format!("{}: {e}", folder.display()))?;
        std::fs::write(folder.join(&name), text).map_err(|e| format!("{name}: {e}"))?;
        Ok(Some(name))
    }
}
