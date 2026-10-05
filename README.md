# Meme Digest

Draw random memes from your media library, review them in a gallery, keep the good ones and share them straight to Discord — no pendrive sprints between folders.

![.NET](https://img.shields.io/badge/.NET-8.0-blue) ![UI](https://img.shields.io/badge/UI-WPF-purple) ![CI](https://github.com/zero3growlithe/Meme-Digest/actions/workflows/build.yml/badge.svg)

## What it does

- **Random draw** — picks memes from a configurable library root (subdirectories scanned recursively). Draw count is configurable (1–200). Both images and videos are supported; the mix is balanced whenever both kinds are available.
- **Gallery review** — tiles show thumbnails (video tiles show a poster frame). Click a tile to open it in a **full-window viewer** (image or playing video), navigate with arrow keys, then **Keep ❤** it or **Discard ✕** it.
- **Slot refill** — every discarded/kept tile is replaced by a fresh random meme automatically, until the library runs out of unseen memes.
- **Per-user profiles** — each user gets their own draw feed and their own picked/discarded history, so nobody's "already seen" pool interferes with anyone else's.
- **No repeats** — the picker excludes every meme already picked or discarded *by the current profile*.
- **Human-readable history** — per-profile text files (`history-<profile>.txt`) listing when each meme was picked/discarded and its path relative to the library root. Plain text — open in Notepad, edit freely (formatting is re-parsed on load).
- **Share, not run around:**
  - **Copy files to clipboard** — paste directly into a Discord chat with Ctrl+V.
  - **Reveal in folders** — opens Explorer and multi-selects the picked memes in their source folders so you can drag them anywhere.
  - **Move… / Copy to folder…** — batch move or copy selected memes into a folder of your choice (collision-safe naming; moved memes are marked picked and their slots refill).

## Supported formats

Images: `png`, `jpg`, `jpeg`, `gif`, `bmp`, `webp`, `tiff` — via WIC.
Videos: `mp4`, `m4v`, `mkv`, `webm`, `mov`, `avi`, `wmv`, `mpeg`, `mpg` — full-window playback uses the Windows codec set (MediaElement); **preview posters require [ffmpeg](https://ffmpeg.org/download.html)** (see Settings). Without ffmpeg video tiles fall back to a placeholder badge — the gallery still works and full-window playback for common codecs still applies.

Formats are editable in Settings, so exotic formats (`avif` images, `flv` clips, …) can be added without a rebuild.

## Getting started

1. Grab `MemeDigest-win-x64.zip` from the latest [Actions run](https://github.com/zero3growlithe/Meme-Digest/actions) (artifact) — requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Run `MemeDigest.exe`. First launch opens **Settings**.
3. Point **Meme library root** at your meme collection, adjust the rest if needed, OK.
4. **Draw memes** → review → Keep/Discard → select favorites → copy to clipboard into Discord.

### Settings reference

| Setting | Meaning |
| --- | --- |
| Meme library root | Root folder scanned recursively for media files |
| History folder | Where per-profile `history-*.txt` files live |
| Thumbnail cache folder | Where ffmpeg video posters are cached |
| ffmpeg executable | Path to `ffmpeg.exe` (optional, video previews only) |
| Memes per draw | Gallery size for one draw (1–200) |
| Image / Video extensions | Comma-separated lists of accepted extensions |

Defaults live under `%LocalAppData%\MemeDigest\` (settings.json, History, Thumbnails). Crash log: `%LocalAppData%\MemeDigest\crash.log`.

### Profiles

Switch profiles from the toolbar combo; **Add profile** creates one (own history file, own seen-pool), **Remove** keeps the history file on disk for later. Profile names become part of a file name — keep them filesystem-friendly.

## Privacy

Everything is local: the library, the history files, the thumbnail cache. Nothing leaves your machine.

## Building from source

Any .NET 8 SDK on Windows:

```
dotnet build src/MemeDigest/MemeDigest.csproj -c Release
```

CI builds on every push to `main` and attaches `MemeDigest-win-x64.zip` as a run artifact.

## License

MIT — see [LICENSE](LICENSE).