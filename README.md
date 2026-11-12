# HellForge Local Co-op

Play HellForge co-op with friends and **actually keep the rewards** — not only on the host.

Companion patch for [HellForge](https://github.com/SkyeShade/HellForge). Install HellForge first, then apply this from [Releases](../../releases).

## What you get

| | |
|---|---|
| **Progression for the whole squad** | Joiners earn **XP, medals, and requisition** after a mission — same payout rules as the host, once per mission. |
| **Shared sample extract** | Everyone who **extracts** gets the squad sample pool (common / rare / super), not only who picked them up. |
| **Weapon mastery** | Guns you use gain mastery XP on your profile (and the mission results can refresh the armory snapshot). |
| **Squad voice** | In-game voice chat through the Party stand-in (Opus / RNNoise). Headphones recommended. |
| **Pickups in co-op** | Medal / requisition / Forge Credit piles credit every backend in the lobby. |
| **Superstore stub** | Superstore page works locally; purchases spend **Forge Credits**. |

Without this patch, HellForge’s LocalBackend often only sees `Mission/end` on the **host** PC — friends finish the dive, see the results screen, then lose medals / samples / weapon XP after a relaunch.

## Quick start

1. Install / update **HellForge**.
2. Download `HellForge-LocalCoop_vN.zip` from [Releases](../../releases).
3. Close the game and HellForge → run `INSTALL.cmd`.
4. Prefer `HellForge-Portable.cmd` → PLAY when using the Spacewar (480) peer-link setup.
5. **Same release on every machine** in the lobby.

Installer backups go under `app\backend\backups\`. Profiles are not wiped.

More detail: [docs/install.md](docs/install.md) · changelog: [FEATURES.txt](FEATURES.txt)

## Requirements

- Windows 10/11, Steam signed in  
- Working HellForge install (portable **0.4** recommended; stock **0.2.0** backend layout supported)  
- Same patch build for the whole squad  

## For developers

```
install/     # scripts shipped in the release zip
src/overlay/ # LocalBackend sources applied onto HellForge in CI
src/wrapper/ # thin LocalBackend wrapper (Spacewar steam-link helper)
scripts/     # tests, overlay sync, release zip
docs/        # install + architecture
```

Binaries live on **Releases**, not in git.

```powershell
.\scripts\Invoke-Validate.ps1
.\scripts\Invoke-CiTests.ps1
.\scripts\Sync-Overlay.ps1 -HellForgeRoot "D:\path\to\HellForge"
.\scripts\Build-ReleaseZip.ps1 -PayloadDir ".\payload" -Version v21
```

CI pins HellForge via [`UPSTREAM.lock`](UPSTREAM.lock) and caches the clone.  
Push a `vN` tag (or run **Release** → workflow_dispatch) to publish `HellForge-LocalCoop_vN.zip`.  
Architecture notes: [docs/architecture.md](docs/architecture.md).

## Licence

GPL-3.0 for overlay sources ([LICENSE](LICENSE)), same spirit as HellForge.  
HELLDIVERS 2 © Arrowhead / PlayStation — this repo does not ship game assets.  
Unofficial; not affiliated with Arrowhead, PlayStation, or the HellForge maintainers.
