# Timeline

This is a timeline mod which shows the timeline of the events that happened in the game.  I tried to group things reasonably so there isnt too much clutter of different events on the timeline, and hovering over an event tells you in detail what all happened in that event.  Its just a nice thing for keeping track of things.  I haven't really tried it on multiplayer much but it should work(I think I had it open for one run but didn't really check)


timeline closed(default)
<img width="569" height="576" alt="Screenshot 2026-05-23 at 2 28 35 PM" src="https://github.com/user-attachments/assets/a4105140-9e3a-4134-8d83-31392be40819" />

timeline open(click anywhere on it to open it)
<img width="1511" height="853" alt="Screenshot 2026-05-23 at 2 28 03 PM" src="https://github.com/user-attachments/assets/95620e57-1d7e-474c-93a0-67d103e5bd63" />

if you have suggestions or find bugs leave them as issues here and I'll get to them.

The rest of this was written by claude so its probably right but idk:

## What it does

- Overlay showing every event in the current fight in chronological
  order: card plays, attacks, relic triggers, power applications.
- Each event uses the actual game card / relic / power icon (same art
  the deck-view uses) so it looks native rather than bolted on.
- Hover any event to see the parent (what triggered it) and the
  children (what it triggered in turn).
- Updates live as combat progresses; persists for the fight.
- No gameplay effect.

## Known limits

- Long fights produce a long list. The overlay scrolls but on
  particularly busy turns it can get dense.
- STS2 disables achievements while any mod is loaded — uninstall if
  you're chasing those.

## Install

### Steam Workshop

Subscribe via the game's Workshop page. Launch the game and enable the
mod from the in-game Mods screen.

### Manual

1. Download the zip from the [Releases page](../../releases).
2. Extract so the folder structure is
   `<game>/mods/Timeline/{Timeline.dll, mod_manifest.json}`.
   - Mac: `<game>/SlayTheSpire2.app/Contents/MacOS/mods/Timeline/`
   - Windows/Linux: `<game>/mods/Timeline/`
3. Launch the game and enable Timeline on the in-game Mods screen.

## Build from source

Requires .NET 9 SDK and a local copy of Slay the Spire 2.

```
./build.sh
```

The build script compiles `Timeline.dll` and copies it + the manifest
into your game's `mods/` folder.

## Companion mods

- [Retry](https://github.com/sts2mods/Retry) — replay any past run
  from any floor.
- [Run Table](https://github.com/sts2mods/RunTable) — searchable
  table of your past runs.
- [Enemy Cycle](https://github.com/sts2mods/EnemyCycle) — see enemy
  move cycles.

## License

MIT.
