# debrid-dl

A small Windows download manager for [AllDebrid](https://alldebrid.com) users: paste or copy links, and it
downloads them over many connections at once, resumes where it stopped, and updates itself.

The interface is in French.

## Install

Download `DebridDl-win-Setup.exe` from the [latest release](https://github.com/StyuDEV/debrid-dl/releases/latest)
and run it. It installs for the current user (no admin rights) and adds a Start menu shortcut.

Updates are checked at launch and every 6 hours. When one is ready, a **Mettre à jour** button appears;
otherwise it is applied when the window closes. Running downloads are paused, then resumed after the restart.

## Usage

1. Open **Réglages** (gear icon) and paste your AllDebrid API key, created at
   [alldebrid.com/apikeys](https://alldebrid.com/apikeys/).
2. Paste links in the box, one per line, or just copy them anywhere: links from AllDebrid-supported hosts are
   picked up from the clipboard.
3. Choose the folder and click **Télécharger**.

Each file shows its progress, speed and time left, and can be paused, resumed, or removed.

| Setting | Default | Meaning |
|---|---|---|
| Connexions par fichier | 16 | Parallel connections per file |
| Fichiers en même temps | 2 | Files downloaded simultaneously |
| Récupérer les liens copiés | on | Watch the clipboard for links |

## How it works

- A file is split into 4 MB chunks; each connection takes the next missing chunk.
- Progress is written next to the file (`name.telech` and `name.telech.json`) every 3 seconds, so a crash
  loses at most the chunks in flight. The finished file is renamed into place.
- Links are unlocked through the AllDebrid API. When a direct link expires (HTTP 401, 403, 404 or 410), the
  original link is unlocked again and the download continues.
- Settings and the queue live in `%APPDATA%\debrid-dl\settings.json`. The API key never leaves your machine
  except to call `api.alldebrid.com`.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build src/DebridDl -c Release
```

Pushing a `v*` tag builds the installer and publishes the release with
[Velopack](https://velopack.io) (see `.github/workflows/release.yml`).

## License

[MIT](LICENSE)
