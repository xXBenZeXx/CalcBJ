// Creates the Android project in src-tauri/gen/android and applies CalcBJ's own parts to it: the MainActivity kept in
// src-tauri/android and the launcher icons. The project is Tauri's template and is not committed, so GitHub Actions
// runs this before every Android build (.github/workflows/phone.yml). Running it again is safe: an existing project is
// kept, and our files are copied over it every time.
//
//   node scripts/android-project.mjs
//
// Needs what "tauri android init" needs: a JDK 17 or newer (JAVA_HOME or java on the PATH), the Android SDK
// (ANDROID_HOME) and NDK (NDK_HOME, or the newest one in $ANDROID_HOME/ndk), and the packages from npm ci.
import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, readFileSync, rmSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const appDir = fileURLToPath(new URL("../", import.meta.url));
const tauriDir = join(appDir, "src-tauri");
const projectDir = join(tauriDir, "gen", "android");
const ourActivity = join(tauriDir, "android", "MainActivity.kt");
// The only full-bleed, opaque icon: Android crops launcher icons into its own shape.
const iconSource = "public/icons/icon-maskable-512.png";
// "tauri icon" writes the Android icons into gen/android when that folder exists next to the output folder's parent
// (src-tauri here). Everything else it makes (desktop and iPhone icons) lands in this folder, which is deleted, so
// src-tauri/icons stays as it is.
const iconScratch = "src-tauri/icons-android-tmp";

function fail(message) {
  console.error(`android-project: ${message}`);
  process.exit(1);
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (e) {
    fail(`could not read ${path}: ${e.message}`);
  }
}

/** Runs the npm script "tauri" (the local Tauri CLI) in web/app. Throws an Error when it does not succeed. */
function tauri(args) {
  // Through npm rather than npx: when the packages are missing this fails instead of downloading a CLI. The Android
  // project also records the command it was made with and runs it again during Gradle builds.
  const command = ["run", "tauri", "--", ...args];
  console.log(`> npm ${command.join(" ")}`);
  // npm is npm.cmd on Windows, which Node only starts through a shell, given as one line (the arguments have no
  // spaces or quotes).
  const result =
    process.platform === "win32"
      ? spawnSync(`npm.cmd ${command.join(" ")}`, { cwd: appDir, stdio: "inherit", shell: true })
      : spawnSync("npm", command, { cwd: appDir, stdio: "inherit" });
  if (result.error) throw new Error(`could not start npm: ${result.error.message}`);
  if (result.status !== 0) throw new Error(`"npm ${command.join(" ")}" failed with exit code ${result.status}.`);
}

/** The Android app's identifier: the one in tauri.android.conf.json, which Tauri merges over tauri.conf.json. */
function androidIdentifier() {
  const desktop = readJson(join(tauriDir, "tauri.conf.json")).identifier;
  const androidConfig = join(tauriDir, "tauri.android.conf.json");
  const identifier = existsSync(androidConfig) ? readJson(androidConfig).identifier : undefined;
  if (!identifier || identifier === desktop) {
    // The identifier is baked into the project, and the desktop one must stay with the desktop app (its data folder).
    fail(`src-tauri/tauri.android.conf.json must set the phone app's own "identifier" (com.calcbj.app), not the desktop app's ${desktop}.`);
  }
  return identifier;
}

const identifier = androidIdentifier();
// The Kotlin package folder, as "tauri android init" names it.
const packageDir = join(projectDir, "app", "src", "main", "java", ...identifier.replace(/-/g, "_").split("."));

// 1. Tauri's Android project.
if (existsSync(projectDir)) {
  console.log(`Keeping the existing Android project in ${projectDir}.`);
} else {
  // --ci: no questions; --skip-targets-install: the Rust targets come from rustup (CI installs only the ones it uses).
  try {
    tauri(["android", "init", "--ci", "--skip-targets-install"]);
  } catch (e) {
    fail(e.message);
  }
}
const activity = join(packageDir, "MainActivity.kt");
if (!existsSync(activity)) {
  fail(
    `${activity} is missing: src-tauri/gen/android was made for another identifier or is incomplete. ` +
      "Delete src-tauri/gen/android and run this script again.",
  );
}

// 2. Our MainActivity, in place of the template's.
if (!existsSync(ourActivity)) fail(`${ourActivity} is missing.`);
const declared = /^\s*package\s+([\w.`]+)/m.exec(readFileSync(ourActivity, "utf8"))?.[1]?.replace(/`/g, "");
if (declared !== identifier) {
  fail(`src-tauri/android/MainActivity.kt declares package ${declared ?? "(none)"}, but the app's identifier is ${identifier}.`);
}
copyFileSync(ourActivity, activity);
console.log(`Copied src-tauri/android/MainActivity.kt to ${activity}.`);

// 3. The launcher icons.
const started = Date.now();
rmSync(join(appDir, iconScratch), { recursive: true, force: true });
let iconError = null;
try {
  tauri(["icon", iconSource, "-o", iconScratch]);
} catch (e) {
  iconError = e;
}
// Before a failure is reported (fail() ends the process at once), so that the folder never stays behind.
rmSync(join(appDir, iconScratch), { recursive: true, force: true });
if (iconError) fail(iconError.message);
const adaptiveIcon = join(projectDir, "app", "src", "main", "res", "mipmap-anydpi-v26", "ic_launcher.xml");
// File times can lag the clock by up to a couple of seconds on some file systems.
if (!existsSync(adaptiveIcon) || statSync(adaptiveIcon).mtimeMs < started - 2000) {
  fail(`"tauri icon" did not write the Android icons into ${join(projectDir, "app", "src", "main", "res")}.`);
}
console.log(`Android project ready for ${identifier}: ${projectDir}`);
