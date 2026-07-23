# TassHunting

A hunting overhaul for Vintage Story that makes the game feel more realistic
and alive: arrows and spears that stick in what they hit, arrow break chance and
recoverable arrowheads, more aggressive predators, blood trails and bloody water,
health-based animal run speed, damage-over-time bleed, better close-range bow
aiming, longer audible predator footsteps, smarter fleeing, faster harvesting
with auto-drop loot, and in-game config via ConfigLib.

Rust creatures (drifters, locusts, shivers, bells, bowtorn, eidolon) take arrows
and bleed like anything else, but do not show red blood by default (toggle in
config). The smarter predator AI applies to animals only.

## License

TassHunting is released under the MIT License - see the LICENSE file. You are
free to use, modify, and redistribute it, including in modpacks, as long as the
copyright notice is kept.

The MIT license covers this mod's own source code only. The DLLs bundled under
lib/ are third-party build-reference dependencies and keep their own licenses:
- ConfigLib (configlib.dll) - by Maltiez, used for the in-game config panel
- ImGui.NET (ImGui.NET.dll) - MIT

## Building

Set the VINTAGE_STORY environment variable to your Vintage Story install
directory, then build TassHunting/TassHunting.csproj. The build syncs the mod
to your VintagestoryData/Mods folder automatically.
