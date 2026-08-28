# Tass Hunting - version history

One entry per release, written in the same commit that bumps modinfo.json.
Each line says what a player would notice. The packager refuses to zip a
version that has no entry here and prints the entry with every new zip.

## 0.14.22 (2026-08-28)
Big herbivores can now hold their ground. Two new creature lists in the
config: retaliation animals remember who hurt them far longer (three minutes
of anger instead of one), chase them twice as far (40 blocks instead of 20)
and press the chase for two minutes instead of thirty seconds - so wounding a
triceratops and stepping back no longer resets it to grazing. Territorial
animals go further: they start the fight themselves when a player walks into
their space, using the same instinct they already had for defending their
young - now the territory is around the animal itself, since most spawn
without young. They keep re-arming as long as you stand inside the radius and
only calm down after you have truly left. Everything is sliders and
config-file lists; both lists ship empty, so nothing changes until a server
names its creatures. Only works on worlds where creature hostility is set to
aggressive (the normal setting) - passive worlds stay passive.

## 0.14.21 (2026-08-28)
Server owners can now tune any creature's bite from the config file. A new
list maps creature names (wildcards allowed) to a damage multiplier, applied
to their melee attacks when the world loads - health, speed, drops and
everything else stay exactly as the creature's own mod shipped them. Built
for modded creatures that hit far above their weight: in the dinosaur packs,
two small pack hunters bit at double the damage the rest of their own roster
follows for their size, hard enough to down an unarmored player in two
touches. A name in the list that matches nothing logs a warning instead of
silently doing nothing.

## 0.14.20 (2026-08-28)
The red bleed blink is back. Bleeding creatures flash red on every bleed tick
again, the same tint and half-second fade as a normal hit - but purely as a
light: unlike the old days, the flash never makes the animal briefly immune
to your next hit, so a bleeding target stays fully hittable and two hunters
never rob each other's arrows through it. If a real hit lands mid-blink, the
normal hit flash takes over cleanly. Its own switch in the config panel,
per player, on by default.

## 0.14.19 (2026-08-28)
Sharper metal bites deeper. The chance that thick hide turns an arrow or spear
now falls as the weapon's damage rises: flint is the baseline and everything
at or under flint grade bounces exactly as before, but each point of weapon
damage above it shaves the bounce chance - a steel spear bounces about a
third less often than a flint one, with copper and the bronzes in between.
No blade ever gets the bounce below about a third of the base chance: plate
stays plate. The huge bites of the biggest predators sit on that same floor,
so crushing jaws mostly punch through armor that turns your spears. Three new
sliders control the baseline, the per-point step and the floor.

## 0.14.18 (2026-08-28)
Big game finally fights like big game. Bleeding used to grow with an animal's
health, which quietly meant everything - a hare or an 800-health dinosaur -
bled out in about the same two and a half minutes from the same two flint
spears. Now bleed damage levels off past bear size, so a giant needs four to
six wounds open at once instead of two, and the bigger it is the longer it
takes to go down. On top of that, thick hide can turn a blade: anything
bigger than a bear has a chance that an arrow or spear bounces off - it drops
at the animal's feet, still yours to pick up, opens no wound and does not
stick. A bear turns about one spear in twenty; the biggest beasts turn up to
half, and a per-creature list in the config file lets armored hides turn more
and soft giants fewer - though nothing can ever be made arrow-proof. The
answer to thick hide is patience: a full power-shot draw punches through,
halving the bounce chance. Everything wolf-sized and smaller is exactly as it
was - hits always bite, and bleed numbers are untouched.

## 0.14.17 (2026-08-28)
Some animals can now be kept wild for good. Name creatures in the new stay-wild
list and nobody can ever tame, pet, rope or ride them - not even after
installing a mod that would normally allow it, because the taming is taken off
the creature itself when the world loads rather than blocked when somebody
tries. Built for dinosaur packs, where the intent is that you fear them instead
of saddling them. Off by default and it only touches creatures you name, so
your chickens, goats and riding elk are untouched.

## 0.14.16 (2026-08-22)
Bleeding reworked around the size of whatever is bleeding. A single wound now
hurts about a third as much as it used to but stays open far longer, and each
extra wound both hurts more and drags the whole bleed out - so one bite is
something you notice and bandage, while being swarmed is what kills you. How
long a wound stays open depends on the body: small animals 12 seconds, up to 80
for a moose or a bear, players 30, and rust from 20 up to 45 for the nastiest
drifters. How likely something is to make you bleed now depends on its size
too - a fox draws blood about half the time, a wolf three times in four, and a
bear always, sometimes twice from one swing. Blunt attacks still never cause
bleeding. Your own weapons always draw blood, no luck involved, and they hit
harder to make up for the gentler bleed - so hunting feels the same as before,
except big game now bleeds out properly. Existing configs are upgraded
automatically, which resets the bleed damage numbers to the new balance; blood
looks, harvest and archery settings are left alone.

## 0.14.15 (2026-08-19)
The bleeding box's default spot moved 50 pixels down. It used to sit exactly
where the XSkills effects panel sits, so with both mods the two panels could
stack on top of each other and the bleeding box looked like it never showed
up. Your own position and offset settings still apply on top of the new spot.

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