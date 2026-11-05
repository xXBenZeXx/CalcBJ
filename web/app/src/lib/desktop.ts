// The desktop and phone apps (src-tauri): scenario files through the app's own Rust commands. On the desktop these
// show the system's Open and Save dialogs; on Android the system's file picker and "Save to" screen; the iPhone app
// saves into its Documents folder. Only loaded inside the apps (files.ts).
import { invoke } from "@tauri-apps/api/core";

/** Saves the file (asking where, except on iPhone); the saved file's name, or null when cancelled. */
export function saveFile(name: string, text: string): Promise<string | null> {
  return invoke<string | null>("save_scenario", { name, text });
}

/** Asks for a scenario file; its name and text, or null when cancelled. */
export function openFile(): Promise<{ name: string; text: string } | null> {
  return invoke<{ name: string; text: string } | null>("open_scenario");
}
