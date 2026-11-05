// Smoke test of the Android app on an emulator (or a phone), run by .github/workflows/phone.yml inside
// reactivecircus/android-emulator-runner. It installs a debug APK (only debug builds let the WebView be inspected),
// clears the app's data, drives the app with adb (taps, the back and home keys, force-stop) and through the WebView's
// DevTools protocol (reading the page, clicking, localStorage), and writes screenshots, logcat, screen dumps,
// results.json and summary.md into the output folder. The summary also goes to the job's summary on GitHub.
//
//   node scripts/android-smoke.mjs --apk <file.apk, or a folder with one> --out <folder>
//
// Needs adb on the PATH and one device or emulator (ANDROID_SERIAL picks one of several). The exit code is 1 when a
// must-pass check failed; the best-effort checks only warn. Node 24, no packages. The checks find their way by what is
// on screen: the tab, menu and button labels and the page titles, so they keep working when the layout changes.
import { spawnSync } from "node:child_process";
import { appendFileSync, mkdirSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { setTimeout as sleep } from "node:timers/promises";
import { parseArgs } from "node:util";

const PACKAGE = "com.calcbj.app";
const ACTIVITY = `${PACKAGE}/.MainActivity`;
const SETTINGS_KEY = "calcbj.settings.v1";
const SIMS_KEY = "calcbj.sims.v1";
/** The local port that adb forwards to the WebView's DevTools socket. */
const DEVTOOLS_PORT = 9333;
/** The bottom tabs and the titles (h1) of the pages they open. */
const TABS = [
  ["Rules", "Game rules"],
  ["Count", "Count & strategy"],
  ["Simulate", "Simulation"],
  ["Results", "Bet ramp & results"],
];
/** The pages under More; the sheet lists them by their titles. */
const MORE_PAGES = ["Bankroll & risk", "Charts", "Sensitivity sweep", "Compare games", "Guide"];
const KEY_HOME = 3;
const KEY_BACK = 4;

// ---------------------------------------------------------------------------------------------------------------
// Reading what adb and the page report (no device needed; checked on their own by a test).

/** The app's WebView DevTools socket in /proc/net/unix (abstract sockets are listed with an "@"), or null. */
export function devtoolsSocket(procNetUnix, pid) {
  const name = `webview_devtools_remote_${pid}`;
  return procNetUnix.split("\n").some((line) => line.trim().split(/\s+/).at(-1) === `@${name}`) ? name : null;
}

/** The package whose window has the focus, from "dumpsys window"; a popup over an app counts as that app. */
export function focusedPackage(dumpsys) {
  const window = /mCurrentFocus=Window\{\S+ \S+ ([\w.]+)\/[^\s}]*\}/.exec(dumpsys);
  if (window) return window[1];
  const app = /mFocusedApp=.*?ActivityRecord\{\S+ \S+ ([\w.]+)\//.exec(dumpsys);
  return app ? app[1] : null;
}

/** The app named in an "Application Not Responding" dialog that has the focus, or null. */
export function notRespondingApp(dumpsys) {
  return /mCurrentFocus=Window\{[^}]*Not Responding: ([\w.]+)\}/.exec(dumpsys)?.[1] ?? null;
}

const INSET_TYPES = { ITYPE_STATUS_BAR: "statusBars", ITYPE_NAVIGATION_BAR: "navigationBars", ITYPE_IME: "ime" };

/**
 * The visible system bars and keyboard, from "dumpsys window": { statusBars, navigationBars, ime } as frames in
 * screen pixels ({ left, top, right, bottom }), each missing when it is not on screen.
 */
export function insetFrames(dumpsys) {
  const frames = {};
  const source = /InsetsSource\b[^\n]*?type=(\w+)[^\n]*?frame=\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\][^\n]*?visible=(true|false)/g;
  for (const m of dumpsys.matchAll(source)) {
    const type = INSET_TYPES[m[1]] ?? m[1];
    const [left, top, right, bottom] = m.slice(2, 6).map(Number);
    if (m[6] === "true" && right > left && bottom > top) frames[type] ??= { left, top, right, bottom };
  }
  return frames;
}

/** The nodes of a uiautomator screen dump, with their bounds in screen pixels. */
export function uiNodes(xml) {
  const decode = (s) =>
    s.replace(/&(#x[\da-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, e) => {
      const named = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" }[e.toLowerCase()];
      if (named) return named;
      return String.fromCodePoint(e[1].toLowerCase() === "x" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10));
    });
  return [...xml.matchAll(/<node\b([^>]*?)\/?>/g)].map((m) => {
    const a = Object.fromEntries([...m[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(([, k, v]) => [k, decode(v)]));
    const b = /\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]/.exec(a.bounds ?? "");
    return {
      text: a.text ?? "",
      id: a["resource-id"] ?? "",
      className: a.class ?? "",
      packageName: a.package ?? "",
      description: a["content-desc"] ?? "",
      enabled: a.enabled !== "false",
      bounds: b ? { left: +b[1], top: +b[2], right: +b[3], bottom: +b[4] } : null,
    };
  });
}

/** The WebView's version from its user agent ("Chrome/1xx.0.…"), or null. */
export function chromeVersion(userAgent) {
  return /Chrome\/([\d.]+)/.exec(userAgent ?? "")?.[1] ?? null;
}

/**
 * The app's problems in logcat (threadtime format): console errors of the page (wry logs the WebView's console with
 * the tag Tauri/Console in debug builds), Java and native crashes, Rust panics and "not responding" reports.
 */
export function logcatProblems(text) {
  const consoleErrors = [];
  const crashes = [];
  const entries = text
    .split(/\r?\n/)
    .map((line) => /^\S+\s+\S+\s+\d+\s+\d+\s+([VDIWEFA])\s+(.*?)\s*: (.*)$/.exec(line))
    .filter(Boolean)
    .map(([, level, tag, message]) => ({ level, tag, message }));
  entries.forEach(({ level, tag, message }, i) => {
    if (tag === "Tauri/Console" && level === "E") {
      const parts = /^File: (.*?) - Line (\d+) - Msg: (.*)$/.exec(message);
      consoleErrors.push(parts ? `${parts[3]} (${parts[1]}:${parts[2]})` : message);
    } else if (tag === "AndroidRuntime" && message.includes("FATAL EXCEPTION")) {
      // The crash report's next two lines name the process and the exception.
      const next = entries.slice(i + 1, i + 3).filter((e) => e.tag === "AndroidRuntime").map((e) => e.message);
      if (next[0]?.startsWith(`Process: ${PACKAGE},`)) crashes.push([message, ...next].join(" "));
    } else if (tag === "libc" && message.includes("Fatal signal") && message.includes(PACKAGE)) {
      crashes.push(message);
    } else if (tag === "RustStdoutStderr" && message.includes("panicked at")) {
      crashes.push(`Rust: ${message}`);
    } else if (tag === "ActivityManager" && message.startsWith(`ANR in ${PACKAGE}`)) {
      crashes.push(message);
    }
  });
  return { consoleErrors, crashes };
}

/**
 * What went wrong outside the app, from logcat: an app or the system UI not responding or crashing, and processes that
 * died or were killed. On a slow emulator these explain a stalled check (the app pauses while something covers it).
 */
export function systemTrouble(text) {
  const trouble = /ANR in |am_anr|Application Not Responding|FATAL EXCEPTION|am_crash|has died|lowmemorykiller|am_kill|Force finishing/;
  return text.split(/\r?\n/).filter((line) => trouble.test(line)).map((line) => line.replace(/\s+/g, " ").trim());
}

