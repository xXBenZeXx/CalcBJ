// CalcBJ's main activity for the Android app, used instead of the one "tauri android init" generates. src-tauri/gen
// is not committed: CI runs the init and then copies this file over
// src-tauri/gen/android/app/src/main/java/com/calcbj/app/MainActivity.kt.
//
// It starts from the template in tauri-cli 2.12.4 (templates/mobile/android/app/src/main/MainActivity.kt: edge to
// edge, then TauriActivity's onCreate) and adds what the page cannot do from inside the WebView:
// - system bar icons that suit the page: light ones over the dark tab bar, theme-coloured ones over the top bar;
// - the CalcBJInsets bridge, which tells the page how much of it the system bars and the camera cutout cover;
// - room for the keyboard, so that it does not cover the field being typed in;
// - no stretch effect when the page is scrolled past its ends.
// It uses only the platform and the libraries the template's app/build.gradle.kts brings in (androidx activity, core,
// annotation).

package com.calcbj.app

import android.content.res.Configuration
import android.graphics.Color
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.JavascriptInterface
import android.webkit.WebView
import androidx.activity.SystemBarStyle
import androidx.activity.enableEdgeToEdge
import androidx.annotation.Keep
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import org.json.JSONObject

class MainActivity : TauriActivity() {
  private val insets = InsetsBridge()

  override fun onCreate(savedInstanceState: Bundle?) {
    styleSystemBars()
    super.onCreate(savedInstanceState)
    // Edge to edge, Android no longer makes room for the keyboard: applyInsets does that. "Resize" also keeps Android
    // from panning the whole window up instead, which it may do in the default mode.
    @Suppress("DEPRECATION")
    window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
  }

  // wry's WryActivity.setWebView calls this before it loads the first page and before the WebView is on screen.
  override fun onWebViewCreate(webView: WebView) {
    super.onWebViewCreate(webView)
    // An interface added later would only reach the next page that loads.
    webView.addJavascriptInterface(insets, "CalcBJInsets")
    // The stretch at the ends of the page drags the fixed top bar and tab bar along with it.
    webView.overScrollMode = View.OVER_SCROLL_NEVER
    // The listener goes on the WebView's parent. Chromium takes the WebView's own insets (cutout, keyboard) through a
    // listener on the WebView before Android 12 and through WebView.onApplyWindowInsets after; a listener set on the
    // WebView would replace the one or bypass the other. (Newer WebViews also shrink the page for the part of the
    // keyboard that overlaps them; with the margin below nothing overlaps, so nothing is counted twice.)
    val content = findViewById<View>(android.R.id.content)
    ViewCompat.setOnApplyWindowInsetsListener(content) { _, windowInsets ->
      applyInsets(webView, windowInsets)
      windowInsets
    }
    // The insets known already (null while the window is not on screen yet), so that get() is right from the page's
    // first script on; the listener takes over from the next layout.
    ViewCompat.getRootWindowInsets(window.decorView)?.let { applyInsets(webView, it) }
    ViewCompat.requestApplyInsets(content)
  }

  override fun onConfigurationChanged(newConfig: Configuration) {
    super.onConfigurationChanged(newConfig)
    // The activity is kept when the system switches between light and dark mode ("uiMode" is in the manifest's
    // configChanges), and androidx activity 1.10 does not restyle the bars then, so it is done here.
    styleSystemBars()
    ViewCompat.requestApplyInsets(window.decorView)
  }

  private fun styleSystemBars() {
    enableEdgeToEdge(
      // The page's top bar is white in light mode and dark in dark mode: the status bar icons follow the theme.
      statusBarStyle = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT),
      // The tab bar under the navigation bar is dark in both modes: light icons, and no scrim behind them.
      navigationBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
    )
  }

  private fun isDarkTheme(): Boolean =
    (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES

  private fun applyInsets(webView: WebView, windowInsets: WindowInsetsCompat) {
    val bars = windowInsets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
    val keyboard = windowInsets.getInsets(WindowInsetsCompat.Type.ime()).bottom

    // The keyboard: the WebView ends where the keyboard begins. The page's viewport gets smaller, and Chromium scrolls
    // the focused field into view, as it does in a browser.
    val params = webView.layoutParams as? ViewGroup.MarginLayoutParams
    if (params != null && params.bottomMargin != keyboard) {
      params.bottomMargin = keyboard
      webView.layoutParams = params
    }

    // On a phone in landscape the navigation bar moves to the side, over the page instead of the tab bar: there its
    // icons follow the theme, like the status bar's. (Before Android 8 they are always light.)
    val navigation = windowInsets.getInsets(WindowInsetsCompat.Type.navigationBars())
    val atSide = navigation.left > 0 || navigation.right > 0
    WindowInsetsControllerCompat(window, window.decorView).isAppearanceLightNavigationBars = atSide && !isDarkTheme()

    // How far the bars reach into the WebView, in CSS pixels (in the WebView a CSS pixel is Android's dp). While the
    // keyboard is up the WebView ends above it, so the navigation bar no longer covers any of it.
    val density = resources.displayMetrics.density.toDouble()
    val json = JSONObject()
      .put("top", bars.top / density)
      .put("right", bars.right / density)
      .put("bottom", maxOf(0, bars.bottom - keyboard) / density)
      .put("left", bars.left / density)
      .toString()
    if (json != insets.json) {
      insets.json = json
      webView.evaluateJavascript("window.dispatchEvent(new Event(\"calcbj-insets\"))", null)
    }
  }
}

/**
 * The page's window.CalcBJInsets. get() returns {"top":n,"right":n,"bottom":n,"left":n}: how far the system bars and
 * the display cutout reach into the WebView, in CSS pixels, without the keyboard. The WebView calls get() on a thread
 * of its own, hence @Volatile.
 */
@Keep // release builds shrink the code with R8, and the WebView finds get() only by reflection
class InsetsBridge {
  @Volatile
  var json = "{\"top\":0,\"right\":0,\"bottom\":0,\"left\":0}"

  @JavascriptInterface
  fun get(): String = json
}
