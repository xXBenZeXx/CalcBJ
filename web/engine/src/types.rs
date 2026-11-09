//! Rules, count systems and strategy settings (C#: Types.cs).

use crate::num::{dmax, dmin, dotnet_bool, dotnet_fixed, dotnet_general};

macro_rules! named_enum {
    ($(#[$meta:meta])* $name:ident { $($variant:ident = $text:literal),+ $(,)? }) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
        pub enum $name { $($variant),+ }

        impl $name {
            /// Every value, in declaration order.
            pub const ALL: &'static [$name] = &[$($name::$variant),+];

            /// The name used in keys and scenario files (the C# enum name).
            pub fn name(self) -> &'static str {
                match self { $($name::$variant => $text),+ }
            }

            pub fn from_name(text: &str) -> Option<$name> {
                match text { $($text => Some($name::$variant),)+ _ => None }
            }
        }

        /// Serialised as its name, e.g. "USPeek".
        #[cfg(feature = "serde")]
        impl serde::Serialize for $name {
            fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
                s.serialize_str(self.name())
            }
        }

        #[cfg(feature = "serde")]
        impl<'de> serde::Deserialize<'de> for $name {
            fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
                let text = <String as serde::Deserialize>::deserialize(d)?;
                $name::from_name(&text)
                    .ok_or_else(|| serde::de::Error::custom(format!("unknown {} \"{}\"", stringify!($name), text)))
            }
        }
    };
}

named_enum! {
    /// US peek, European no-hole-card (a dealer blackjack takes doubles and splits too), or no-hole-card where only the
    /// original bet is lost to a dealer blackjack.
    HoleCardRule { UsPeek = "USPeek", Enhc = "ENHC", EnhcObo = "ENHC_OBO" }
}
named_enum! { DoubleRule { AnyTwo = "AnyTwo", NineToEleven = "NineToEleven", TenToEleven = "TenToEleven" } }
named_enum! { SurrenderRule { None = "None", Late = "Late", Early = "Early" } }
named_enum! { DeckEstimation { Exact = "Exact", QuarterDeck = "QuarterDeck", HalfDeck = "HalfDeck", FullDeck = "FullDeck" } }
named_enum! { TcRounding { Floor = "Floor", Truncate = "Truncate", Round = "Round" } }
named_enum! { CountMetric { TrueCount = "TrueCount", RunningCount = "RunningCount" } }
named_enum! { IndexMode { BasicOnly = "BasicOnly", Illustrious18 = "Illustrious18", I18Fab4 = "I18Fab4", FullIndices = "FullIndices" } }
named_enum! { HandKind { Hard = "Hard", Soft = "Soft", Pair = "Pair", Insurance = "Insurance" } }
named_enum! {
    PlayKind {
        Stand = "Stand", Hit = "Hit", Double = "Double", NoDouble = "NoDouble", Split = "Split", NoSplit = "NoSplit",
        Surrender = "Surrender", NoSurrender = "NoSurrender", Insure = "Insure", NoInsure = "NoInsure",
    }
}

/// Most hands a pair can be split to, in the analysis and in the simulator.
pub const MAX_SPLIT_LIMIT: i32 = 4;

/// Table rules. Ranks are 1..=10 everywhere in the engine (1 = ace, 10 = any ten-value card).
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct Rules {
    pub decks: i32,
    pub h17: bool,
    pub hole_card: HoleCardRule,
    pub blackjack_pays: f64,
    pub double_on: DoubleRule,
    pub das: bool,
    pub max_split_hands: i32,
    pub resplit_aces: bool,
    pub hit_split_aces: bool,
    pub surrender: SurrenderRule,
    pub insurance: bool,
    /// Fraction of the shoe dealt before the shuffle (cut card position).
    pub penetration: f64,
    pub burn_card: bool,
}

impl Default for Rules {
    fn default() -> Self {
        Rules {
            decks: 6,
            h17: true,
            hole_card: HoleCardRule::UsPeek,
            blackjack_pays: 1.5,
            double_on: DoubleRule::AnyTwo,
            das: true,
            max_split_hands: 4,
            resplit_aces: false,
            hit_split_aces: false,
            surrender: SurrenderRule::Late,
            insurance: true,
            penetration: 0.75,
            burn_card: true,
        }
    }
}

impl Rules {
    pub fn can_double_total(&self, hard: i32, soft: bool) -> bool {
        match self.double_on {
            DoubleRule::NineToEleven => !soft && (9..=11).contains(&hard),
            DoubleRule::TenToEleven => !soft && (10..=11).contains(&hard),
            DoubleRule::AnyTwo => true,
        }
    }

