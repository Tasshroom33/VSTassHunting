# Tass Hunting - version history

One entry per release, written in the same commit that bumps modinfo.json.
Each line says what a player would notice. The packager refuses to zip a
version that has no entry here and prints the entry with every new zip.

## 0.14.14 (2026-08-18)
On multiplayer servers the gameplay settings now come from the server: every
player who joins plays by the server's config instead of their own config
file quietly deciding part of the rules on their screen. This fixes players
being unable to loot animal corpses on servers where the owner turned the
auto harvest off (reported by earwiq) - the other players' own game was
still hiding the loot window using its default settings. Look-and-feel
settings (bleeding box corner, blood looks and colors, the power shot click)
stay each player's own. Also, harvest time 0 in the config now means vanilla
speed - it used to mean near-instant, which nobody setting 0 wanted.

## 0.14.13 (2026-08-18)
Fixes the stuck bleeding after leaving a world mid-bleed (reported by
Sanches31). Before this, if you exited a world while bleeding, you came back
with a bleed that never counted down, dealt no damage, and no bandage or
poultice could stop - because the "you are bleeding" marker was saved with
your character but the actual wounds were not. Now everything bleeding-related
is wiped clean the moment you or any animal re-enters the world, and a bandage
also clears a stuck marker from a world saved before this fix. Rejoining a
multiplayer server had the same problem and is fixed the same way.

## 0.14.12 (2026-08-03)
Groundwork for Tass Factions bounties. When a player wounds another player, the
victim now remembers who bled them, so if they later bleed out, Tass Factions
credits the right hunter for a bounty instead of counting it as a nobody kill.
Nothing changes if you are not running Tass Factions bounties.

## 0.14.11 (2026-08-03)
Armor now matters to bleeding. A wound is only as big as the part of the hit
your armor let through, and armor that stops most of a blow turns the edge so
you do not bleed at all. Sitting still helps: stay seated five seconds and the
bleeding does half damage and the wounds close in half the time, for as long as
you stay down - standing up ends it and the five seconds start over. New dials
for how badly rust beings and wild animals cut you, either of which can be set
to zero so they never make you bleed.

## 0.14.1 (2026-08-03)
Baseline entry at the version current when this changelog was created. The
history before it is not backdated; entries begin with the next release.