/** 10M, 2B: round counts as the app shows them. */
export function roundsText(n) {
  if (!Number.isFinite(n)) return String(n);
  if (n >= 1e9) return `${+(n / 1e9).toFixed(2)}B`;
  if (n >= 1e6) return `${+(n / 1e6).toFixed(1)}M`;
  return String(n);
}

/** The test's report as Markdown: the device, one row per check, and the page's errors after startup. */
export function summaryMarkdown({ device, checks, laterErrors }) {
  const cell = (s) => String(s ?? "").replace(/\|/g, "\\|").replace(/\r?\n/g, "<br>");
  const failed = checks.filter((c) => c.required && c.status !== "pass");
  const optional = checks.filter((c) => !c.required);
  const about = [
    device.android && `Android ${device.android} (API ${device.sdk}) on ${device.model}`,
    device.size && `${device.size} px at ${device.density} dpi`,
    device.webview && `WebView ${device.webview}${device.webviewPackage ? ` (${device.webviewPackage})` : ""}`,
    device.ready && `app ready ${device.ready}`,
  ].filter(Boolean);
  const lines = [
    `## Android smoke test: ${failed.length ? "FAILED" : "passed"}`,
    "",
    ...(about.length ? [about.join(" · "), ""] : []),
    "| Result | Check | Details |",
    "|---|---|---|",
    ...checks.map((c) => `| ${c.status.toUpperCase()} | ${cell(c.name)}${c.required ? "" : " (best effort)"} | ${cell(c.details)} |`),
    "",
    `Must pass: ${checks.length - optional.length - failed.length} of ${checks.length - optional.length} passed. ` +
      `Best effort: ${optional.filter((c) => c.status === "pass").length} of ${optional.length} passed.`,
  ];
  if (laterErrors.length) {
    lines.push("", "Errors the page reported after startup:", "", ...laterErrors.slice(0, 20).map((e) => `- ${cell(e)}`));
  }
  lines.push("", "Screenshots, logcat and the screen dumps are in this run's android-smoke-test artifact.", "");
  return lines.join("\n");
}

// ---------------------------------------------------------------------------------------------------------------
// The device.

/** Runs adb and returns what it printed (a Buffer with `binary`); throws when it fails, unless `check` is false. */
function adb(args, { binary = false, check = true, timeout = 60_000 } = {}) {
  const r = spawnSync("adb", args, { encoding: binary ? "buffer" : "utf8", maxBuffer: 512 * 1024 * 1024, timeout });
  if (r.error) throw new Error(`adb ${args.join(" ")}: ${r.error.message}`);
  if (check && r.status !== 0) throw new Error(`adb ${args.join(" ")} failed: ${String(r.stderr || r.stdout).trim()}`);
  return r.stdout;
}

function shell(command, options) {
  return String(adb(["shell", command], options)).trim();
}

function key(code) {
  shell(`input keyevent ${code}`);
}

function tap(x, y) {
  shell(`input tap ${Math.round(x)} ${Math.round(y)}`);
}

function tapNode(node) {
  tap((node.bounds.left + node.bounds.right) / 2, (node.bounds.top + node.bounds.bottom) / 2);
}

function windowFocus() {
  return shell("dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'", { check: false });
}

function systemInsets() {
  return insetFrames(shell("dumpsys window | grep InsetsSource", { check: false }));
}

/** Calls `test` every half second until it returns something truthy, which it returns; throws after `seconds`. */
async function waitFor(what, seconds, test) {
  const end = Date.now() + seconds * 1000;
  let last = null;
  for (;;) {
    try {
      const result = await test();
      if (result) return result;
    } catch (e) {
      last = e;
    }
    if (Date.now() > end) throw new Error(`no ${what} after ${seconds} s${last ? ` (${last.message})` : ""}`);
    await sleep(500);
  }
}

/**
 * Waits until the package service answers, and the user's storage is unlocked, three times in a row a few seconds
 * apart. An emulator reports that it has booted before both are reliably there, and a restart of its system server
 * takes them away again for a while: an install sent then fails with "Failure calling service package: Broken pipe".
 */
async function waitForPackageService(seconds = 300) {
  let streak = 0;
  await waitFor("package service answering three times in a row", seconds, async () => {
    let ready = false;
    try {
      const answers = /^package:/m.test(shell("pm path android", { check: false, timeout: 30_000 }));
      // Empty on images that don't have the property: that counts as unlocked.
      ready = answers && shell("getprop sys.user.0.ce_available", { check: false, timeout: 30_000 }) !== "false";
    } catch {
      // adb itself failed (the device went offline for a moment): not ready.
    }
    streak = ready ? streak + 1 : 0;
    if (streak >= 3) return true;
    await sleep(2500);
    return false;
  });
}

/** The device's CPU time so far, all cores together, idle and in total (from /proc/stat); null if it can't be read. */
function cpuTimes() {
  const fields = shell("head -n 1 /proc/stat", { check: false }).split(/\s+/);
  const n = fields.slice(1).map(Number);
  if (fields[0] !== "cpu" || n.length < 5 || n.some((v) => !Number.isFinite(v))) return null;
  // idle and iowait
  return { idle: n[3] + n[4], total: n.reduce((a, b) => a + b, 0) };
}

/**
 * Waits until the device has settled after booting: the status bar is on screen (the system UI runs) and the cores
 * have been at least 60% idle for 15 s. Right after booting, an emulator on 2 cores is busy for a minute or more, and
 * in the fourth CI run its system UI stopped answering and was restarted meanwhile, which hid the app. Gives up after
 * `seconds` and says so; the test then carries on.
 */
async function waitForCalm(seconds = 240) {
  const start = Date.now();
  let previous = cpuTimes();
  let calm = 0;
  let idle = null;
  const load = () => (idle === null ? "CPU load unknown" : `${Math.round(idle * 100)}% idle`);
  while (Date.now() - start < seconds * 1000) {
    await sleep(5000);
    const now = cpuTimes();
    idle = previous && now && now.total > previous.total ? (now.idle - previous.idle) / (now.total - previous.total) : null;
    previous = now;
    calm = systemInsets().statusBars && (idle === null || idle >= 0.6) ? calm + 1 : 0;
    if (calm >= 3) return `settled after ${Math.round((Date.now() - start) / 1000)} s (${load()})`;
  }
  return `still busy after ${seconds} s (${load()}), testing anyway`;
}

/** Installs the APK once the package service is there; tries again (copying the APK first) when that fails. */
async function installApk(apk) {
  let last = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    await waitForPackageService();
    try {
      // --no-streaming from the second attempt on: adb copies the APK to the device, then installs it from there.
      adb(["install", "-r", "-t", ...(attempt > 1 ? ["--no-streaming"] : []), apk], { timeout: 300_000 });
      return attempt;
    } catch (e) {
      last = e;
      console.log(`  install attempt ${attempt} failed: ${e.message}`);
      await sleep(5000);
    }
  }
  throw last;
}

// ---------------------------------------------------------------------------------------------------------------
// The page, through the WebView's DevTools protocol.

function exceptionText(details) {
  const what = details.exception?.description?.split("\n")[0] ?? details.text;
  return details.url ? `${what} (${details.url}:${details.lineNumber + 1})` : what;
}

/**
 * A DevTools protocol connection over Node's WebSocket, which sends no Origin header, so the WebView accepts it.
 * (Exported, like pageAction, for a test against a desktop Chrome.)
 */