    /// True for early surrender: half the bet is kept even when the dealer has blackjack. Late surrender in a
    /// no-hole-card game loses the whole bet if the dealer later turns out to have blackjack.
    pub fn surrender_before_peek(&self) -> bool {
        self.surrender == SurrenderRule::Early
    }

    /// European no-hole-card where a dealer blackjack takes doubles and splits too.
    pub fn dealer_bj_takes_all(&self) -> bool {
        self.hole_card == HoleCardRule::Enhc
    }

    pub fn total_cards(&self) -> i32 {
        self.decks * 52
    }

    pub fn cut_card(&self, spots: i32) -> i32 {
        let n = self.total_cards();
        if self.penetration <= 0.0 {
            return 1; // reshuffle after every round
        }
        let mut cut = (n as f64 * self.penetration).round_ties_even() as i32;
        let reserve = (n / 3).min(8 + spots * 6);
        if cut > n - reserve {
            cut = n - reserve;
        }
        if cut < 10 {
            cut = 10.min(n - 1);
        }
        cut
    }

    /// Brings values read from a file into the ranges the program works with: 1 to 8 decks, split to at most 4 hands,
    /// a blackjack payout in twentieths of a unit (what the simulator pays in) and a penetration from 0 to 1 (C#:
    /// Rules.Sanitize).
    pub fn sanitize(&mut self) {
        self.decks = self.decks.clamp(1, 8);
        self.max_split_hands = self.max_split_hands.clamp(1, MAX_SPLIT_LIMIT);
        if self.blackjack_pays.is_nan() {
            self.blackjack_pays = 1.5;
        }
        self.blackjack_pays = (dmax(0.0, dmin(10.0, self.blackjack_pays)) * 20.0).round_ties_even() / 20.0;
        if self.penetration.is_nan() {
            self.penetration = 0.75;
        }
        self.penetration = dmax(0.0, dmin(1.0, self.penetration));
    }

    pub fn payout_text(p: f64) -> String {
        for (v, text) in [(1.5, "3:2"), (1.2, "6:5"), (1.4, "7:5"), (1.0, "1:1"), (2.0, "2:1")] {
            if (p - v).abs() < 1e-9 {
                return text.to_string();
            }
        }
        format!("{}:1", dotnet_fixed(p, 2))
    }

    pub fn describe(&self) -> String {
        let mut s = format!("{}{}", self.decks, if self.decks == 1 { " deck" } else { " decks" });
        s.push_str(if self.h17 { ", H17" } else { ", S17" });
        s.push_str(match self.double_on {
            DoubleRule::NineToEleven => ", D9-11",
            DoubleRule::TenToEleven => ", D10-11",
            DoubleRule::AnyTwo => ", DOA",
        });
        if self.das {
            s.push_str(", DAS");
        }
        if self.max_split_hands <= 1 {
            s.push_str(", no splits");
        } else {
            s.push_str(&format!(", SP{}", self.max_split_hands));
        }
        if self.resplit_aces {
            s.push_str(", RSA");
        }
        if self.hit_split_aces {
            s.push_str(", HSA");
        }
        match self.surrender {
            SurrenderRule::Late => s.push_str(", LS"),
            SurrenderRule::Early => s.push_str(", ES"),
            SurrenderRule::None => {}
        }
        match self.hole_card {
            HoleCardRule::Enhc => s.push_str(", ENHC"),
            HoleCardRule::EnhcObo => s.push_str(", ENHC-OBO"),
            HoleCardRule::UsPeek => {}
        }
        s.push_str(", BJ ");
        s.push_str(&Self::payout_text(self.blackjack_pays));
        s.push_str(", pen ");
        s.push_str(&dotnet_fixed(self.penetration * 100.0, 1));
        s.push('%');
        s
    }

    pub fn key(&self) -> String {
        format!(
            "{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}",
            self.decks,
            dotnet_bool(self.h17),
            self.hole_card.name(),
            dotnet_general(self.blackjack_pays),
            self.double_on.name(),
            dotnet_bool(self.das),
            self.max_split_hands,
            dotnet_bool(self.resplit_aces),
            dotnet_bool(self.hit_split_aces),
            self.surrender.name(),
            dotnet_bool(self.insurance),
            dotnet_fixed(self.penetration, 4),
            dotnet_bool(self.burn_card)
        )
    }
}

