# Contributing

## Overlay workflow

1. Develop against a HellForge checkout (GPL-3).
2. Sync changed LocalBackend sources into this repo:

```powershell
.\scripts\Sync-Overlay.ps1 -HellForgeRoot "D:\path\to\HellForge"
```

Or copy the whole `src/HellForge.LocalBackend` tree (excluding `bin`/`obj`) into `src/overlay/HellForge.LocalBackend`.

3. Bump `PATCH_VERSION` in `UPSTREAM.lock` / `FEATURES.txt` when behaviour changes.
4. Run tests:

```powershell
pwsh ./scripts/Invoke-CiTests.ps1
```

5. Publish binaries via `scripts/Build-ReleaseZip.ps1` and a GitHub Release asset.

## Scope

Keep this repository a **companion** for HellForge LocalBackend squad testing.
Do not add game assets or retail depot files.
