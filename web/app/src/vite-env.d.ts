/// <reference types="svelte" />
/// <reference types="vite/client" />

/** True in the desktop app's build (`vite build --mode desktop`, see src-tauri), false in the web app's. */
declare const __DESKTOP__: boolean;
/** True in the phone apps' builds (`vite build --mode android` or `--mode ios`, see src-tauri), false otherwise. */
declare const __MOBILE__: boolean;
/** True in the Android app's build (`vite build --mode android`). */
declare const __ANDROID__: boolean;
/** True in the iPhone and iPad app's build (`vite build --mode ios`). */
declare const __IOS__: boolean;
