// Runs the CalcBJ engine as WebAssembly in Node: prints single-thread speed and the checksum of 4M rounds per
// benchmark game, which must equal the "checksum" column of the native benchmark (in web\engine:
// cargo run --release --example bench).
// Build first: cargo build --release --target wasm32-unknown-unknown
import { readFile } from "node:fs/promises";

const file = new URL("./target/wasm32-unknown-unknown/release/calcbj_wasm_bench.wasm", import.meta.url);
const { instance } = await WebAssembly.instantiate(await readFile(file), {});
const { prepare, run } = instance.exports;

const games = [
  "6D H17 LS Hi-Lo I18F4, 1 spot",
  "6D H17 LS Hi-Lo I18F4, 3 spots",
  "6D H17 LS Hi-Lo I18F4, 7 spots",
  "6D H17 LS Hi-Lo full, 3 spots",
  "2D H17 KO RC I18F4, 2 spots",
  "1D H17 Hi-Lo full, 1 spot",
];
const rounds = 4_000_000;
for (let g = 0; g < games.length; g++) {
  let t = performance.now();
  prepare(g);
  const strategyMs = performance.now() - t;
  t = performance.now();
  const sum = BigInt.asUintN(64, run(rounds, 99));
  const seconds = (performance.now() - t) / 1000;
  const speed = (rounds / seconds / 1e6).toFixed(2).padStart(6);
  const hex = sum.toString(16).toUpperCase().padStart(16, "0");
  console.log(`${games[g].padEnd(34)} 1 thread ${speed}M/s   checksum ${hex}   (strategy table ${strategyMs.toFixed(0)} ms)`);
}
