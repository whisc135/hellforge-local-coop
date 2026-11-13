# Install

## Players (release zip)

1. Have HellForge installed (folder with `HellForge.exe`, `app\`, `game\`).
2. Download the latest `HellForge-LocalCoop_v*.zip` from Releases.
3. Quit Helldivers / HellForge / LocalBackend.
4. Run `INSTALL.cmd`.
5. Prefer `HellForge-Portable.cmd` when you use Steam AppId **480** for the backend peer link.

The installer does **not** upgrade HellForge (e.g. 0.2.0 → 0.4). It only patches LocalBackend / PartyWin / related helpers on top of whatever HellForge version you already have. Stock **0.2.0** stays **0.2.0** in the UI; that is expected.

### What gets replaced

- `app\backend\HellForge.LocalBackend.real.exe` — patched LocalBackend  
- `app\backend\HellForge.LocalBackend.exe` — small wrapper (created if you still have a stock single-exe layout)  
- `app\backend\steam_api64.spacewar.dll` — used by the wrapper for steam-link  
- `app\sdk-shims\PartyWin.dll` — voice-capable Party stand-in  
- `game\bin\steam_api64.dll` — restored to Valve size for build **25480438** when a GSE/online-fix DLL broke `gameCopy`  
- hashes in `app\sdk-shims\build.json` and `app\engine.json`  
- optional `app\scripts\skip-steam-check.enabled` + ownership script for portable installs  

Backups land in `app\backend\backups\party-voice-*` (historical folder name).

### Squad rule

Everyone in the lobby must run the **same** patch version (same backend + PartyWin pair).

## Developers

See the root README for `Invoke-CiTests.ps1` and overlay sync.