export class DevTools {
  static open(url) {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      ws.onopen = () => resolve(new DevTools(ws));
      ws.onerror = () => reject(new Error(`could not connect to ${url}`));
    });
  }

  onEvent = () => {};
  #ws;
  #next = 0;
  #pending = new Map();

  constructor(ws) {
    this.#ws = ws;
    ws.onerror = () => {};
    ws.onmessage = (e) => this.#receive(JSON.parse(String(e.data)));
    ws.onclose = () => {
      for (const p of this.#pending.values()) p.reject(new Error("the DevTools connection closed"));
      this.#pending.clear();
    };
  }

  #receive(message) {
    if (message.id === undefined) {
      this.onEvent(message.method, message.params);
      return;
    }
    const pending = this.#pending.get(message.id);
    if (!pending) return;
    this.#pending.delete(message.id);
    clearTimeout(pending.timer);
    if (message.error) pending.reject(new Error(`${pending.method}: ${message.error.message}`));
    else pending.resolve(message.result);
  }

  send(method, params = {}, seconds = 30) {
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`${method}: no answer after ${seconds} s`));
      }, seconds * 1000);
      this.#pending.set(id, { method, resolve, reject, timer });
      try {
        this.#ws.send(JSON.stringify({ id, method, params }));
      } catch (e) {
        this.#pending.delete(id);
        clearTimeout(timer);
        reject(new Error(`${method}: ${e.message}`));
      }
    });
  }

  /** Calls `fn` in the page with `args` (both sent as text, so `fn` must not use anything from this script). */
  async evaluate(fn, ...args) {
    const r = await this.send("Runtime.evaluate", {
      expression: `(${fn})(...${JSON.stringify(args)})`,
      awaitPromise: true,
      returnByValue: true,
      // Counts as a tap: what the interface opens on a tap (file pickers) opens on these clicks too.
      userGesture: true,
    });
    if (r.exceptionDetails) throw new Error(`in the page: ${exceptionText(r.exceptionDetails)}`);
    return r.result.value;
  }

  close() {
    try {
      this.#ws.close();
    } catch {
      // Already closed.
    }
  }
}

/**
 * Runs in the page (sent as text into Runtime.evaluate): reads and drives the interface by what is on screen.
 * Buttons are found by their words without the leading glyph ("♠Rules" is "Rules"); hidden ones (the desktop
 * sidebar) don't count.
 */
export function pageAction(action, ...args) {
  const visible = (el) => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const words = (el) =>
    (el.textContent ?? "")
      .replace(/\s+/g, " ")
      .trim()
      .replace(/^[^\p{L}\p{N}]+/u, "");
  const buttons = () => [...document.querySelectorAll("button")].filter(visible);
  const inHeader = (el) => !!el.closest("header");
  const title = () => {
    const h1 = [...document.querySelectorAll("h1")].find(visible);
    return h1 ? (h1.textContent ?? "").replace(/\s+/g, " ").trim() : "";
  };
  const text = () => document.body.innerText;
  const roundsSelect = () =>
    [...document.querySelectorAll("select")].find((s) => [...s.options].some((o) => / rounds$/.test(o.textContent.trim())));
  const rect = (el) => {
    const r = el.getBoundingClientRect();
    return { left: r.left, top: r.top, right: r.right, bottom: r.bottom };
  };
  const stored = (key) => {
    try {
      return JSON.parse(localStorage.getItem(key) ?? "null");
    } catch {
      return "unreadable";
    }
  };
  const press = (button) => {
    button.click();
    return words(button);
  };

  const actions = {
    info: () => ({
      userAgent: navigator.userAgent,
      dpr: devicePixelRatio,
      width: innerWidth,
      height: innerHeight,
      cores: navigator.hardwareConcurrency,
      // The interface is up, or the message that it could not start is.
      ready: document.readyState === "complete" && (title() !== "" || /could not start/i.test(text())),
      title: title(),
    }),
    title,
    text,
    buttons: () => buttons().map(words),
    click(label) {
      const found = buttons().filter((b) => words(b) === label && !b.disabled);
      // The top bar's own button first (the Simulation page has a Run button of its own).
      const button = found.find(inHeader) ?? found[0];
      if (!button) throw new Error(`no "${label}" button; there are: ${buttons().map(words).join(" | ")}`);
      return press(button);
    },
    /** Where a button is, found as `click` finds it, in CSS pixels: for a real tap through adb. */
    buttonRect(label) {
      const found = buttons().filter((b) => words(b) === label && !b.disabled);
      const button = found.find(inHeader) ?? found[0];
      if (!button) throw new Error(`no "${label}" button; there are: ${buttons().map(words).join(" | ")}`);
      return { rect: rect(button), dpr: devicePixelRatio };
    },
    /** The page's place in the WebView's history, for the back checks' reports. */
    history: () => `${history.length} entries, this one ${JSON.stringify(history.state)}`,
    /** The top bar's Run button: "Run simulation", or "Add 10M rounds" once there are results. */
    clickRun() {
      const isRun = (b) => words(b).includes("Run simulation") || /^Add .* rounds$/.test(words(b));
      const button = buttons().find((b) => inHeader(b) && isRun(b)) ?? buttons().find((b) => isRun(b) && !b.disabled);
      if (!button) throw new Error(`no Run button; there are: ${buttons().map(words).join(" | ")}`);
      return press(button);
    },
    /** A Stop button that can be pressed: the top bar's, while a simulation runs. */
    running: () => buttons().some((b) => !b.disabled && words(b).startsWith("Stop")),
    stop() {
      const button = buttons().find((b) => !b.disabled && words(b).startsWith("Stop"));
      return button ? press(button) : null;
    },
    /** The Simulation page's "Running: x% of …" as a number, or null. */
    progress: () => {
      const m = /Running: ([\d.]+)% of/i.exec(text());
      return m ? Number(m[1]) : null;
    },
    moreOpen: () => buttons().some((b) => words(b) === "Open scenario…"),
    storage: (settingsKey, simsKey) => {
      const settings = stored(settingsKey);
      const sims = stored(simsKey);
      return {
        settings: settings && typeof settings === "object" ? { simRounds: settings.simRounds, threads: settings.threads, page: settings.page } : settings,
        sims: Array.isArray(sims) ? sims.map((d) => ({ key: d?.key, rounds: d?.rounds, seconds: d?.seconds, threads: d?.threads })) : sims,
      };
    },
    setRounds(n) {
      const select = roundsSelect();
      if (!select) throw new Error("no rounds-to-simulate list on this page");
      const option = [...select.options].find((o) => o.value === String(n));
      if (!option) throw new Error(`no ${n} in the rounds list: ${[...select.options].map((o) => o.value).join(", ")}`);
      select.value = option.value;
      // What a choice in the list fires; the page's binding listens to it.
      select.dispatchEvent(new Event("change", { bubbles: true }));
      return select.value;
    },
    roundsValue: () => roundsSelect()?.value ?? null,
    /** Types `n` into the Simulation page's "CPU threads" field and leaves it, which is when the field commits. */
    setThreads(n) {
      const field = [...document.querySelectorAll("label")].find(
        (l) => (l.querySelector(".label")?.textContent ?? "").trim() === "CPU threads",
      );
      const input = field?.querySelector("input");
      if (!input) throw new Error("no CPU threads field on this page");
      input.value = String(n);
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.dispatchEvent(new Event("change", { bubbles: true }));
      return input.value;
    },
    /** Whether the page counts as on screen: a phone app pauses its simulation while it does not. */
    visibility: () => document.visibilityState,
    /** The Simulation page's results: the "Rounds simulated" figure, and whether it says there are none. */
    results: () => ({
      // innerText follows CSS text-transform, and the tile titles are in capitals: these ignore case.
      rounds: /Rounds simulated\s+(\d[\d.,]*\s*[KMB]?)/i.exec(text())?.[1] ?? null,
      none: /No results yet|Run a simulation to see/i.test(text()),
    }),
    /** The safe areas in CSS pixels: the CSS variables (env() or the Android app's bridge), env() itself, the bridge. */
    insets: () => {
      const probe = (css) => {
        const d = document.createElement("div");
        d.style.cssText = `position:fixed;visibility:hidden;padding-top:${css}`;
        document.body.append(d);
        const px = parseFloat(getComputedStyle(d).paddingTop) || 0;
        d.remove();
        return px;
      };
      let bridge = null;
      try {
        bridge = window.CalcBJInsets ? JSON.parse(window.CalcBJInsets.get()) : null;
      } catch (e) {
        bridge = `unreadable: ${e}`;
      }
      const h1 = [...document.querySelectorAll("h1")].find(visible);
      const bar = buttons().find((b) => words(b) === "Rules")?.closest("nav");
      return {
        safeTop: probe("var(--safe-top, 0px)"),
        safeBottom: probe("var(--safe-bottom, 0px)"),
        envTop: probe("env(safe-area-inset-top, 0px)"),
        envBottom: probe("env(safe-area-inset-bottom, 0px)"),
        bridge,
        titleTop: h1 ? h1.getBoundingClientRect().top : null,
        tabBarBottom: bar ? bar.getBoundingClientRect().bottom : null,
        height: innerHeight,
        dpr: devicePixelRatio,
      };
    },
    /** Marks the text field lowest on the page and scrolls it to just above the tab bar; returns where it is. */
    keyboardTarget() {
      const skip = ["checkbox", "radio", "range", "file", "hidden", "button", "submit", "color"];
      // Not in a part of a wide table that is scrolled out of view sideways: the tap has to land on it.
      const across = (el) => el.getBoundingClientRect().left >= 0 && el.getBoundingClientRect().right <= innerWidth;
      const fields = [...document.querySelectorAll("input, textarea")].filter(
        (el) => visible(el) && !el.disabled && !el.readOnly && !skip.includes(el.type) && across(el),
      );
      if (!fields.length) throw new Error("no text field on this page");
      const top = (el) => el.getBoundingClientRect().top + scrollY;
      const field = fields.reduce((a, b) => (top(b) > top(a) ? b : a));
      for (const el of document.querySelectorAll("[data-smoke-field]")) el.removeAttribute("data-smoke-field");
      field.setAttribute("data-smoke-field", "");
      const bar = buttons().find((b) => words(b) === "Rules")?.closest("nav");
      const above = bar && visible(bar) ? bar.getBoundingClientRect().top : innerHeight;
      scrollBy({ top: field.getBoundingClientRect().bottom - (above - 12), behavior: "instant" });
      return { rect: rect(field), dpr: devicePixelRatio, label: field.getAttribute("aria-label") ?? "" };
    },
    /** The marked field: focused or not, where it is, and whether something of the page lies over its middle. */
    keyboardField() {
      const field = document.querySelector("[data-smoke-field]");
      if (!field) return null;
      const r = field.getBoundingClientRect();
      const x = (r.left + r.right) / 2;
      const y = (r.top + r.bottom) / 2;
      const hit = y >= 0 && y < innerHeight ? document.elementFromPoint(x, y) : null;
      const covered = !hit || !(hit === field || field.contains(hit));
      return {
        focused: document.activeElement === field,
        rect: rect(field),
        height: innerHeight,
        coveredBy: covered ? (hit ? `${hit.tagName.toLowerCase()}.${hit.className}` : "nothing (off screen)") : null,
      };
    },
    blur: () => {
      document.activeElement?.blur?.();
      return true;
    },
  };
  return actions[action](...args);
}

