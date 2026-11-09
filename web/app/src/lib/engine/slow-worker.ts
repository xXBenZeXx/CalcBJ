// Engine requests that take too long for the main thread (see engine.ts). The worker answers them one after the
// other, each as soon as it is worked out, and is ended by the main thread after the last.
import { EngineInstance } from "./wasm";

export interface SlowRequest {
  module: WebAssembly.Module;
  /** The engine requests as JSON text. */
  requests: string[];
}

export type SlowReply = { index: number; value: unknown } | { done: true } | { error: string };

const scope = self as unknown as {
  onmessage: ((e: MessageEvent<SlowRequest>) => void) | null;
  onmessageerror: ((e: MessageEvent) => void) | null;
  postMessage(message: SlowReply): void;
};

scope.onmessage = async (e) => {
  try {
    const engine = await EngineInstance.create(e.data.module);
    e.data.requests.forEach((request, index) => scope.postMessage({ index, value: engine.call(JSON.parse(request)) }));
    scope.postMessage({ done: true });
  } catch (err) {
    scope.postMessage({ error: err instanceof Error ? err.message : String(err) });
  }
};

scope.onmessageerror = () => scope.postMessage({ error: "The engine could not be passed to the worker." });
