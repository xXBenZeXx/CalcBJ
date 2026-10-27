/// <reference types="svelte" />
/// <reference types="vite/client" />

/** True in the desktop app's build (`vite build --mode desktop`, see src-tauri), false in the web app's. */
declare const __DESKTOP__: boolean;
