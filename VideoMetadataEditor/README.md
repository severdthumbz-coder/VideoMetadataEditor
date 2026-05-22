# Video Metadata Editor

A production-ready, portable WPF desktop application for editing and batch-renaming video files with embedded metadata.

---

## Quick Start

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows)
- Windows 10/11 (WPF requires Windows)

### Build & Run

```bash
# Clone / extract this project, then:
cd VideoMetadataEditor
dotnet restore
dotnet run
```

### Publish (portable single-EXE)
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish
```

---

## Features

| Feature | Details |
|---|---|
| **File Input** | Drag-and-drop, file browser, folder scan |
| **Formats** | MP4, MKV, MOV, WMV, M4V, WebM |
| **Metadata APIs** | TMDB, OMDB (configurable API keys) |
| **Lookup** | By title search or IMDB/TMDB ID |
| **Embed** | Title, Year, Genre, Director, Cast, Description, Cover Art |
| **Artwork** | Drag-drop image, auto-compress to 500px JPEG 85% |
| **Rename** | Customizable pattern: `{Title} ({Year})` |
| **Batch** | Concurrent processing with progress tracking |
| **Per-File** | Change caching when switching between files |
| **Format Guard** | Disabled fields + warnings per format capability |
| **Themes** | Dark and Light mode with one-click toggle |
| **Portable** | `config.json` stored in app root directory |

---

## API Keys

You need free API keys for metadata search:

| Service | Get Key | Used For |
|---|---|---|
| **TMDB** | https://www.themoviedb.org/settings/api | Primary metadata source |
| **OMDB** | https://www.omdbapi.com/apikey.aspx | Secondary / IMDB data |

Enter keys in the **Settings** tab — they are saved to `config.json` in the app folder.

---

## Rename Pattern Tokens

| Token | Example Output |
|---|---|
| `{Title}` | `The Shawshank Redemption` |
| `{Year}` | `1994` |
| `{Genre}` | `Drama` |
| `{Director}` | `Frank Darabont` |
| `{ImdbId}` | `tt0111161` |
| `{TmdbId}` | `278` |

Default pattern: **`{Title} ({Year})`** → `The Shawshank Redemption (1994).mp4`

---

## Format Capabilities

| Format | Full Tags | Artwork | Notes |
|---|---|---|---|
| MP4 | ✅ | ✅ | Best support |
| MKV | ✅ | ✅ | Best support |
| M4V | ✅ | ✅ | Best support |
| MOV | ✅ | ✅ | Compatibility varies by player |
| WMV | ⚠️ | ✅ | Limited field support |
| WebM | ❌ | ❌ | Minimal tag support |

---

## Config File

`config.json` is stored in the same folder as the EXE (portable mode):

```json
{
  "TmdbApiKey": "...",
  "OmdbApiKey": "...",
  "IsDarkTheme": true,
  "ShowSplashScreen": true,
  "RenamePattern": "{Title} ({Year})",
  "MaxConcurrentProcessing": 4,
  "ArtworkMaxPx": 500,
  "ArtworkJpegQuality": 85
}
```

---

## Custom App Icon

Replace `Resources/app.ico` with your own `.ico` file, then rebuild.

---

## Architecture

```
VideoMetadataEditor/
├── App.xaml / App.xaml.cs         — Startup, theme switching
├── Views/
│   ├── SplashScreen.xaml/.cs      — 2-second splash with progress
│   └── MainWindow.xaml/.cs        — Main UI (4-tab layout)
├── ViewModels/
│   └── MainViewModel.cs           — All UI logic, commands, state
├── Models/
│   └── Models.cs                  — AppSettings, VideoFile, MovieMetadata
├── Services/
│   ├── ConfigService.cs           — config.json read/write
│   ├── MetadataService.cs         — TagLib# read/write, format detection
│   ├── ApiService.cs              — TMDB + OMDB HTTP calls
│   └── FileRenameService.cs       — Pattern-based rename
├── Themes/
│   ├── DarkTheme.xaml             — Dark color scheme
│   ├── LightTheme.xaml            — Light color scheme
│   └── CommonStyles.xaml          — All control styles + converters
├── Converters/
│   └── Converters.cs              — WPF IValueConverter implementations
└── Resources/
    └── app.ico                    — Replaceable application icon
```

---

## Dependencies (NuGet)

| Package | Version | Purpose |
|---|---|---|
| `TagLibSharp` | 2.3.0 | Read/write video file metadata |
| `Newtonsoft.Json` | 13.0.3 | Config JSON serialization |
| `CommunityToolkit.Mvvm` | 8.3.2 | MVVM helpers |
| `Microsoft.Extensions.Http` | 8.0.0 | HttpClient factory |

---

## License
MIT — free to use, modify, and distribute.
