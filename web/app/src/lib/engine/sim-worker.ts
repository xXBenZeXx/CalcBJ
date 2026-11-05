// Simulation worker: its own engine instance dealing its own shoe. Driven by simulation.svelte.ts.
import { EngineInstance } from "./wasm";

export type WorkerRequest =
  | { type: "init"; module: WebAssembly.Module }
  | { type: "prepare"; job: number; request: object }
  | { type: "run"; job: number; rounds: number };

export type WorkerReply =
  | { type: "prepared"; job: number; basicEv: number }
  | { type: "tally"; job: number; rounds: number; tally: Float64Array }
  | { type: "error"; job: number; message: string };

const scope = self as unknown as {
  onmessage: ((e: MessageEvent<WorkerRequest>) => void) | null;
  onmessageerror: ((e: MessageEvent) => void) | null;
  postMessage(message: WorkerReply, transfer?: Transferable[]): void;
};

let module: WebAssembly.Module | null = null;
let engine: EngineInstance | null = null;
/** Why the engine could not be loaded. "init" has no job to answer, so the next request gets this as its error. */
let loadError = "";
let queue: Promise<void> = Promise.resolve();

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// Handle messages one at a time: instantiating is asynchronous and must finish before the first request. handle()
// answers every failure itself; the catch only keeps one unexpected failure from blocking every later message.
scope.onmessage = (e) => {
  const m = e.data;
  queue = queue.then(() => handle(m)).catch((err) => {
    if (m.type !== "init") scope.postMessage({ type: "error", job: m.job, message: message(err) });
  });
};

// The compiled engine could not be passed to this worker (the request messages are plain data).
scope.onmessageerror = () => {
  loadError = "the engine could not be passed to the simulation worker";
};

/** A new engine instance, or null with the reason in `loadError` (for example when the phone is short of memory). */
async function load(m: WebAssembly.Module): Promise<EngineInstance | null> {
  try {
    loadError = "";
    return await EngineInstance.create(m);
  } catch (err) {
    loadError = message(err);
    return null;
  }
}

async function handle(m: WorkerRequest): Promise<void> {
  if (m.type === "init") {
    module = m.module;
    engine = await load(module);
    return;
  }
  try {
    if (!engine) throw new Error("The engine could not be loaded" + (loadError ? ": " + loadError : "."));
    if (m.type === "prepare") {
      const info = engine.call<{ basicEv: number }>({ op: "simPrepare", ...m.request });
      scope.postMessage({ type: "prepared", job: m.job, basicEv: info.basicEv });
    } else {
      const tally = engine.simRun(m.rounds);
      scope.postMessage({ type: "tally", job: m.job, rounds: m.rounds, tally }, [tally.buffer]);
    }
  } catch (err) {
    scope.postMessage({ type: "error", job: m.job, message: message(err) });
    // A trap leaves the instance unusable: the next request gets a fresh one.
    if (engine?.broken && module) engine = await load(module);
  }
}
