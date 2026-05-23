# Timeline

I kept losing track of where damage was coming from in busy fights —
which relic triggered, what cascade resolved, whose debuff did that.
This adds an in-combat overlay listing every event in order with
hover-tooltips for what triggered what.

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
