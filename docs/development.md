# Developing CalcBJ

The desktop app for Windows, Mac and Linux, the Android app and the web app are one program: the engine in Rust
compiled to WebAssembly, and the interface in TypeScript and Svelte. The desktop and phone apps wrap it with Tauri, and
GitHub Actions builds the installers and the APK. In a browser it is a web app that can be installed and then works
offline.

* `web\engine`: the engine. See `web\engine\README.md`; how its maths works is in [how-it-works.md](how-it-works.md).
* `web\engine-wasm`: its WebAssembly interface.
* `web\app`: the interface, with all nine pages. Simulations run on every CPU thread in Web Workers. Run it with
  `npm install` and `npm run dev` in `web\app`; see `web\app\README.md`.
* `web\app\src-tauri`: the desktop app (Windows installer, Mac `.dmg`, Linux `.AppImage`, `.deb` and `.rpm`), built by
  `.github\workflows\desktop.yml`, which also starts the Linux app once on a virtual screen as a test, and the phone
  apps (the Android APK; the iPhone app is prepared), built by `.github\workflows\phone.yml`.

CalcBJ started out as a Windows program in C# (.NET Framework) with an engine of its own, which the Rust engine was
translated from line by line and checked against until both gave the same results. That program was retired in
November 2026; its code is in the git history up to commit `d24b7e2`.

## Building and testing

The installers and the Android app are built by GitHub Actions (below). On a PC with Rust (with the
`wasm32-unknown-unknown` target) and Node.js:

```
cd web\engine
cargo test                                            # the engine's checks, about 40 seconds
cargo test --test offtop -- --ignored --nocapture     # the long simulations, about 2 minutes
cd ..\app
npm install
npm test                                              # the app's unit tests, a few seconds
npm run dev                                           # the app in the browser at http://localhost:5173
```

## Releasing

1. Raise the version in `web/app/src-tauri/tauri.conf.json`, `web/app/src-tauri/Cargo.toml`, the `calcbj-desktop`
   entry of `web/app/src-tauri/Cargo.lock`, and with `npm version <version> --no-git-tag-version` in `web/app`. The
   same version again replaces the files of its release.
2. Commit to `main` and push. "Desktop apps" (`desktop.yml`) runs the app's unit tests, then builds the Mac, Windows
   and Linux installers and publishes the release `v<version>` (failing tests stop it before anything is built); about
   25 minutes later "Phone apps" (`phone.yml`) has built, tested and attached the signed APK.
3. To try a branch first without releasing anything: `gh workflow run desktop.yml --ref <branch> -f release=false`
   keeps the installers with the run for a week.

When a change alters what a simulation of the same settings returns, raise `RESULTS_VERSION` in `web/engine/src/lib.rs`
(its test fails until then), so the apps show results stored by older versions as outdated.

## Phone apps

