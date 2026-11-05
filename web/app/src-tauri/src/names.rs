// The file name the iPhone app saves under. A plain function without Tauri, so the tests below run on any computer.

/// The requested name made safe as a file name in the iPhone app's Documents folder: a single name (no folders),
/// without characters the Files app shows wrongly or not at all, and never empty or hidden (a leading dot).
pub fn safe_file_name(requested: &str) -> String {
    let cleaned: String =
        requested.chars().map(|c| if matches!(c, '/' | '\\' | ':') || c.is_control() { '-' } else { c }).collect();
    let cleaned = cleaned.trim_end().trim_start_matches(|c: char| c == '.' || c.is_whitespace());
    if cleaned.is_empty() {
        "scenario.bjev".to_owned()
    } else {
        cleaned.to_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_names_for_documents() {
        assert_eq!(safe_file_name("scenario.bjev"), "scenario.bjev");
        assert_eq!(safe_file_name("6 decks H17 (2).bjev"), "6 decks H17 (2).bjev");
        assert_eq!(safe_file_name("Spielä.bjev"), "Spielä.bjev");
        assert_eq!(safe_file_name("a/b:c\\d.bjev"), "a-b-c-d.bjev");
        assert_eq!(safe_file_name("line\nbreak.bjev"), "line-break.bjev");
        assert_eq!(safe_file_name(".hidden.bjev"), "hidden.bjev");
        assert_eq!(safe_file_name(". .hidden.bjev"), "hidden.bjev");
        assert_eq!(safe_file_name("  spaced.bjev  "), "spaced.bjev");
        assert_eq!(safe_file_name(".."), "scenario.bjev");
        assert_eq!(safe_file_name(""), "scenario.bjev");
    }
}
