// Opening and saving .bjev scenario files: downloads and the file picker in the browser, the system's dialogs in the
// desktop app (the format is in scenario.ts).
import { readScenario, writeScenario } from "./scenario";
import { app, defaultRules, defaultStrategy, presets } from "./state.svelte";

/** The current scenario, simulation results and compare entries as .bjev text. */
export function scenarioText(): string {
  return writeScenario(app.toScenario());
}

/** Saves the current scenario as a .bjev file and returns its name, or null when the save dialog was cancelled. */
export async function saveScenario(): Promise<string | null> {
  const name = app.fileName || "scenario.bjev";
  if (__DESKTOP__) {
    const saved = await (await import("./desktop")).saveFile(name, scenarioText());
    if (saved) app.fileName = saved;
    return saved;
  }
  const url = URL.createObjectURL(new Blob([scenarioText()], { type: "application/xml" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
  return name;
}

/** Opens .bjev text from this app or the Windows app; throws with a readable message when it can't. */
export function openScenarioText(text: string, name: string): void {
  const s = readScenario(text, { rules: defaultRules(), count: structuredClone(presets[0]), strategy: defaultStrategy() });
  app.loadScenario(s, name);
}

export async function openScenario(file: File): Promise<void> {
  openScenarioText(await file.text(), file.name);
}

/** Desktop app: asks for a scenario file and opens it; its name, or null when the dialog was cancelled. */
export async function openScenarioDialog(): Promise<string | null> {
  const file = await (await import("./desktop")).openFile();
  if (!file) return null;
  openScenarioText(file.text, file.name);
  return file.name;
}