The Android app is the same interface and engine as the web and desktop apps, wrapped by Tauri 2 into an app of its
own (the same `web\app\src-tauri` project, with the pages built for Android). It works without internet, opens and
saves the same `.bjev` files, and keeps its settings and results on the phone. An iPhone app is prepared in the same
project but not built yet ([below](#iphone-later)).

`.github\workflows\phone.yml` builds it on GitHub's machines (this PC has no Android SDK):

* on every push to a branch other than `main` that changes `web\`: an APK, tested on an Android emulator by
  `web\app\scripts\android-smoke.mjs` (startup without errors, the engine, all nine pages, a short simulation, the back
  button, settings and results kept across a restart, a checkpoint of an interrupted run, scenario files). The run's
  page shows a summary; screenshots and logcat are in its `android-smoke-test` artifact. Nothing is released.
* after every successful "Desktop apps" run on `main`: the same, and then the signed
  `CalcBJ_<version>_android-arm64.apk` is attached to that version's release, next to the desktop installers.
* by hand: Actions → Phone apps → Run workflow.

How users install and update it is in the [README](../README.md#download). The repository is private, so on a phone
the Releases page needs a GitHub sign-in in the browser first (or the GitHub app). From a PC with Android's platform
tools, `adb install -r CalcBJ_<version>_android-arm64.apk` installs it too.

A higher `version` makes a new release; a new build of the same version replaces the release's APK and installs over
the old one too. Updates install over the installed app only because every APK is signed with the same key (below).

### The signing key (once)

Android installs an update only when it is signed with the same key as the app on the phone, so the workflow signs
every APK with one key kept in the repository's secrets. Without them it still builds, but the APK is not signed (phones
won't install it) and is not attached to the release.

This PC has no Java, but Git for Windows comes with OpenSSL, so everything runs in PowerShell. Git puts only its
`cmd` folder on the PATH, so the first line tells this PowerShell window where `openssl` is. The `pkcs12` command
asks for a password twice, at least 6 characters; it protects the file and the key alike:

```powershell
Set-Alias openssl "C:\Program Files\Git\mingw64\bin\openssl.exe"
New-Item -ItemType Directory -Force "$env:USERPROFILE\calcbj-signing" | Out-Null; cd "$env:USERPROFILE\calcbj-signing"
openssl req -x509 -newkey rsa:2048 -sha256 -days 10000 -nodes -keyout key.pem -out cert.pem -subj "/CN=CalcBJ"
openssl pkcs12 -export -inkey key.pem -in cert.pem -name calcbj -out calcbj-release.p12
Remove-Item key.pem, cert.pem
```

(Without Git's OpenSSL, Docker Desktop does it too, once its engine is running:
`docker run --rm -it -v "$env:USERPROFILE\calcbj-signing:/keys" eclipse-temurin:21-jdk keytool -genkeypair -keystore
/keys/calcbj-release.p12 -storetype PKCS12 -alias calcbj -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=CalcBJ"`.)

Then put it into the repository's secrets, in the same folder (the last command asks for the password):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$PWD\calcbj-release.p12")) | gh secret set ANDROID_KEYSTORE_BASE64 --repo xXBenZeXx/CalcBJ
gh secret set ANDROID_KEY_ALIAS --body calcbj --repo xXBenZeXx/CalcBJ
gh secret set ANDROID_KEY_PASSWORD --repo xXBenZeXx/CalcBJ
```

**Back up `calcbj-release.p12` and its password** outside GitHub (a password manager, a USB stick): secrets cannot be
read back. Without the key no update can ever be installed over the app again: every phone would have to uninstall
CalcBJ, losing its settings and results, and install an app signed with a new key. Google's developer verification
(next section) also ties the package name to this key's certificate.

### Google's developer verification (from 2027)

Google is bringing in a rule that apps on certified Android phones (those with Google Play) come from registered
developers. It started on 30 September 2026 in Brazil, Indonesia, Singapore and Thailand, for apps from Google Play and
a few other stores; it is planned for all apps worldwide in 2027, with no published date for Germany or the EU yet.
Until then nothing changes here, and apps already installed reportedly keep working afterwards. Once it applies, a
self-built APK installs only:

* with `adb install` from a PC (always allowed), or
* after the "advanced flow" on the phone (a switch in Developer options, a one-time 24-hour wait, a warning at each
  install), or
* normally, once the package name `com.calcbj.app` is registered with a free **limited distribution** account in
  Google's Android Developer Console: no fee and no ID document, but a Google account with 2-step verification and a
  payments profile (name and address); up to 50 phones, each authorised once with a QR code. Registering asks for the
  SHA-256 fingerprint of the signing certificate, which the workflow's summary shows ("Signing certificate SHA-256").
  These accounts can only register package names that Android has not seen before, so registering early is the safe
  order.

### iPhone (later)

The iPhone app is prepared but not built yet: `phone.yml` has a job for it that runs only by hand (Run workflow,
"iPhone"), on a Mac runner, and it has not been tested. Without a Mac there are three ways onto an iPhone:

* **TestFlight** (iPhone = testflight): needs the Apple Developer Program (99 USD a year; 98.99 € in Germany in the
  Apple Developer app). Register the bundle ID `com.calcbj.app` and create the app in App Store Connect, create a team
  API key with Admin access (Users and Access → Integrations), and add the secrets `APPLE_API_KEY_ID`,
  `APPLE_API_ISSUER` and `APPLE_API_PRIVATE_KEY` (the text of the `.p8` file, which can be downloaded only once) and
  the variables `APPLE_DEVELOPMENT_TEAM` (the Team ID) and `APP_STORE_APP_ID` (the app's Apple ID number in App Store
  Connect). The run uploads the build, which then appears in the TestFlight app. Testing it yourself needs no App
  Review; each build runs for 90 days. (A public App Store listing would risk rejection: the guidelines bar card
  counters.)
* **Unsigned IPA with Sideloadly** (iPhone = unsigned, free): the run's `ios-unsigned-ipa` artifact, signed with a
  free Apple ID and installed over USB by Sideloadly on Windows (which needs iTunes and iCloud from Apple's website).
  The signature lasts 7 days, so it has to be refreshed every week; at most 3 such apps; Developer Mode must be on
  (Settings → Privacy & Security).
* **The web app on GitHub Pages**: with GitHub Pro, Pages also works from this private repository, at no extra cost.
  Publish `web\app\dist` (built with `npm run build`; there is no Pages workflow yet), open the address in Safari and
  choose Share → Add to Home Screen: it then works offline. The site itself is public (anyone with the address can use
  it; the repository stays private), iOS may clear its storage when the phone runs short of space, and saving `.bjev`
  files from a Home Screen app is unreliable.

### Costs

The repository is private, so GitHub counts Actions time against the account's included usage: $12 a month with
GitHub Free and $18 with GitHub Pro, at $0.006 a minute on Linux and $0.062 on a Mac. An Android run (build and
emulator test) should take roughly 20 to 30 Linux minutes (about $0.15), an iPhone run about 10 Mac minutes (about
$0.60); these are estimates until the first runs. Artifacts count towards the account's storage (500 MB with Free,
1 GB with Pro): the workflow keeps them for 14 days, the emulator's debug APK for 3.
