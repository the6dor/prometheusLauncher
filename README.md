# Prometheus Launcher

The official launcher for [Prometheus](https://www.prmths.lol). Download it at https://www.prmths.lol/download.

## What it does

- **Install:** run it once. It installs itself for your Windows user (no administrator rights) and registers the `prometheus-player://` link that the website's **Play** button opens.
- **Play:** when you press Play, it downloads the right player for the game's year (2021 for now), checks the download's SHA-256 fingerprint against the website's manifest, and starts the game.
- **Updates itself:** after a game starts, it compares itself with the website's copy and swaps in the new version for the next start.
- **Discord:** while a game is open, it shows "Playing Prometheus" in Discord with the game's name and picture. Nothing is sent if Discord isn't running.

The website address is built in, so a Play link can't point the launcher at another server.

## Building

Needs the .NET SDK. The launcher targets .NET Framework 4.8, which ships with Windows 10 and 11.

```
dotnet build -c Release
```

The result is `bin/Release/net48/PrometheusLauncher.exe`.

`package-client.ps1` is the script that packages a player folder into the zip, manifest and per-file fingerprint list that the launcher downloads.
