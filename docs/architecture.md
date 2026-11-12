# Architecture

```
HellForge session
  └─ LocalBackend (per PC)
        ├─ HTTP: /Operation/Mission/end, /Progression, /StoreFront/…
        └─ Steam peer frames (lobby / mission.ended / pickups / party.voice)
```

## Mission reward share

The game often posts `/Operation/Mission/end` only on the **host** backend.
`MissionEndShare` broadcasts a `mission.ended` frame so each friend’s backend
can `CreditEnd` once per `missionId` (XP, medals, requisition, sample pool,
weapon packages).

## Samples

Extracted players receive the **sum** of extracted `lootedItems` pockets.
Players who did not extract get an empty sample grant.

## Weapon UI

Profiles store `packageProgress` on disk immediately. The client loads
`GET /Progression` mainly at login; Mission/end therefore returns a full
`progressionPackageUpdates` snapshot. Joiners who never POST Mission/end still
rely on the share for disk updates; their armory UI may need a relaunch.

## Voice

`PartyWin.dll` stand-in + LocalBackend `party.voice` path (Opus / RNNoise).
Build the stand-in with HellForge’s sdk-shim tooling; this repo keeps codec
reference sources under `src/native/sdk-shims/`.