/// A card counting system. Tags are given per rank (A, 2..9, T); `red_tags` optionally override red cards.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct CountSystem {
    pub name: String,
    pub tags: [f64; 10],
    pub red_tags: Option<[f64; 10]>,
    pub irc_base: f64,
    pub irc_per_deck: f64,
    pub metric: CountMetric,
    pub deck_est: DeckEstimation,
    pub rounding: TcRounding,
}

impl Default for CountSystem {
    fn default() -> Self {
        CountSystem {
            name: "Hi-Lo".to_string(),
            tags: [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, -1.0],
            red_tags: None,
            irc_base: 0.0,
            irc_per_deck: 0.0,
            metric: CountMetric::TrueCount,
            deck_est: DeckEstimation::HalfDeck,
            rounding: TcRounding::Floor,
        }
    }
}

impl CountSystem {
    /// Tag for a card code (code = rank index * 2 + red).
    pub fn tag_of_code(&self, code: usize) -> f64 {
        let ri = code >> 1;
        match &self.red_tags {
            Some(red) if code & 1 == 1 => red[ri],
            _ => self.tags[ri],
        }
    }

    /// Average tag of a rank (1..=10) across colours.
    pub fn rank_tag(&self, rank: usize) -> f64 {
        let ri = rank - 1;
        match &self.red_tags {
            None => self.tags[ri],
            Some(red) => 0.5 * (self.tags[ri] + red[ri]),
        }
    }

    /// Largest multiplier the simulator works with; the count page keeps entries on a grid that fits it.
    pub const MAX_SCALE: i32 = 60;

    /// Smallest multiplier that makes every tag and the initial running count integers, so the simulator counts exactly
    /// the entered values (`MAX_SCALE` when nothing up to it does).
    pub fn scale(&self) -> i32 {
        for s in 1..Self::MAX_SCALE {
            let sf = s as f64;
            let ok = whole(self.irc_base * sf)
                && whole(self.irc_per_deck * sf)
                && (0..20).all(|code| whole(self.tag_of_code(code) * sf));
            if ok {
                return s;
            }
        }
        Self::MAX_SCALE
    }

    /// Sum of tags over one 52-card deck.
    pub fn imbalance_per_deck(&self) -> f64 {
        let mut sum = 0.0;
        for r in 1..=10 {
            sum += self.rank_tag(r) * if r == 10 { 16.0 } else { 4.0 };
        }
        sum
    }

    pub fn is_balanced(&self) -> bool {
        self.imbalance_per_deck().abs() < 1e-9
    }

    pub fn irc(&self, decks: i32) -> f64 {
        self.irc_base + self.irc_per_deck * decks as f64
    }

    pub fn level(&self) -> f64 {
        let mut m = 0.0;
        for code in 0..20 {
            m = dmax(m, self.tag_of_code(code).abs());
        }
        m
    }

