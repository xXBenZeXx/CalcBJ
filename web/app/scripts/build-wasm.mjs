// Builds the engine as WebAssembly (web/engine-wasm) and copies it next to the code that loads it.
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const crate = fileURLToPath(new URL("../../engine-wasm/", import.meta.url));
// Terminals opened before Rust was installed don't have cargo on their PATH yet.
const local = join(homedir(), ".cargo", "bin", process.platform === "win32" ? "cargo.exe" : "cargo");
const cargo = existsSync(local) ? local : "cargo";

execFileSync(cargo, ["build", "--release", "--target", "wasm32-unknown-unknown"], { cwd: crate, stdio: "inherit" });
const target = fileURLToPath(new URL("../src/lib/engine/calcbj.wasm", import.meta.url));
mkdirSync(dirname(target), { recursive: true });
copyFileSync(join(crate, "target", "wasm32-unknown-unknown", "release", "calcbj_wasm.wasm"), target);
