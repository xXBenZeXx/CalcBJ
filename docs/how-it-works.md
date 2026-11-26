# How CalcBJ works

CalcBJ's engine is written in Rust (`web/engine`) and runs as WebAssembly inside every version of the app. It has four
parts: an exact combinatorial analysis, the index generation built on it, a shoe simulator, and the analysis of bet
ramps and risk.

## Combinatorial analysis

`web/engine/src/ca.rs`, `dealer.rs`: exact dealer probabilities with card removal for every player two-card hand and
upcard (the dealer's draws are enumerated as multisets, so this is fast: ~1–3 ms per full analysis), optimal
hit/stand/double/split (with resplits)/surrender, total-dependent decisions.

Basic strategy itself takes every card the player draws out of the shoe too (hits with the best play after them,
doubles, splits), and in peek games the player's draws know that the dealer has no blackjack, so close one- and two-deck
plays come out as in the published charts (e.g. double 11 v A with 2 decks S17). Where a total's two-card hands double
or surrender, its hit/stand fallback comes from the hands of three or more cards that use it (16 v T with surrender:
stand).

The house edge off the top then plays that strategy exactly, as the simulator does: hits, doubles and split hands with
full card removal (a split hand with its own cards and the pair cards out of the shoe; leaving the other split hands'
cards in makes no measurable difference).

## Index generation

`web/engine/src/strategy.rs`: a play's index is the count at which the player decides, which includes the player's
cards and the dealer's upcard. So for each count value and each hand, those cards are taken out of the shoe and the
rest is estimated by linear regression of the rank counts on the tag sum (red and black cards that count differently
included), to the composition whose count (with the cards on the table) is that value. The number of unseen cards and
their average count in each count bin come from the distribution of the count over the shoe. The full analysis of that
composition gives the best play at that count.

Its draw odds stay as they are after the first three cards (exact removal at every count would be too slow), so the
change that exact removal makes is worked out off the top and carried over to every count, scaled by the cards left.
Two more things follow the count: the hands of a total are weighted by how often each comes up at that count (with one
deck T,2 is nine in ten of the 12s at Hi-Lo +1), and a total's stand-or-hit also counts its hands of three or more
cards, which the simulator plays by the same decision (16 v T stands from 0, as published).

Checked against a Monte-Carlo of 180 million one-deck rounds with exact values at the simulator's real decision points:
on the 91 index lines tested the generated indices earn 0.269 and 0.106 units per 100 rounds in two games, where
version 0.1.3 earned 0.252 and 0.103 (the best possible: 0.287 and 0.112). For Hi-Lo, 6 decks S17, the I18 + Fab 4
indices are within one point of the published numbers, which are for unfloored true counts. The simulator floors (TC 0
means 0 to 0.99), so 12 v 2, for example, stands from +4.

## Simulator

`web/engine/src/sim.rs`: real shoes, all spots, peek/no-peek, insurance, surrender, splits; every decision uses the
count at that moment. Results are tallied exactly per count bin, so bet ramps never need re-simulation. The hot loop
deals lazily (each card is a single Fisher–Yates step, so only dealt cards cost a random number), keeps rank and tag
packed in one value per card and skips the count lookup for decisions without index plays; what remains is mostly
unpredictable game branching (hit/stand, busts, dealer draws).

The app keeps the results per configuration, under a key that also names the engine's results version: results that an
older version of CalcBJ simulated are shown as outdated, and Run starts those settings again from zero instead of adding
to them (`RESULTS_VERSION`, see `web/engine/README.md`).

## Bet ramps and risk

`web/engine/src/analysis.rs`: ramp evaluation, multi-hand covariance, ROR (exponential and trip/reflection formulas),
Kelly/SCORE optimiser.

## Validation

The engine's tests (`cargo test` in `web/engine`) check:

* **House edges:** for 8D, 6D S17 and 6D H17 (DAS, no surrender) they are 0.432%, 0.406% and 0.618%, within 0.03% of
  published figures (`tests/ca.rs`).
* **Basic strategy is the best there is:** changing any one decision of any chart cell gains at most 0.0001% of exact
  expectation (`tests/basic.rs`, 12 games in about 16 seconds; over 600 rule sets the largest gain was 0.00007%).
* **Simulation against analysis:** for 1 to 8 decks and the other rule variants, single and double deck with resplits
  and doubling after splits included, 2 billion shuffle-every-round rounds per game agree with the off-the-top analysis
  within 3.5 SE, about 0.009% (`tests/offtop.rs`, about 2 minutes on 16 threads, so it runs only when asked for; in the
  last run all 11 games were within 0.006% and 2.3 SE).
* **The count model:** random draws of unseen cards agree with the composition the index generation uses at a count,
  for counts with separate red tags too (`tests/count_model.rs`).
* **Everything else** (analyses, strategy tables, index lists, seeded simulations, ramp evaluations, the optimiser) is
  compared with reference results in `web/engine/tests/fixtures`, so any change to a number shows up.

See `web/engine/README.md` for the tests and the reference results.
