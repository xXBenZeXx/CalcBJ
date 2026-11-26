// CalcBJ as a desktop app for Windows, Mac and Linux. The app itself is in the library (src/lib.rs), which the Android
// and iPhone apps load too.

// No console window next to the app on Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    #[cfg(target_os = "linux")]
    software_drawing_without_gles();
    calcbj_lib::run();
}

/// WebKitGTK draws with OpenGL ES where it can, and its graphics loader stops the whole app when the system has no
/// libGLESv2 (some minimal installs, and WSL without the libgles2 package; the AppImage leaves graphics libraries to the
/// system). Without it, WebKit draws in software, which is fast enough for CalcBJ's pages. A value of
/// WEBKIT_DISABLE_COMPOSITING_MODE that the user set is left alone.
#[cfg(target_os = "linux")]
fn software_drawing_without_gles() {
    const SOFTWARE: &str = "WEBKIT_DISABLE_COMPOSITING_MODE";
    if std::env::var_os(SOFTWARE).is_some() {
        return;
    }
    // SAFETY: a valid C string, and the handle is closed again at once.
    let gles = unsafe { libc::dlopen(c"libGLESv2.so.2".as_ptr(), libc::RTLD_LAZY | libc::RTLD_LOCAL) };
    if gles.is_null() {
        // No other thread runs yet.
        std::env::set_var(SOFTWARE, "1");
    } else {
        unsafe { libc::dlclose(gles) };
    }
}
