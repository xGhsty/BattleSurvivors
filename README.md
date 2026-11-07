# Battle Survivors

RimWorld patch for Dynamic Diplomacy (DD) and Eternal Pawns (EP).

DD removes every pawn from a faction battle map with `Destroy(Vanish)`, including pawns you marked with "Remember character". This mod saves those pawns to world pawns and the EP veteran pool first, so they can come back later as caravan members or visitors.

## Behavior

- Pinned pawns and EP veterans on a DD battle map are saved before DD destroys them.
- Pawns left downed by the losing side are decided at random: 50% recruited by the winners, 50% killed.
- Downed pawns on the winning side are saved unchanged.
- Pawns that are not pinned or not veterans are handled by DD as usual.

## Requirements

- RimWorld 1.6 or 1.7
- Harmony
- Dynamic Diplomacy
- Eternal Pawns

## Usage

Press "Remember character" for each pawn you want to keep. Opening the Memory window is not enough.

## Build

Set `ManagedDir` and `HarmonyDll` in `Source/BattleSurvivors.csproj` to your local paths and build. Output goes to `1.6/Assemblies`.

## How it was built

The cause was found by decompiling the IL of both mods: DD calls `Destroy(Vanish)` on every pawn when a battle map ends, before EP can move them to the veteran pool. The mod adds a Harmony prefix on `Pawn.Destroy`, limited to DD battle maps, and uses reflection so it needs no compile-time reference to either mod. Behavior was verified against game logs. The code was written with AI assistance (Claude code).

## Notes

- Eternal Pawns only sends pawns that are not downed, so a downed winner may need time to heal before it appears in a caravan.
- Log lines are prefixed with `[BattleSurvivors]`.
