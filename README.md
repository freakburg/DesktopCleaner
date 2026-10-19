# Desktop Cleaner

A friendly Windows app that shows what is using your disk space and helps you clear it safely. It explains things in plain language, never deletes anything without your OK, and sends removed files to the Recycle Bin so you can get them back.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![.NET](https://img.shields.io/badge/.NET-10-512bd4) ![License](https://img.shields.io/badge/license-MIT-green)

## Download

Get **`DesktopCleaner.exe`** from the [latest release](../../releases/latest). It is a single file and needs nothing else installed. Put it somewhere permanent (for example `C:\Tools`) before turning on the weekly report or the right-click menu, because Windows remembers its location.

Windows may show a "SmartScreen" warning the first time, because the program is not code-signed. Choose **More info > Run anyway**. You can check the file against `SHA256SUMS.txt` on the release page, or build it yourself from this source.

A command-line version (`dcleaner.exe`) is also on the release page.

## What it does

| Tab | What it does |
|---|---|
| **Home** | Scans a folder and shows what is using space: by file type, by folder (with a drill-down bar view and a treemap "map"), and the biggest files. Shows what grew since your last check and suggests what to clear up. Reports can be saved as a printable page or CSV. |
| **Duplicate files** | Finds exact copies (size, then partial hash, then full SHA-256). You pick which copies to remove; one always stays. |
| **Similar photos** | Finds resized or re-saved copies using a perceptual hash, with thumbnails and a suggested best-quality keeper. |
| **Videos** | Finds every video (MP4, MKV, MOV, AVI and more) on your user folder, one folder or the whole computer. Filter by type and size, sort, play, show in folder, and delete the ones you pick. |
| **Downloads** | Flags old installers, ZIP archives and very large files for review. |
| **Temp files** | Lists leftovers in your temp folder, including empty folders programs leave behind, with an age filter. Files a program is using are skipped. |
| **App caches** | Clears browser and app caches (Chrome, Edge, Brave, Firefox, Discord, npm, pip, NuGet and more). Logins, history and bookmarks are never touched. |
| **OneDrive** | Sets big downloaded OneDrive files back to online-only ("Free up space"). Nothing is deleted. |
| **Empty folders** | Finds and removes empty folders. |
| **Installed apps** | Lists installed programs by size and opens Windows' Installed apps page. |
| **Weekly report** | Schedules a weekly dry-run report through Task Scheduler. It only writes a report. |
| **History** | Everything the app removed, with one-click restore from the Recycle Bin. |
| **Settings** | Folder right-click menu entry, low-disk-space warning. Light and dark mode follow Windows. |

## Safety model

- Nothing is removed without a confirmation. Review lists start with nothing selected (duplicates select the extra copies, and always keep one).
- Files go to the **Recycle Bin**. The exception is app caches, which rebuild themselves, so they are deleted directly. This is stated in the app.
- Before any removal, each file is re-checked (size, time, and content hash for duplicates) in case it changed after the scan.
- Windows, Program Files and standard user folders are protected. Automatic rules refuse personal folders (Documents, Pictures, Desktop) and cloud-sync folders.
- Files that exist only in the cloud (OneDrive online-only) are never opened, so they are never downloaded by accident.
- Every action is logged to `%LOCALAPPDATA%\DesktopCleaner\activity.jsonl`.
- Nothing is uploaded. Photos and files are only read on your PC.

## Command line

```
dcleaner dupes <folder> [--min-size <bytes>] [--delete-extras]
dcleaner photos <folder> [--strict]
dcleaner cleanup [--apply] [--report]
dcleaner videos [folder] [--all-drives] [--min-mb N] [--type .mp4]
dcleaner temp [--days 7] [--apply]
dcleaner downloads [folder] [--days 30] [--large-mb 500] [--remove]
dcleaner caches [--apply]
dcleaner storage <folder> [--top 10] [--threads N]
dcleaner onedrive [--min-mb 5]
dcleaner empty <folder> [--remove]
dcleaner apps
dcleaner history
dcleaner restore "<full path>"
dcleaner schedule [--day MON] [--time 09:00] | schedule --remove
```

Commands that remove things are dry runs unless you pass `--apply` or `--remove`.

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build DesktopCleaner.sln
dotnet test tests/DesktopCleaner.Tests
dotnet run --project src/DesktopCleaner.App
```

Single-file release build:

```
dotnet publish src/DesktopCleaner.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Project layout

- `src/DesktopCleaner.Core` – the scanning and cleanup engine (no UI): duplicate finder, storage analyzer (parallel, with a squarified treemap layout), photo similarity, cleanup rules, safe removal, Recycle Bin restore, scheduling.
- `src/DesktopCleaner.App` – the WPF interface.
- `src/DesktopCleaner.Cli` – the command-line tool, built on the same engine.
- `tests/DesktopCleaner.Tests` – xUnit tests.

Notes on the engineering: scans run off the UI thread with cancellation and progress; locked and unreadable files are skipped and reported rather than failing the scan; OneDrive placeholders are handled explicitly (the reparse-point flag that marks them is hidden from .NET programs, so links are detected by their target instead).

## Limitations

- Windows only. HEIC and WebP photos are not compared yet.
- The scan reads the directory tree; it does not use the NTFS file index, so very large drives take longer than tools such as WizTree.
- Windows does not record when a program was last used, so the installed-apps list shows size and install date only.

## License

[MIT](LICENSE)




