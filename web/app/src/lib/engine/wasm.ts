// One instance of the engine's WebAssembly module and its request protocol (see web/engine-wasm/src/lib.rs).

export const BIN_MIN = -100;
export const NUM_BINS = 201;
/** Doubles per `sim_run` reply: six arrays of NUM_BINS (n, sa, qa, sb, qb, ab), then rounds, shoes, fallbacks. */
export const TALLY_LEN = 6 * NUM_BINS + 3;

interface Exports {
  memory: WebAssembly.Memory;
  alloc(len: number): number;
  dealloc(ptr: number, len: number): void;
  call(ptr: number, len: number): number;
  out_ptr(): number;
  sim_run(rounds: number): number;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export class EngineInstance {
  /** Set when the module trapped; it must then be replaced by a new instance. */
  broken = false;
  private readonly ex: Exports;

  private constructor(instance: WebAssembly.Instance) {
    this.ex = instance.exports as unknown as Exports;
  }

  static async create(module: WebAssembly.Module): Promise<EngineInstance> {
    return new EngineInstance(await WebAssembly.instantiate(module, {}));
  }

  /** Sends a JSON request and returns the reply's value; throws with the engine's message on errors. */
  call<T>(request: object): T {
    const ex = this.ex;
    const bytes = encoder.encode(JSON.stringify(request));
    const ptr = ex.alloc(bytes.length) >>> 0;
    // No memory left for the request (a phone short of memory): writing it to address 0 would corrupt the engine.
    if (ptr === 0) throw new Error("The engine ran out of memory.");
    new Uint8Array(ex.memory.buffer, ptr, bytes.length).set(bytes);
    let len: number;
    try {
      len = ex.call(ptr, bytes.length) >>> 0;
    } catch (e) {
      this.broken = true;
      throw e;
    }
    ex.dealloc(ptr, bytes.length);
    const reply = JSON.parse(decoder.decode(new Uint8Array(ex.memory.buffer, ex.out_ptr() >>> 0, len)));
    if (reply.error !== undefined) throw new Error(reply.error);
    return reply.ok as T;
  }

  /** Plays rounds on the worker set up by a "simPrepare" request; returns a copy of its tallies. */
  simRun(rounds: number): Float64Array {
    let ptr: number;
    try {
      ptr = this.ex.sim_run(rounds) >>> 0;
    } catch (e) {
      this.broken = true;
      throw e;
    }
    if (ptr === 0) throw new Error("The simulation was not prepared.");
    return new Float64Array(this.ex.memory.buffer, ptr, TALLY_LEN).slice();
  }
}