/** The app on the device: starting it, and the DevTools connection to its page. */
class App {
  devtools = null;
  /** Errors the page reported through the DevTools protocol, with the check that was running. */
  errors = [];
  phase = "startup";

  /** Starts the app (or brings it to the front), connects to its page and waits for a page title. */
  async launch() {
    const started = Date.now();
    const output = shell(`am start -W -n ${ACTIVITY}`);
    if (/Error|Exception/.test(output)) throw new Error(output);
    await this.connect();
    await waitFor("page title", 60, async () => (await this.page("info")).ready);
    return (Date.now() - started) / 1000;
  }

  async connect() {
    const pid = await waitFor("app process", 30, () => shell(`pidof ${PACKAGE}`, { check: false }).split(/\s+/)[0] || null);
    const socket = await waitFor("WebView DevTools socket (only debug builds have one)", 60, () =>
      devtoolsSocket(shell("cat /proc/net/unix"), pid),
    );
    adb(["forward", `tcp:${DEVTOOLS_PORT}`, `localabstract:${socket}`]);
    const target = await waitFor("CalcBJ page in the WebView", 60, async () => {
      const response = await fetch(`http://127.0.0.1:${DEVTOOLS_PORT}/json/list`, { signal: AbortSignal.timeout(5000) });
      const targets = await response.json();
      // Android serves the app from http://tauri.localhost/ (a port only appears in a test with a desktop Chrome).
      return targets.find((t) => t.type === "page" && /^https?:\/\/tauri\.localhost(:\d+)?\//.test(t.url)) ?? null;
    });
    this.devtools?.close();
    this.devtools = await DevTools.open(`ws://127.0.0.1:${DEVTOOLS_PORT}/devtools/page/${target.id}`);
    this.devtools.onEvent = (method, params) => this.#event(method, params);
    // Both also report what the page logged before this connection: V8 keeps console messages and uncaught errors.
    await this.devtools.send("Runtime.enable");
    await this.devtools.send("Log.enable");
  }

  #event(method, params) {
    let text = null;
    if (method === "Runtime.exceptionThrown") text = exceptionText(params.exceptionDetails);
    else if (method === "Runtime.consoleAPICalled" && (params.type === "error" || params.type === "assert")) {
      text = `console.${params.type}: ${params.args.map((a) => a.value ?? a.description ?? a.type).join(" ")}`;
    } else if (method === "Log.entryAdded" && params.entry.level === "error") {
      text = params.entry.url ? `${params.entry.text} (${params.entry.url})` : params.entry.text;
    }
    if (text && !this.errors.some((e) => e.text === text && e.phase === this.phase)) this.errors.push({ phase: this.phase, text });
  }

  page(action, ...args) {
    if (!this.devtools) throw new Error("not connected to the app's page");
    return this.devtools.evaluate(pageAction, action, ...args);
  }

  storage() {
    return this.page("storage", SETTINGS_KEY, SIMS_KEY);
  }

  async waitForTitle(title, seconds = 20) {
    let last = "";
    await waitFor(`page "${title}"`, seconds, async () => {
      last = await this.page("title");
      return last.endsWith(title);
    }).catch((e) => {
      throw new Error(`${e.message}; the title is "${last}"`);
    });
  }

  async openMore() {
    await this.page("click", "More");
    await waitFor("More sheet", 10, () => this.page("moreOpen"));
  }

  async open(tab, title) {
    await this.page("click", tab);
    await this.waitForTitle(title);
  }

  close() {
    this.devtools?.close();
    this.devtools = null;
  }
}

/** The most rounds kept for any settings in calcbj.sims.v1 (0 when there are none). */
function mostRounds(stored) {
  return Array.isArray(stored.sims) ? Math.max(0, ...stored.sims.map((d) => d.rounds ?? 0)) : 0;
}

// ---------------------------------------------------------------------------------------------------------------
// The test.

