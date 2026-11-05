// CalcBJ as a desktop app for Windows and Mac. The app itself is in the library (src/lib.rs), which the Android and
// iPhone apps load too.

// No console window next to the app on Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    calcbj_lib::run();
}
