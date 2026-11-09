//! Numeric helpers that behave exactly like .NET Framework, so results and texts match the C# engine.

/// `Math.Max(double, double)` of .NET Framework: when the values compare equal it returns `b`, and a NaN in `a` wins.
#[inline]
pub(crate) fn dmax(a: f64, b: f64) -> f64 {
    if a > b || a.is_nan() { a } else { b }
}

/// `Math.Min(double, double)` of .NET Framework.
#[inline]
pub(crate) fn dmin(a: f64, b: f64) -> f64 {
    if a < b || a.is_nan() { a } else { b }
}

/// `(int)Math.Round(x)`: .NET rounds halves to even by default.
#[inline]
pub(crate) fn round_even_i32(x: f64) -> i32 {
    x.round_ties_even() as i32
}

pub(crate) fn dotnet_bool(b: bool) -> &'static str {
    if b { "True" } else { "False" }
}

/// The value rounded to 15 significant digits, as .NET Framework does before formatting a double:
/// (negative, digits without trailing zeros, scale) where value = 0.d1d2d3… × 10^scale. .NET rounds a 16th digit of 5
/// up, also when nothing follows it (0.5000152587890625 gives …063); Rust's own formatter would round such an exact tie
/// to the even digit. 41 digits tell a tie from a value next to it.
fn digits15(v: f64) -> (bool, Vec<u8>, i32) {
    if v == 0.0 {
        return (false, Vec::new(), 0);
    }
    let s = format!("{:.40e}", v.abs());
    let (mantissa, exp) = s.split_once('e').expect("exponent");
    let mut exp: i32 = exp.parse().expect("exponent value");
    let all: Vec<u8> = mantissa.bytes().filter(u8::is_ascii_digit).map(|b| b - b'0').collect();
    let mut digits = all[..15].to_vec();
    if all[15] >= 5 {
        let mut i = 15;
        loop {
            if i == 0 {
                // 999…9 rounds up to 1000…0
                digits.insert(0, 1);
                digits.pop();
                exp += 1;
                break;
            }
            i -= 1;
            if digits[i] == 9 {
                digits[i] = 0;
            } else {
                digits[i] += 1;
                break;
            }
        }
    }
    while digits.last() == Some(&0) {
        digits.pop();
    }
    (v < 0.0, digits, exp + 1)
}

fn push_fixed(s: &mut String, d: &[u8], scale: i32) {
    if scale <= 0 {
        s.push_str("0.");
        for _ in 0..-scale {
            s.push('0');
        }
        s.extend(d.iter().map(|&x| (b'0' + x) as char));
    } else {
        let sc = scale as usize;
        for i in 0..sc {
            s.push((b'0' + d.get(i).copied().unwrap_or(0)) as char);
        }
        if d.len() > sc {
            s.push('.');
            s.extend(d[sc..].iter().map(|&x| (b'0' + x) as char));
        }
    }
}

fn special(v: f64) -> Option<String> {
    if v.is_nan() {
        Some("NaN".to_string())
    } else if v.is_infinite() {
        Some(if v > 0.0 { "Infinity" } else { "-Infinity" }.to_string())
    } else {
        None
    }
}

/// `double.ToString()` of .NET Framework: "G" with 15 significant digits (scientific below 1E-04 and from 1E+15).
pub fn dotnet_general(v: f64) -> String {
    if let Some(s) = special(v) {
        return s;
    }
    let (neg, d, scale) = digits15(v);
    if d.is_empty() {
        return "0".to_string();
    }
    let mut s = String::new();
    if neg {
        s.push('-');
    }
    let exp = scale - 1;
    if exp >= 15 || exp <= -5 {
        s.push((b'0' + d[0]) as char);
        if d.len() > 1 {
            s.push('.');
            s.extend(d[1..].iter().map(|&x| (b'0' + x) as char));
        }
        s.push('E');
        s.push(if exp < 0 { '-' } else { '+' });
        s.push_str(&format!("{:02}", exp.abs()));
    } else {
        push_fixed(&mut s, &d, scale);
    }
    s
}

/// A .NET custom format such as "0.##" (`decimals` = number of optional decimals): rounds half away from zero,
/// drops trailing zeros and prints values that round to zero as "0".
pub fn dotnet_fixed(v: f64, decimals: usize) -> String {
    if let Some(s) = special(v) {
        return s;
    }
    let (neg, mut d, mut scale) = digits15(v);
    let keep = scale + decimals as i32;
    if keep < 0 {
        return "0".to_string();
    }
    let keep = keep as usize;
    if keep < d.len() {
        let up = d[keep] >= 5;
        d.truncate(keep);
        if up {
            let mut i = d.len();
            loop {
                if i == 0 {
                    d.insert(0, 1);
                    scale += 1;
                    break;
                }
                i -= 1;
                if d[i] == 9 {
                    d[i] = 0;
                } else {
                    d[i] += 1;
                    break;
                }
            }
        }
        while d.last() == Some(&0) {
            d.pop();
        }
    }
    if d.is_empty() {
        return "0".to_string();
    }
    let mut s = String::new();
    if neg {
        s.push('-');
    }
    push_fixed(&mut s, &d, scale);
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn general_format() {
        assert_eq!(dotnet_general(1.5), "1.5");
        assert_eq!(dotnet_general(-4.0), "-4");
        assert_eq!(dotnet_general(0.0), "0");
        assert_eq!(dotnet_general(0.1 + 0.2), "0.3");
        assert_eq!(dotnet_general(0.0001), "0.0001");
        assert_eq!(dotnet_general(0.00001), "1E-05");
        assert_eq!(dotnet_general(123456789012345.0), "123456789012345");
        assert_eq!(dotnet_general(1e15), "1E+15");
        assert_eq!(dotnet_general(-2.5e-7), "-2.5E-07");
        // Exact ties at the 16th digit round away from zero, as in .NET.
        assert_eq!(dotnet_general(0.5000152587890625), "0.500015258789063");
        assert_eq!(dotnet_general(3777081272337.625), "3777081272337.63");
        assert_eq!(dotnet_general(999999999999999.9), "1E+15");
    }

    #[test]
    fn fixed_format() {
        assert_eq!(dotnet_fixed(75.0, 1), "75");
        assert_eq!(dotnet_fixed(62.5, 1), "62.5");
        assert_eq!(dotnet_fixed(12.25, 1), "12.3");
        assert_eq!(dotnet_fixed(9.96, 1), "10");
        assert_eq!(dotnet_fixed(0.005, 2), "0.01");
        assert_eq!(dotnet_fixed(0.004, 2), "0");
        assert_eq!(dotnet_fixed(-0.001, 2), "0");
        assert_eq!(dotnet_fixed(-0.5, 2), "-0.5");
        assert_eq!(dotnet_fixed(0.6, 0), "1");
        assert_eq!(dotnet_fixed(1.25, 2), "1.25");
        assert_eq!(dotnet_fixed(0.83, 4), "0.83");
        assert_eq!(dotnet_fixed(10210382710427.25, 1), "10210382710427.3");
    }
}
