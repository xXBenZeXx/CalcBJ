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
  postMessage(message: WorkerReply, transfer?: Transferable[]): void;
};

let module: WebAssembly.Module | null = null;
let engine: EngineInstance | null = null;
let queue: Promise<void> = Promise.resolve();

// Handle messages one at a time: instantiating is asynchronous and must finish before the first request.
scope.onmessage = (e) => {
  queue = queue.then(() => handle(e.data));
};

async function handle(m: WorkerRequest): Promise<void> {
  if (m.type === "init") {
    module = m.module;
    engine = await EngineInstance.create(module);
    return;
  }
  try {
    if (!engine) throw new Error("The engine could not be loaded.");
    if (m.type === "prepare") {
      const info = engine.call<{ basicEv: number }>({ op: "simPrepare", ...m.request });
      scope.postMessage({ type: "prepared", job: m.job, basicEv: info.basicEv });
    } else {
      const tally = engine.simRun(m.rounds);
      scope.postMessage({ type: "tally", job: m.job, rounds: m.rounds, tally }, [tally.buffer]);
    }
  } catch (err) {
    scope.postMessage({ type: "error", job: m.job, message: err instanceof Error ? err.message : String(err) });
    if (engine?.broken && module) engine = await EngineInstance.create(module);
  }
}
