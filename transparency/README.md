# See exactly what you're installing

Prometheus runs the **2021 (v463, 0.463.0.417004)** Roblox player and Studio. We don't build them from source: they are Roblox's own programs with a few small changes so they talk to prmths.lol instead of roblox.com. This folder shows every file we ship, where each one comes from, and every byte we changed, so you can check it yourself.

## Where the files come from

1. **Roblox's originals**, preserved on archive.org:
   - Player: [`funarchive/RobloxClient/0.463.0.417004.exe`](https://archive.org/download/funarchive/RobloxClient/0.463.0.417004.exe), sha256 `3349d6ef21d9393dc579154a489136cc68a3c193ee496810b92afc60bb61762c`
   - Studio (64-bit): [`WEAO-LIVE-WindowsStudio64-version-c993d5e9c7224b14.zip`](https://archive.org/download/weao-live-windows-studio-64-version-c-993d-5e-9c-7224b-14/WEAO-LIVE-WindowsStudio64-version-c993d5e9c7224b14.zip) (`RobloxStudioBeta.exe` inside), sha256 of the program `1f1e140be897ffc705e2daf6ea1575f102aff129c827f42ef57de86845b0a704`
2. **Rōblox Freedom Distribution (RFD)**, an open-source project that already patched these builds to run on private servers: release [`0.69.1-binaries`](https://github.com/Windows81/Roblox-Freedom-Distribution/releases/tag/0.69.1-binaries), files `v463.Player.7z` (sha256 `ca770f8da987071f63bdcf419da70c06b3fcf0493e0af9bbe96998eaad4aaa40`) and `v463.Studio.7z` (sha256 `3f6d91c515bb2dea8e638a877398f3e1688985a27a0a1cfcbdcb4f8f65fa9087`).
3. **Our own changes** on top of RFD, listed below.

## What's in this folder

| File | What it is |
|---|---|
| `2021-player/files-sha256.txt` | SHA-256 fingerprint of every file in the player download (2,344 files). |
| `2021-player/RobloxPlayerBeta.exe-changes.txt` | Every changed byte between Roblox's original player and ours. |
| `2021-studio/files-sha256.txt` | SHA-256 fingerprint of every file in the Studio download (7,379 files). |
| `2021-studio/RobloxStudioBeta.exe-changes.txt` | Every changed byte between Roblox's original Studio and ours. |
| `check-install.ps1` | Checks the player or Studio installed on your PC against these lists. |

## What we changed

**Player:** nothing beyond RFD. Our player download is byte-for-byte RFD's `v463.Player.7z` (all 2,344 files identical). RFD's changes to `RobloxPlayerBeta.exe` (59 places, 941 bytes) point the game at the website it's started for, switch off checks that only make sense on roblox.com (such as signature checks on join scripts), and load material textures from the website. For that last part RFD added one 512-byte section, `.rdata2`, at the end of the file. It holds only four texture addresses as text (`rbxassetid://rbxmtl-plastic-diffuse.dds` and similar); RFD's tool marked it executable, but there's no program code in it. Roblox's digital signature is still in the file but no longer matches, as with any changed file. The launcher writes one extra file on your PC, `AppSettings.xml` (the website address), and starts the game from `PrometheusPlayer.exe`, an exact copy of `RobloxPlayerBeta.exe`, so Discord doesn't show "Playing ROBLOX".

**Studio:** compared with RFD's `v463.Studio.7z`:
- `RobloxStudioBeta.exe` (37 places, 6,555 bytes vs. Roblox's original; see the changes file):
  - RFD's address and texture changes;
  - the registry names `RobloxStudio` → `PrmthsStudio` and `RobloxStudioBrowser` → `PrmthsStudioBrowser`, so Prometheus Studio never overwrites your real Roblox Studio's settings;
  - two of RFD's "skip login" changes undone (restored to Roblox's original bytes), because they crashed Studio when you sign in.
- `RobloxStudioRibbon.xml`: adds the Publish menu entries RFD left out.
- Added `Content/scripts/CoreScripts/RFDStarterScript.lua`: a 2-line script that turns on chat bubbles in Studio test servers.
- Added `Qml/QtQuick/LocalStorage/*`: restored from Roblox's original Studio; Studio's login screen needs it.
- Removed the third-party plugins RFD bundled (`Plugins/186568674.rbxm`, `1083345917.rbxm`, `1966664564.rbxm`, `Moon Animator.rbxm.bak`). Add the plugins you want yourself.

No new program code was added to either program: the changes replace existing bytes (mostly addresses, and checks switched off), plus the player's small text section described above. None of them read, send or store anything new.

## Check your own copy

1. Download this repository (Code → Download ZIP) and unzip it.
2. Open PowerShell in the `transparency` folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\check-install.ps1
   ```
   For Studio: add `-Kind 2021-studio`.
3. It prints how many files match exactly. Every file must match. It also lists files the launcher or the game created on your PC (settings, logs), which aren't part of the download.

To check the changes yourself, download the originals above and compare them byte by byte with your installed files; the `*-changes.txt` files list every offset.