async function main() {
  const { values } = parseArgs({ options: { apk: { type: "string" }, out: { type: "string", default: "android-smoke" } } });
  if (!values.apk) throw new Error("usage: node scripts/android-smoke.mjs --apk <file.apk or folder> --out <folder>");
  const out = values.out;
  mkdirSync(out, { recursive: true });
  let apk = values.apk;
  if (statSync(apk).isDirectory()) {
    const found = readdirSync(apk).filter((f) => f.endsWith(".apk"));
    if (found.length !== 1) throw new Error(`expected one .apk in ${apk}, found ${found.length}`);
    apk = join(apk, found[0]);
  }

  const log = (line) => console.log(line);
  const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
  let shots = 0;
  const screenshot = (name) => {
    try {
      const png = adb(["exec-out", "screencap", "-p"], { binary: true });
      writeFileSync(join(out, `${String(++shots).padStart(2, "0")}-${name}.png`), png);
    } catch (e) {
      log(`  (no screenshot: ${e.message})`);
    }
  };
  let dumps = 0;
  /** What is on screen, from uiautomator (also saved, for looking into a failure). */
  const screenDump = async (name) => {
    for (let attempt = 1; attempt <= 3; attempt++) {
      if (/dumped to/i.test(shell("uiautomator dump /sdcard/smoke-ui.xml", { check: false }))) {
        const xml = shell("cat /sdcard/smoke-ui.xml");
        writeFileSync(join(out, `ui-${String(++dumps).padStart(2, "0")}-${name}.xml`), xml);
        return uiNodes(xml);
      }
      await sleep(1000);
    }
    throw new Error("uiautomator could not read the screen");
  };
  const focused = () => focusedPackage(windowFocus());

  const app = new App();
  const device = {};
  const checks = [];
  /** What later checks need from earlier ones. */
  const facts = {};
  /** Why the remaining checks cannot run (the app did not start). */
  let blocked = null;

  /** Runs one check; a required one that fails makes the test fail, an optional one only warns. */
  const check = async (name, required, run) => {
    log(`\n== ${name}${required ? "" : " (best effort)"}`);
    if (blocked) {
      checks.push({ name, required, status: "skip", details: `not run: ${blocked}` });
      log(`  SKIP: ${blocked}`);
      return false;
    }
    app.phase = name;
    try {
      const details = (await run()) ?? "";
      checks.push({ name, required, status: "pass", details });
      log(`  PASS: ${details}`);
      return true;
    } catch (e) {
      checks.push({ name, required, status: required ? "fail" : "warn", details: e.message });
      log(`  ${required ? "FAIL" : "WARN"}: ${e.message}`);
      screenshot(`problem-${slug(name)}`);
      return false;
    }
  };

  /** After the system's file picker: back presses until the app has the screen, or a start when they left it. */
  const backToApp = async () => {
    for (let i = 0; i < 4 && focused() !== PACKAGE; i++) {
      key(KEY_BACK);
      await sleep(1500);
    }
    if (focused() !== PACKAGE) {
      shell(`am start -n ${ACTIVITY}`, { check: false });
      await sleep(2000);
    }
  };

  /** Waits until another app (the launcher, say) has the screen; returns its package. */
  const waitForOtherApp = (what) =>
    waitFor(what, 20, () => {
      const p = focused();
      return p && p !== PACKAGE ? p : null;
    });

  /** Shows a place (Downloads) in the system's file picker: from its drawer of places, or from the screen as it is. */
  const pickerShow = async (place) => {
    let nodes = await screenDump("picker");
    const drawer = nodes.find((n) => /show roots|navigation drawer/i.test(n.description) && n.bounds);
    if (drawer) {
      tapNode(drawer);
      await sleep(1500);
      nodes = await screenDump("picker-places");
    }
    const target = nodes.find((n) => n.text === place && n.bounds);
    if (!target) throw new Error(`no "${place}" in the file picker; it shows: ${nodes.map((n) => n.text).filter(Boolean).join(" | ")}`);
    tapNode(target);
    await sleep(2000);
  };

  const waitForNotice = (pattern, what) => waitFor(what, 15, async () => pattern.exec(await app.page("text"))?.[0] ?? null);

  /** The app's message about a file ("Saved …", "Opened …", "Could not …"), or null. */
  const FILE_NOTICE = /(Saved |Opened |Could not (save|open))[^\n]*/;
  const fileNotice = async () => FILE_NOTICE.exec(await app.page("text"))?.[0] ?? null;

  /**
   * Presses a More-sheet item that opens the system's file screen and waits for that screen. A new message of the app
   * instead ends the wait: an error, or a save that needed no screen (a download, which the Android WebView ignores).
   */
  const openFileScreen = async (item, what) => {
    const before = await fileNotice();
    await app.openMore();
    await app.page("click", item);
    const result = await waitFor(what, 20, async () => {
      const p = focused();
      if (p && p !== PACKAGE) return { screen: p };
      const notice = await fileNotice();
      return notice && notice !== before ? { notice } : null;
    });
    if (result.notice) throw new Error(`no system file screen; the app says: ${result.notice}`);
    // The screen takes a moment to fill.
    await sleep(1500);
    return result.screen;
  };

  /** Opens the Simulation page and sets "Rounds to simulate", waiting until the setting is saved. */
  const setRounds = async (n) => {
    await app.open("Simulate", "Simulation");
    await app.page("setRounds", n);
    await waitFor(`saved rounds setting of ${roundsText(n)}`, 10, async () => (await app.storage()).settings?.simRounds === n);
  };

  /** Force-stops the app and starts it again, connected to the new page. */
  const restart = async (phase) => {
    shell(`am force-stop ${PACKAGE}`);
    await sleep(1000);
    app.close();
    app.phase = phase;
    return app.launch();
  };

  /** The WebView's top left corner on the screen, to turn the page's CSS pixels into screen pixels for a tap. */
  const webViewOrigin = async () => {
    const view = (await screenDump("webview")).find((n) => n.className === "android.webkit.WebView" && n.bounds)?.bounds;
    return { left: view?.left ?? 0, top: view?.top ?? 0 };
  };

  /**
   * Closes a system dialog that has taken the focus, such as "System UI isn't responding" on a slow emulator, with Wait
   * (or OK or Close app): it would take the taps meant for the app. Returns the window it closed, or null.
   */
  const closeSystemDialog = async () => {
    const focus = /mCurrentFocus=Window\{[^}]*\}/.exec(windowFocus())?.[0] ?? "";
    if (!/Application Not Responding|Application Error|isn't responding|has stopped|keeps stopping/i.test(focus)) return null;
    const nodes = await screenDump("system-dialog");
    const pick = (pattern) => nodes.find((n) => pattern.test(n.text) && n.bounds);
    const button = pick(/^wait$/i) ?? pick(/^(ok|close app|close)$/i);
    if (button) tapNode(button);
    else key(KEY_BACK);
    await sleep(1500);
    log(`  (closed a system dialog with ${button ? `"${button.text}"` : "back"}: ${focus})`);
    return focus;
  };

  /**
   * Taps one of the page's buttons with a real touch, as a user does. The back checks need that: Chromium's back
   * button skips every history entry of a page that added one without a user activation, and a click through the
   * DevTools protocol did not count as one in the first emulator runs (back then left the app at once).
   */
  const tapButton = async (label) => {
    await closeSystemDialog();
    const target = await app.page("buttonRect", label);
    const origin = await webViewOrigin();
    const x = origin.left + ((target.rect.left + target.rect.right) / 2) * target.dpr;
    const y = origin.top + ((target.rect.top + target.rect.bottom) / 2) * target.dpr;
    tap(x, y);
  };

  /** For a check that waited in vain: what the screen, the page and the system were doing. */
  const situation = async () => {
    const parts = [`focus: ${focused() ?? "?"}`];
    try {
      parts.push(`page ${await app.page("visibility")}`);
      const progress = await app.page("progress");
      if (progress !== null) parts.push(`${progress}% done`);
    } catch (e) {
      parts.push(`page unreachable (${e.message})`);
    }
    const trouble = systemTrouble(String(adb(["logcat", "-d", "-b", "main,system,events,crash"], { check: false })));
    parts.push(trouble.length ? `system: ${trouble.slice(-4).join(" / ")}` : "nothing wrong in logcat");
    return parts.join("; ");
  };

  /** After a check that may have left the app: brings it back to the front and reconnects to its page. */
  const ensureApp = async () => {
    if (focused() === PACKAGE && app.devtools) {
      try {
        await app.page("title");
        return;
      } catch {
        // The page is gone (the app was closed and started again): connect anew below.
      }
    }
    app.close();
    await app.launch();
  };

  /**
   * Brings the app's page back on screen when something covered it (a lock screen, a window of the system), which
   * also pauses a simulation of the phone app. Returns whether it had to.
   */
  const ensureVisible = async () => {
    await closeSystemDialog();
    if ((await app.page("visibility")) === "visible") return false;
    shell("wm dismiss-keyguard", { check: false });
    shell(`am start -n ${ACTIVITY}`, { check: false });
    await waitFor("the app's page back on screen", 20, async () => (await app.page("visibility")) === "visible");
    return true;
  };

  try {
    const connected = await check("A device is connected", true, async () => {
      adb(["wait-for-device"], { timeout: 300_000 });
      // The screen stays on, other apps' error dialogs don't take the focus, and logcat starts empty (and roomy).
      shell("svc power stayon true", { check: false });
      shell("settings put system screen_off_timeout 1800000", { check: false });
      shell("settings put global hide_error_dialogs 1", { check: false });
      // Android reads hide_error_dialogs only when its configuration changes: a step of the font scale and back makes
      // it count, so that a system app that stops answering is restarted rather than covering the screen with a
      // dialog (in the fifth CI run "System UI isn't responding" took the taps meant for the app).
      shell("settings put system font_scale 1.1", { check: false });
      await sleep(1000);
      shell("settings put system font_scale 1.0", { check: false });
      // The on-screen keyboard even though the emulator has a hardware keyboard.
      shell("settings put secure show_ime_with_hard_keyboard 1", { check: false });
      shell("wm dismiss-keyguard", { check: false });
      // No lock screen at all: Android shows it again whenever the system UI restarts, and the app behind it is hidden.
      shell("locksettings set-disabled true", { check: false });
      adb(["logcat", "-G", "16M"], { check: false });
      adb(["logcat", "-c"], { check: false });
      const prop = (name) => shell(`getprop ${name}`, { check: false });
      Object.assign(device, {
        android: prop("ro.build.version.release"),
        sdk: prop("ro.build.version.sdk"),
        model: prop("ro.product.model"),
        size: /(\d+x\d+)/.exec(shell("wm size", { check: false }))?.[1],
        density: /(\d+)\s*$/.exec(shell("wm density", { check: false }))?.[1],
        webviewPackage: /\(name, version\): \((.+)\)/.exec(shell("dumpsys webviewupdate", { check: false }))?.[1],
      });
      const settled = await waitForCalm();
      return `Android ${device.android} (API ${device.sdk}), ${device.model}, WebView package ${device.webviewPackage ?? "?"}; ${settled}`;
    });
    if (!connected) blocked = "no device";

    const started = await check("Install and start", true, async () => {
      const attempts = await installApk(apk);
      // A first start: no settings or results from before.
      shell(`pm clear ${PACKAGE}`);
      const seconds = await app.launch();
      const info = await app.page("info");
      facts.cores = info.cores;
      device.webview = chromeVersion(info.userAgent) ?? info.userAgent;
      device.ready = `${seconds.toFixed(1)} s after launch`;
      screenshot("start");
      const installed = attempts > 1 ? `installed at attempt ${attempts}; ` : "";
      return `${installed}ready ${seconds.toFixed(1)} s after "am start"; ${info.width}×${info.height} CSS px at ${info.dpr}×; ${info.cores} cores; ${info.userAgent}`;
    });
    if (!started) blocked ??= "the app did not start";

    await check("No errors at startup", true, async () => {
      // Errors that come a moment after the first screen count too.
      await sleep(3000);
      const text = await app.page("text");
      const startError = /CalcBJ could not start[^\n]*/i.exec(text)?.[0];
      if (startError) throw new Error(startError);
      const logcat = logcatProblems(String(adb(["logcat", "-d", "-v", "threadtime"])));
      // Everything so far is from the start: the page's console was replayed when the DevTools connection opened.
      facts.startupErrors = app.errors.length;
      facts.startupConsoleErrors = logcat.consoleErrors.length;
      if (logcat.crashes.length) throw new Error(`crash: ${logcat.crashes.join(" | ")}`);
      const errors = [...new Set([...app.errors.map((e) => e.text), ...logcat.consoleErrors])];
      if (errors.length) throw new Error(`${errors.length} error(s): ${errors.slice(0, 5).join(" | ")}`);
      return "no uncaught errors and no console errors (DevTools protocol and logcat)";
    });

    await check("Engine works: Game rules shows a house edge", true, async () => {
      const title = await app.page("title");
      if (!title.endsWith("Game rules")) throw new Error(`the first page is "${title}", not Game rules`);
      const text = await app.page("text");
      const edge = /([+−-]?\d+\.\d+%)\s+(House|Player) edge/i.exec(text);
      if (!edge) throw new Error(`no house edge on the page: ${text.slice(0, 300).replace(/\s+/g, " ")}`);
      return `${edge[2]} edge ${edge[1]} for the default rules`;
    });

    await check("Phone layout: bottom tabs, no keyboard hints", true, async () => {
      const shown = await app.page("buttons");
      const missing = [...TABS.map(([tab]) => tab), "More"].filter((tab) => !shown.includes(tab));
      if (missing.length) throw new Error(`no ${missing.join(", ")} tab; the buttons are: ${shown.join(" | ")}`);
      if (/\bF5\b/.test(await app.page("text"))) throw new Error('"F5" on the Game rules page: the desktop texts are showing');
      return "tabs Rules, Count, Simulate, Results and More; no F5";
    });

    await check("Safe areas clear the system bars (edge to edge)", false, async () => {
      const page = await app.page("insets");
      const bars = systemInsets();
      const css = (frame) => (frame ? (frame.bottom - frame.top) / page.dpr : null);
      const status = css(bars.statusBars);
      const navigation = css(bars.navigationBars);
      const px = (v) => (v === null || v === undefined ? "?" : `${Math.round(v * 10) / 10}px`);
      const details =
        `--safe-top ${px(page.safeTop)} (status bar ${px(status)}), --safe-bottom ${px(page.safeBottom)} ` +
        `(navigation bar ${px(navigation)}); env(): top ${px(page.envTop)}, bottom ${px(page.envBottom)}; ` +
        `bridge ${JSON.stringify(page.bridge)}; title at ${px(page.titleTop)}`;
      if (page.safeTop <= 0) throw new Error(`no top inset, so the top bar is under the status bar: ${details}`);
      if (status !== null && page.safeTop + 1 < status) throw new Error(`the top inset is smaller than the status bar: ${details}`);
      if (status !== null && page.titleTop !== null && page.titleTop + 1 < status) {
        throw new Error(`the title is under the status bar: ${details}`);
      }
      if (navigation !== null && page.safeBottom + 1 < navigation) {
        throw new Error(`the bottom inset is smaller than the navigation bar: ${details}`);
      }
      return details;
    });

    await check("All nine pages open", true, async () => {
      const opened = ["Game rules"];
      screenshot("game-rules");
      const visit = async (title) => {
        await app.waitForTitle(title);
        // Some pages compute for a moment after they appear.
        await sleep(500);
        if (/\bF5\b/.test(await app.page("text"))) throw new Error(`"F5" on the ${title} page`);
        screenshot(slug(title));
        opened.push(title);
      };
      for (const [tab, title] of TABS.slice(1)) {
        app.phase = `page ${title}`;
        await app.page("click", tab);
        await visit(title);
      }
      for (const title of MORE_PAGES) {
        app.phase = `page ${title}`;
        await app.openMore();
        await app.page("click", title);
        await visit(title);
      }
      return opened.join(", ");
    });

    await check("A short simulation (10M rounds) finishes and shows results", true, async () => {
      await setRounds(10e6);
      // GitHub's emulator has 2 cores and draws in software on the same 2 of the runner: with both simulating, the
      // system starved, something stopped answering and covered the app, and the app paused its run (third CI run).
      if (facts.cores <= 2) {
        await app.page("setThreads", 1);
        await waitFor("saved thread setting of 1", 10, async () => (await app.storage()).settings?.threads === 1);
      }
      await ensureVisible();
      await app.page("clickRun");
      await waitFor("running simulation", 20, () => app.page("running"));
      // Should something cover the app anyway, it is brought back, and the paused run goes on.
      let broughtBack = 0;
      let looked = 0;
      const stored = await waitFor("10M simulated rounds in calcbj.sims.v1", 300, async () => {
        const s = await app.storage();
        if (mostRounds(s) >= 10e6) return s;
        // Every 5 s at most: each look is a screen query, and the emulator has little to spare.
        if (Date.now() - looked > 5000) {
          looked = Date.now();
          if (await ensureVisible()) broughtBack++;
        }
        return null;
      }).catch(async (e) => {
        throw new Error(`${e.message}; ${await situation()}`);
      });
      await waitFor("end of the run", 30, async () => !(await app.page("running")));
      const shown = await app.page("results");
      screenshot("simulated");
      if (shown.none || !shown.rounds) throw new Error("the Simulation page shows no results");
      const run = stored.sims.find((d) => d.rounds >= 10e6);
      facts.rounds = run.rounds;
      const memory = /TOTAL PSS:\s+(\d+)/.exec(shell(`dumpsys meminfo ${PACKAGE}`, { check: false }))?.[1];
      const speed = run.seconds > 0 ? ` (${(run.rounds / run.seconds / 1e6).toFixed(1)}M rounds/s)` : "";
      const covered = broughtBack ? `; the app was covered and brought back ${broughtBack} time(s)` : "";
      return (
        `${roundsText(run.rounds)} rounds in ${run.seconds?.toFixed(1)} s on ${run.threads} thread(s)${speed}; ` +
        `the page shows ${shown.rounds} rounds simulated${memory ? `; the app uses ${Math.round(memory / 1024)} MB (PSS)` : ""}` +
        covered
      );
    });

    // The back checks open the sheet and the pages with real taps (see tapButton).
    await check("Back closes the More sheet", true, async () => {
      await ensureApp();
      await ensureVisible();
      const title = await app.page("title");
      await tapButton("More");
      await waitFor("More sheet", 10, () => app.page("moreOpen")).catch(async (e) => {
        throw new Error(`${e.message} after a tap on More; ${await situation()}`);
      });
      const before = await app.page("history");
      screenshot("more-sheet");
      key(KEY_BACK);
      await sleep(1500);
      if (focused() !== PACKAGE) throw new Error(`the app left the screen (${focused()} has the focus); history before: ${before}`);
      await waitFor("closed More sheet", 10, async () => !(await app.page("moreOpen")));
      const now = await app.page("title");
      if (now !== title) throw new Error(`the page changed from "${title}" to "${now}"`);
      return `the sheet closed and "${title}" stayed (history before: ${before})`;
    });

    await check("Back from another page returns to Game rules", true, async () => {
      await ensureApp();
      await ensureVisible();
      // The page's entry comes from a real tap: to Game rules first if need be, then a tap on Simulate.
      if (!(await app.page("title")).endsWith("Game rules")) {
        await tapButton("Rules");
        await app.waitForTitle("Game rules");
      }
      await tapButton("Simulate");
      await app.waitForTitle("Simulation");
      const from = await app.page("title");
      const before = await app.page("history");
      key(KEY_BACK);
      await sleep(1500);
      if (focused() !== PACKAGE) throw new Error(`the app left the screen (${focused()} has the focus); history before: ${before}`);
      await app.waitForTitle("Game rules", 10);
      return `"${from}" → Game rules (history before: ${before})`;
    });

    await check("Back on Game rules leaves the app", true, async () => {
      await ensureApp();
      await ensureVisible();
      if (!(await app.page("title")).endsWith("Game rules")) {
        await tapButton("Rules");
        await app.waitForTitle("Game rules");
      }
      key(KEY_BACK);
      const other = await waitForOtherApp("other app on screen");
      screenshot("left-the-app");
      return `${other} took the screen`;
    });

    await check("Settings and results survive force-stop and restart", true, async () => {
      if (!facts.rounds) throw new Error("nothing to compare: the simulation check did not pass");
      // The results were saved when the run ended, seconds ago: the WebView writes localStorage within about a second.
      await restart("restart");
      const stored = await app.storage();
      if (stored.settings?.simRounds !== 10e6) {
        throw new Error(`the rounds setting came back as ${JSON.stringify(stored.settings?.simRounds)}, not 10M`);
      }
      const rounds = mostRounds(stored);
      if (rounds < facts.rounds) throw new Error(`calcbj.sims.v1 holds ${rounds} rounds, not the ${facts.rounds} simulated`);
      await app.open("Simulate", "Simulation");
      const shown = await app.page("results");
      screenshot("after-restart");
      if (shown.none || !shown.rounds) throw new Error("the Simulation page shows no results after the restart");
      return `rounds setting 10M and ${roundsText(rounds)} simulated rounds kept; the page shows ${shown.rounds}`;
    });

    await check("A run interrupted by Home and force-stop keeps a checkpoint", true, async () => {
      // Far more than the emulator can do before the home key.
      await setRounds(2e9);
      await ensureVisible();
      const before = mostRounds(await app.storage());
      await app.page("clickRun");
      await waitFor("running simulation", 20, () => app.page("running"));
      // A few chunks of 500,000 rounds (0.1% of 2 billion is 2 million); after a minute it carries on regardless.
      const progress = await waitFor("progress of 0.1%", 60, async () => {
        const p = await app.page("progress");
        return p !== null && p >= 0.1 ? p : null;
      }).catch(() => null);
      screenshot("running");
      key(KEY_HOME);
      // The app keeps a checkpoint when it is hidden; the WebView writes it to disk a moment later.
      await sleep(5000);
      screenshot("home");
      await restart("restart after the interrupted run");
      let rounds = 0;
      await waitFor("more rounds in calcbj.sims.v1 than before the run", 10, async () => {
        rounds = mostRounds(await app.storage());
        return rounds > before;
      }).catch(() => {
        throw new Error(
          `calcbj.sims.v1 still holds ${roundsText(rounds)} rounds: the run (${progress ?? "?"}% done when Home was ` +
            "pressed) was not kept",
        );
      });
      screenshot("after-interrupted-run");
      // The checks that follow want an idle app.
      if (await app.page("running")) await app.page("stop");
      return `${roundsText(rounds - before)} rounds of the interrupted run kept (it showed ${progress ?? "?"}% when Home was pressed)`;
    });

    await check("The keyboard leaves a text field near the bottom visible", false, async () => {
      await ensureApp();
      await ensureVisible();
      await app.open("Results", "Bet ramp & results");
      const target = await app.page("keyboardTarget");
      await sleep(500);
      // Where the WebView is on the screen, to turn CSS pixels into screen pixels for the tap.
      const view = (await screenDump("webview")).find((n) => n.className === "android.webkit.WebView" && n.bounds)?.bounds;
      const left = view?.left ?? 0;
      const top = view?.top ?? 0;
      const dpr = target.dpr;
      tap(left + ((target.rect.left + target.rect.right) / 2) * dpr, top + ((target.rect.top + target.rect.bottom) / 2) * dpr);
      try {
        await waitFor("keyboard on screen", 10, () => systemInsets().ime ?? null).catch((e) => {
          const keyboards = shell("ime list -s", { check: false }).split(/\s+/).filter(Boolean);
          throw new Error(`${e.message}; keyboards: ${keyboards.join(", ") || "none"}; focus: ${focused() ?? "?"}`);
        });
        // The page and the WebView make room for the keyboard.
        await sleep(1500);
        screenshot("keyboard");
        const field = await app.page("keyboardField");
        const ime = systemInsets().ime;
        key(KEY_BACK); // closes the keyboard: it takes this press, not the app
        await sleep(1000);
        if (!field?.focused) throw new Error("the tap did not focus the field");
        if (!ime) throw new Error("the keyboard went away by itself");
        const fieldTop = top + field.rect.top * dpr;
        const fieldBottom = top + field.rect.bottom * dpr;
        const where = `field at ${Math.round(fieldTop)}–${Math.round(fieldBottom)} px, keyboard from ${ime.top} px (screen pixels)`;
        if (fieldBottom > ime.top || fieldTop < 0) throw new Error(`the keyboard hides the field: ${where}`);
        if (field.coveredBy) throw new Error(`the field is under ${field.coveredBy}: ${where}`);
        return where;
      } finally {
        await app.page("blur").catch(() => {});
      }
    });

    await check("Save scenario writes scenario.bjev to Downloads", false, async () => {
      try {
        await ensureApp();
        await setRounds(10e6);
        await ensureVisible();
        await openFileScreen("Save scenario…", "system Save screen");
        screenshot("save-picker");
        await pickerShow("Downloads");
        const nodes = await screenDump("save-in-downloads");
        const name = nodes.find((n) => n.className.endsWith("EditText"))?.text ?? "?";
        const save = nodes.find((n) => /^save$/i.test(n.text) && n.enabled && n.bounds);
        if (!save) throw new Error(`no Save button; the screen shows: ${nodes.map((n) => n.text).filter(Boolean).join(" | ")}`);
        tapNode(save);
        await sleep(1500);
        // With a file of that name there already, Android asks first.
        if (focused() !== PACKAGE) {
          const replace = (await screenDump("save-confirm")).find((n) => /^(ok|overwrite|replace)$/i.test(n.text) && n.bounds);
          if (replace) tapNode(replace);
        }
        await waitFor("CalcBJ back on screen", 15, () => focused() === PACKAGE);
        // The notice starts with a capital; "saved" in other sentences does not count.
        const notice = await waitForNotice(/(Saved |Could not save)[^\n]*/, '"Saved" message');
        if (notice.startsWith("Could not")) throw new Error(notice);
        const files = shell("ls -1 /sdcard/Download", { check: false }).split(/\r?\n/).map((f) => f.trim());
        const file = files.find((f) => /^scenario.*\.bjev$/.test(f)) ?? files.find((f) => f.startsWith("scenario"));
        if (!file) throw new Error(`no scenario file in /sdcard/Download (${files.join(", ") || "empty"})`);
        const text = shell(`cat '/sdcard/Download/${file}'`);
        if (!text.includes("<ScenarioFile")) throw new Error(`${file} is not a scenario: ${text.slice(0, 120)}`);
        writeFileSync(join(out, file), text);
        facts.savedFile = file;
        facts.savedRounds = Number(/<SimRounds>(\d+)<\/SimRounds>/.exec(text)?.[1]);
        screenshot("saved");
        return `${notice} /sdcard/Download/${file}, ${text.length} characters (the Save screen offered "${name}")`;
      } finally {
        await backToApp();
      }
    });

    await check("Open scenario reads it back", false, async () => {
      if (!facts.savedFile) throw new Error("no saved scenario to open (the save check did not pass)");
      try {
        // Another value first, so that opening the file visibly brings its own back.
        await ensureApp();
        await setRounds(25e6);
        await ensureVisible();
        await openFileScreen("Open scenario…", "system file picker");
        screenshot("open-picker");
        await pickerShow("Downloads");
        const item = await waitFor(`${facts.savedFile} in the picker`, 10, async () =>
          (await screenDump("open-in-downloads")).find((n) => n.text === facts.savedFile && n.bounds),
        );
        if (!item.enabled) throw new Error(`${facts.savedFile} is greyed out in the picker: its file type is not accepted`);
        tapNode(item);
        await waitFor("CalcBJ back on screen", 15, () => focused() === PACKAGE).catch(() => {
          throw new Error(`tapping ${facts.savedFile} did not choose it (greyed out?); the picker is still open`);
        });
        const notice = await waitForNotice(/(Opened |Could not open)[^\n]*/, '"Opened" message');
        if (notice.startsWith("Could not")) throw new Error(notice);
        const rounds = Number(await app.page("roundsValue"));
        screenshot("opened");
        if (rounds !== facts.savedRounds) {
          throw new Error(`${notice} But the rounds setting is ${rounds}, not the file's ${facts.savedRounds}.`);
        }
        return `${notice} The rounds setting went back from 25M to the file's ${roundsText(rounds)}.`;
      } finally {
        await backToApp();
      }
    });
  } finally {
    // Whatever happened above (adb itself may be what failed): the crash check, logcat and the report.
    blocked = null;
    let logcatText = "";
    let frozen = false;
    try {
      logcatText = String(adb(["logcat", "-d", "-v", "threadtime"], { check: false }));
      writeFileSync(join(out, "logcat.txt"), logcatText);
      frozen = notRespondingApp(windowFocus()) === PACKAGE;
      // Into the job's log too, so that a stalled check can be explained without the artifact.
      const events = String(adb(["logcat", "-d", "-b", "events"], { check: false }));
      const trouble = systemTrouble(`${logcatText}\n${events}`);
      if (trouble.length) log(`\nProblems outside the app in logcat:\n${trouble.slice(-30).map((l) => `  ${l}`).join("\n")}`);
    } catch (e) {
      log(`(no logcat: ${e.message})`);
    }
    const logcat = logcatProblems(logcatText);

    await check("No crash, panic or freeze of the app", true, async () => {
      if (!logcatText) throw new Error("logcat could not be read");
      const problems = [...logcat.crashes, ...(frozen ? ["an Application Not Responding dialog is on screen"] : [])];
      if (problems.length) throw new Error(problems.slice(0, 5).join(" | "));
      return "none in logcat";
    });

    const laterErrors = [
      ...new Set([
        ...app.errors.slice(facts.startupErrors ?? 0).map((e) => `${e.phase}: ${e.text}`),
        ...logcat.consoleErrors.slice(facts.startupConsoleErrors ?? 0).map((e) => `logcat: ${e}`),
      ]),
    ];
    await check("No errors in the page after startup", false, async () => {
      if (laterErrors.length) throw new Error(`${laterErrors.length} error(s): ${laterErrors.slice(0, 3).join(" | ")}`);
      return "none";
    });

    app.close();
    try {
      adb(["forward", "--remove", `tcp:${DEVTOOLS_PORT}`], { check: false });
    } catch {
      // Nothing to remove when adb is not there.
    }
    const report = { device, checks, errors: app.errors, laterErrors };
    writeFileSync(join(out, "results.json"), JSON.stringify(report, null, 2));
    const markdown = summaryMarkdown(report);
    writeFileSync(join(out, "summary.md"), markdown);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, markdown);
    log(`\n${markdown}`);
  }
  return checks.some((c) => c.required && c.status !== "pass") ? 1 : 0;
}

if (import.meta.main) {
  try {
    process.exitCode = await main();
  } catch (e) {
    console.error(`android-smoke: ${e.message}`);
    process.exitCode = 1;
  }
}
