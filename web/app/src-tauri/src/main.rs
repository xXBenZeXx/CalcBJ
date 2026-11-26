// CalcBJ as a desktop app for Windows, Mac and Linux. The app itself is in the library (src/lib.rs), which the Android
// and iPhone apps load too.

// No console window next to the app on Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // Linux: WebKitGTK's DMA-BUF renderer (its default since 2.42) draws through OpenGL ES in the app's own process.
    // Where the system has no libGLESv2 (WSL's Ubuntu, some minimal installs) its graphics loader stops the app at
    // once, and with some graphics drivers the window stays white. CalcBJ's pages don't need it: WebKit's other way of
    // drawing works everywhere. A value the user set is left alone.
    #[cfg(target_os = "linux")]
    if std::env::var_os("WEBKIT_DISABLE_DMABUF_RENDERER").is_none() {
        // No other thread runs yet.
        std::env::set_var("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
    }
    calcbj_lib::run();
}