    pub fn tag_text(&self) -> String {
        const NAMES: [&str; 10] = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "T"];
        let mut s = String::new();
        for i in 0..10 {
            if i > 0 {
                s.push(' ');
            }
            s.push_str(NAMES[i]);
            s.push(':');
            s.push_str(&signed_tag(self.tags[i]));
            if let Some(red) = &self.red_tags
                && (red[i] - self.tags[i]).abs() > 1e-9
            {
                s.push_str("/r");
                s.push_str(&signed_tag(red[i]));
            }
        }
        s
    }

    pub fn key(&self) -> String {
        let mut s = String::new();
        for code in 0..20 {
            s.push_str(&dotnet_fixed(self.tag_of_code(code), 3));
            s.push(',');
        }
        s.push_str(&dotnet_general(self.irc_base));
        s.push('|');
        s.push_str(&dotnet_general(self.irc_per_deck));
        for part in [self.metric.name(), self.deck_est.name(), self.rounding.name()] {
            s.push('|');
            s.push_str(part);
        }
        s
    }

    fn make(name: &str, tags: [f64; 10], irc_base: f64, irc_per_deck: f64, metric: CountMetric) -> CountSystem {
        CountSystem { name: name.to_string(), tags, irc_base, irc_per_deck, metric, ..CountSystem::default() }
    }

    pub fn presets() -> Vec<CountSystem> {
        use CountMetric::{RunningCount, TrueCount};
        let mut list = vec![
            //                     A     2    3    4    5    6    7    8    9     T
            Self::make("Hi-Lo", [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, -1.0], 0.0, 0.0, TrueCount),
            Self::make("Hi-Opt I", [0.0, 0.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, -1.0], 0.0, 0.0, TrueCount),
            Self::make("Hi-Opt II", [0.0, 1.0, 1.0, 2.0, 2.0, 1.0, 1.0, 0.0, 0.0, -2.0], 0.0, 0.0, TrueCount),
            Self::make("Omega II", [0.0, 1.0, 1.0, 2.0, 2.0, 2.0, 1.0, 0.0, -1.0, -2.0], 0.0, 0.0, TrueCount),
            Self::make("Zen Count", [-1.0, 1.0, 1.0, 2.0, 2.0, 2.0, 1.0, 0.0, 0.0, -2.0], 0.0, 0.0, TrueCount),
            Self::make("Wong Halves", [-1.0, 0.5, 1.0, 1.0, 1.5, 1.0, 0.5, 0.0, -0.5, -1.0], 0.0, 0.0, TrueCount),
            Self::make("Uston APC", [0.0, 1.0, 2.0, 2.0, 3.0, 2.0, 2.0, 1.0, -1.0, -3.0], 0.0, 0.0, TrueCount),
            Self::make("Revere RPC", [-2.0, 1.0, 2.0, 2.0, 2.0, 2.0, 1.0, 0.0, 0.0, -2.0], 0.0, 0.0, TrueCount),
            Self::make("Canfield Expert", [0.0, 0.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, -1.0, -1.0], 0.0, 0.0, TrueCount),
            Self::make("Silver Fox", [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, -1.0, -1.0], 0.0, 0.0, TrueCount),
            Self::make("KO (Knock-Out)", [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, -1.0], 4.0, -4.0, RunningCount),
        ];
        let mut red7 =
            Self::make("Red Seven", [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, -1.0], 0.0, -2.0, RunningCount);
        red7.red_tags = Some([-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, -1.0]);
        list.push(red7);
        list.push(Self::make(
            "Uston SS",
            [-2.0, 2.0, 2.0, 2.0, 3.0, 2.0, 1.0, 0.0, -1.0, -2.0],
            0.0,
            -2.0,
            RunningCount,
        ));
        list
    }
}

fn signed_tag(v: f64) -> String {
    format!("{}{}", if v > 0.0 { "+" } else { "" }, dotnet_fixed(v, 2))
}

/// Whether a scaled tag is an integer. Written like the C# check, `!(x > tol)`, so a NaN tag does not disqualify a
/// scale.
#[allow(clippy::neg_cmp_op_on_partial_ord)]
fn whole(v: f64) -> bool {
    !((v - v.round_ties_even()).abs() > 1e-9)
}

/// A user-entered index play that replaces the computed/basic decision for one flag of one cell.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct IndexOverride {
    pub enabled: bool,
    pub kind: HandKind,
    /// Hard/soft total, or pair rank 1..=10.
    pub total: i32,
    /// 1..=10 (1 = ace).
    pub upcard: i32,
    pub play: PlayKind,
    pub at_or_above: bool,
    pub index: f64,
}

impl Default for IndexOverride {
    fn default() -> Self {
        IndexOverride {
            enabled: true,
            kind: HandKind::Hard,
            total: 16,
            upcard: 10,
            play: PlayKind::Stand,
            at_or_above: true,
            index: 0.0,
        }
    }
}

impl IndexOverride {
    pub fn key(&self) -> String {
        format!(
            "{}{}{}{}{}{}{}",
            if self.enabled { 1 } else { 0 },
            self.kind.name(),
            self.total,
            self.upcard,
            self.play.name(),
            if self.at_or_above { ">=" } else { "<=" },
            dotnet_general(self.index)
        )
    }
}

#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct StrategyConfig {
    pub mode: IndexMode,
    pub index_min: i32,
    pub index_max: i32,
    pub overrides: Vec<IndexOverride>,
}

impl Default for StrategyConfig {
    fn default() -> Self {
        StrategyConfig { mode: IndexMode::I18Fab4, index_min: -10, index_max: 10, overrides: Vec::new() }
    }
}

impl StrategyConfig {
    pub fn key(&self) -> String {
        let mut s = format!("{}|{}|{}", self.mode.name(), self.index_min, self.index_max);
        for o in &self.overrides {
            s.push('|');
            s.push_str(&o.key());
        }
        s
    }
}

pub mod cards {
    pub const UP_LABELS: [&str; 11] = ["", "A", "2", "3", "4", "5", "6", "7", "8", "9", "T"];
    /// Dealer upcards in the usual chart order 2..9, T, A.
    pub const CHART_UP_ORDER: [usize; 10] = [2, 3, 4, 5, 6, 7, 8, 9, 10, 1];

    pub fn rank_name(r: usize) -> &'static str {
        UP_LABELS.get(r).copied().unwrap_or("?")
    }
}
