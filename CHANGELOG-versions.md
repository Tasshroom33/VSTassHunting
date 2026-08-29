# Tass Hunting - version history

One entry per release, written in the same commit that bumps modinfo.json.
Each line says what a player would notice. The packager refuses to zip a
version that has no entry here and prints the entry with every new zip.

## 0.14.34 (2026-08-29)
Death messages name the real killer. "Killed by a wild animal" becomes the
actual animal - the dino packs ship their name keys where the engine never
looks, so every dino death read as "wild animal"; now a rex kill reads
"killed by Tyrannosaurus - Tyrant Lizard King" and every other modded
creature gets its real species name. The common name after the dash is a
config list (KillerCommonNames), preloaded with each dino family named
after its pack; unlisted creatures still show their species. The damage
log improves the same way ("attacked by a Velociraptor"). And a player
who goes down and bleeds out minutes later is still reported as killed by
whatever downed them, instead of just "died" - including when the downed
mod loses track (relog while down, admin force-down). One switch
(KillerNamesEnabled, on by default), /tassdeathnames shows what it is
doing, and nothing about how deaths are recorded for other mods changes -
only the chat wording.

## 0.14.33 (2026-08-28)
New switch: tree leaves can be made walk-through (off by default). The
branchy leaf clumps around trunks lose their solid box, the way most
leaves already are, so nothing snags on tree tops anymore: big animals
stop getting stuck on canopy when they chase you, and you cannot stand
in or on a tree out of their reach - climbing a tree to wait out a
predator goes away with it. Chopping, tree felling and drops stay as
before. Which leaf blocks it covers is a config list; only real leaf
blocks are ever touched, whatever the list says.

## 0.14.32 (2026-08-28)
Swapped step sounds got a volume slider. Deep pitch softens a sound's
attack, and a replacement recording can be quieter than the file the
creature's volumes were tuned for, so heavy steps could come out light.
The new slider turns the swapped steps up on top of each animal's own
tuning to put the punch back.

## 0.14.31 (2026-08-28)
Heavy steps can go much deeper now. The pitch floor drops from 0.4 to 0.2,
so at the default settings a tyrannosaur steps at quarter pitch and the
biggest animals bottom out at a fifth of the recording. The deepen slider
also runs the right way now: higher means deeper (it was backwards). Near
the bottom a running giant's steps stretch and overlap into a low rumble.

## 0.14.30 (2026-08-28)
The sound settings got their own "Sounds" section in the config panel:
behind-you steps on or off, what counts as a heavy step, and how much the
step pitch drops with body size. Which sound the heavy steps use stays a
list in the config file, and the panel now says so.

## 0.14.29 (2026-08-28)
Big steps got their bass. The swapped thump was playing at its recorded
pitch, which made an eight ton animal sound like a horse at a canter. The
step pitch now drops with body size: a full grown tyrannosaur booms at less
than half pitch, the biggest sauropods bottom out even deeper, mid-size
animals sit in between, and anything wolf-sized is untouched. One slider
controls how strong the effect is, 0 turns it off.

## 0.14.28 (2026-08-28)
Heavy footsteps can be given a better sound. A new config list swaps the
step sound of heavy walkers for any sound you name, keeping each animal's
own step timing, loudness and pitch variation. Made for creature mods that
shipped scratchy scrape recordings for their biggest stompers: point the
list at a proper thud and every heavy step uses it, on screen and behind
you. Steps quieter than the heavy line (normal wolves and smaller) always
keep their own sounds, and other animation sounds like roars or eating are
never touched.

## 0.14.27 (2026-08-28)
You can hear the big ones behind you now. The game only animates creatures
that are on your screen, and footsteps are played by the animation - so the
moment you turned away from a dinosaur, its steps went silent, which is
exactly backwards for the one sound your life depends on. Heavy creatures -
anything whose steps are loud enough to carry a long way - now keep stomping
audibly while off your screen, using their own step sounds, their own walking
or running pace, and their own loudness. On-screen animals are untouched
(the real animation still plays their steps), small animals were always
quiet enough that nothing changes, and there is a switch per player.

## 0.14.26 (2026-08-28)
Predators get full again. Two bugs had turned them into serial killers: the
extra prey from the food chain was riding a hunting instinct that never gets
hungry (the packs' own prey list rides one that pauses after eating), and
the bones rule was deleting the very carcass whose eating makes a predator
full - so nothing ever was, and the killing never paused. Now new prey goes
on the same hunger-governed hunt as the creature's own prey list, and when a
kill turns to bones the killer counts as fed on the spot - the bones are the
meal it ate. A predator makes its kill, gets full, and lives beside its
leftovers until hunger returns, exactly like its own kind of hunting always
worked. Its own switch in the config for the fed-by-bones half.

## 0.14.25 (2026-08-28)
The bones rule learns whose kill it really was. It used to judge only by the
killing blow, which robbed hunters three ways: an arrow kill could credit the
arrow instead of the archer, a predator landing the last bite on an animal
you had already worn down turned YOUR quarry to bones, and an animal driven
into a pit died to the fall - nobody's kill. Now every hit you land marks
the animal as yours for a while (two minutes by default, a slider), so if it
dies within that window - whoever or whatever finishes it - the corpse and
loot are yours. Bleeding out of your wounds still counts as always. With
blood diagnostics on, every bones-or-corpse ruling is logged with its
reasoning, so a wrongly vanished corpse becomes a log line instead of a
mystery.

## 0.14.24 (2026-08-28)
The food chain opens up, and the wilds clean up after themselves. A new
config list lets a server hand any hunter extra prey - name a creature and
what it may now hunt, and those targets are added to its hunting and biting
lists when the world loads. Tamed animals are fair game the moment their
species is listed: a predator does not check for a lead rope. Nothing
changes for servers that leave the list empty. And with the new bones switch
on, any kill with no player behind it decays where it fell into the
creature's bones instead of lying there as free harvestable meat - so a
world full of fighting animals does not carpet itself in corpses. Your own
kills always keep their corpse and loot, including animals that bleed out
of your wounds after a long chase.

## 0.14.23 (2026-08-28)
Blood pools stop hovering. The pool under a kill used to spawn every drop at
the height of the pool's center, so on a slope or a ledge the outer ring hung
in mid air - and the pool never settled, by an old design choice made when
pools were burying themselves. Now each drop finds the ground directly under
itself like trail drops always did (a drop over a cliff edge is simply not
born), and the pool slowly settles a quarter of its height into the ground
over its lifetime - visible soak-in that can never end up buried, whatever
size the pool is. Most noticeable under the biggest kills, which have the
widest pools.

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