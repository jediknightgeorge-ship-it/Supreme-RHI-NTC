# Supreme RHI + NTC — BETA

> **⚠ THIS IS A BETA VERSION — USE AT YOUR OWN RISK**
>
> All NTC texture compression features are experimental. The app always creates automatic backups before modifying any game file, but compatibility is not guaranteed for every game. No liability for broken saves, corrupted archives, or unexpected behavior.

---

## What is this?

**Supreme RHI + NTC** is a fork of [RHI (ReShade HDR Installer)](https://github.com/RankFTW/RHI) by RankFTW, extended with **NTC Texture Compression** — an experimental feature that compresses game textures to BCn formats (BC7 / BC5 / BC4) using [Microsoft DirectXTex texconv](https://github.com/microsoft/DirectXTex), reducing VRAM usage.

Tested on **RTX 2080 Ti (Turing)**. Compression is 100% CPU-side and offline — the GPU is never touched during the process.

---

## Added features (BETA)

### NTC — Texture Compression panel

Every game card in the detail view now shows a **"NTC — Compresión de Texturas"** section with two sub-features:

#### 1. Loose DDS textures
Scans the game's install folder for uncompressed `.dds` files and compresses them in-place using `texconv.exe`.

- **Scan** → detects uncompressed DDS (R8G8B8A8, B8G8R8A8, etc.)
- **Compress all** → runs texconv (BC7 for color, BC5 for normals, BC4 for grayscale)
- **Restore originals** → reverts to backed-up originals

#### 2. Bethesda Archives (BSA / BA2)  *(BETA)*
Extracts DDS textures from Bethesda game archives, compresses them, and repacks.

| Format | Games |
|--------|-------|
| BSA v104 | Skyrim |
| BSA v105 | Skyrim Special Edition |
| BA2 GNRL | Fallout 4, Starfield |
| BA2 DX10 | Fallout 4 (texture archives) — repacked as GNRL |

- Originals are always backed up to `<game_folder>/_ntc_archive_backup/` before any modification
- BA2 DX10 archives are converted to GNRL format on repack (games accept both)

---

## Requirements

- Windows 10 / 11 x64
- [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0) (if not installed)
- [texconv.exe](https://github.com/microsoft/DirectXTex/releases/latest) — free Microsoft tool, configure path in the NTC panel

---

## Installation

1. Download the latest ZIP from [Releases](../../releases)
2. Extract anywhere (e.g. `C:\Supreme-RHI-NTC\`)
3. Run `RHI.exe`
4. Download `texconv.exe` and configure its path in the NTC panel

---

## Credits

- Original **RHI** app: [RankFTW](https://github.com/RankFTW/RHI) — GPL-3.0
- **texconv** / DirectXTex: Microsoft — MIT
- NTC extension: built with [Claude Code](https://claude.ai/code)

---

## License

GPL-3.0 — same as the original RHI project. See [LICENSE](LICENSE).
