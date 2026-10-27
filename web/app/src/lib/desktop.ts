// The desktop app (src-tauri): scenario files through the system's Open and Save dialogs. Only loaded in its build.
import { invoke } from "@tauri-apps/api/core";

/** Asks where to save and writes the file there; the chosen file name, or null when cancelled. */
export function saveFile(name: string, text: string): Promise<string | null> {
  return invoke<string | null>("save_scenario", { name, text });
}

/** Asks for a scenario file; its name and text, or null when cancelled. */
export function openFile(): Promise<{ name: string; text: string } | null> {
  return invoke<{ name: string; text: string } | null>("open_scenario");
}
