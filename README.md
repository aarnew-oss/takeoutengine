# 📸 TakeoutEngine (.NET 10)

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![ExifTool](https://img.shields.io/badge/ExifTool-13.59+-2E8B57?logo=target)](https://exiftool.org/)
[![Spectre.Console](https://img.shields.io/badge/Spectre.Console-0.57+-F7931E)](https://spectreconsole.net/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**TakeoutEngine** is a high-performance, resilient .NET 10 CLI application designed to transform raw, messy Google Takeout photo archives (tens to hundreds of gigabytes) into a beautifully organized, perfectly tagged photo and video library ready for your NAS or personal cloud.

---

## ✨ Features

- **🪄 Interactive Setup Wizard:** Simply run `dotnet run` without arguments for a guided, step-by-step terminal wizard with folder validation and sensible defaults.
- **⚡ High-Throughput ExifTool Worker Pool:** Keeps persistent ExifTool processes open via `-stay_open True -@ -` to avoid process startup overhead on tens of thousands of files.
- **🕒 QuickTime & MP4 UTC Integrity:** Configured with `-api QuickTimeUTC` to guarantee video timestamps stay in sync with photos across Apple Photos, VLC, and Windows Media Player.
- **🍏 Live Photos & Motion Photos Pairing:** Automatically links Apple Live Photos and Android Motion Photos (`.heic`/`.jpg` + `.mp4`/`.mov`), sharing metadata and timestamps between still and video files.
- **🔍 Modern Google Takeout Metadata Pairing:** Full empirical support for modern Takeout naming quirks:
  - `.supplemental-metadata.json`, `.supplemental-meta.json`, `.supplemen.json`, `.suppl.json`
  - Numbered duplicates like `IMG_001(1).jpg` pairing with `IMG_001.jpg.supplemental-metadata(1).json`
  - Truncated prefixes and double-dot filenames (`sstg-xxx.mp4..json`)
- **🚀 Hardware-Accelerated Deduplication:** Computes 64-bit checksums using `System.IO.Hashing.XxHash64` with shared `ArrayPool<byte>` buffers, prioritizing instances that carry JSON sidecars before dropping duplicates.
- **📱 Full Samsung & Android Media Support:** Handles Samsung Galaxy naming conventions (`YYYYMMDD_HHmmss.jpg`, `Screenshot_YYYYMMDD-HHmmss...`), Motion Photos with embedded or split micro-videos, and specialized formats like `.dng` (Samsung Expert RAW), `.3gp` (legacy Android video), and `.webm`.
- **🏷️ Album Preservation & EXIF Keywords:** Automatically detects user albums and writes them to EXIF/IPTC/XMP (`Keywords` and `Subject`). When imported into Synology Photos, Immich, Apple Photos, or Nextcloud, your Google Photos albums are instantly recognized!
- **⭐ Star Rating & Favorites Sync:** Preserves Google Photos favorites by writing `-Rating=5` and `-RatingPercent=100` into EXIF metadata.
- **📋 Duplicate Transparency Log:** Generates an audit log (`duplicates.csv`) recording every duplicate dropped, the hash, and the kept file.
- **🌐 Full UTF-8 Archive Decompression:** Preserves international characters in folder and file names during extraction.
- **📊 Rich Terminal UI:** Beautiful animated progress bars, live ETA, throughput counters, spinners, and summary tables powered by [Spectre.Console](https://spectreconsole.net/).

---

## 🛠️ Prerequisites

1. **.NET 10 SDK** (version 10.0.401 or newer)  
   Install via Windows Package Manager:
   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```
2. **ExifTool** (version 12.0+ or newer)  
   Install via Windows Package Manager:
   ```powershell
   winget install OliverBetz.ExifTool
   ```

Verify both tools in your terminal:
```powershell
dotnet --version
exiftool -ver
```

---

## 🚀 Quick Start

### 1. Interactive Wizard Mode (Recommended)

Just run the application without arguments:
```powershell
dotnet run
```
TakeoutEngine will launch an interactive wizard:
1. Prompts for the folder containing your downloaded Takeout `.zip` files (validates directory and zip count).
2. Prompts for the target output folder (defaults to `Organized_Photos` on the same drive).
3. Prompts for the temporary staging folder (defaults to `takeout_staging` on the input drive to safeguard disk space).
4. Configures parallel worker processes (defaults to CPU cores / 2).
5. Displays a confirmation summary table before processing.

---

### 2. Headless CLI Mode

For scripts, automation, or power users:

```powershell
dotnet run --configuration Release -- `
  --input "D:\GoogleTakeoutDownloads" `
  --output "D:\PhotoLibrary_Organized" `
  --temp "D:\takeout_staging" `
  --workers 6
```

#### CLI Options:

| Option | Type | Description |
| :--- | :--- | :--- |
| `--input <path>` | Required | Directory containing downloaded Takeout `.zip` archives. |
| `--output <path>` | Required | Destination directory for the organized library. |
| `--temp <path>` | Optional | Staging directory (defaults to `takeout_staging` on input drive). |
| `--workers <int>` | Optional | Number of concurrent ExifTool worker processes (defaults to CPU cores / 2). |
| `--clean-staging` | Flag | Delete staging directory when finished (default: false, preserves raw data). |
| `--dry-run` | Flag | Simulation mode: analyze, date, and deduplicate without moving files or writing tags. |
| `--resume` | Flag | Skip re-extracting completed zips if staging files already exist. |
| `--interactive`, `-i` | Flag | Force launching the interactive setup wizard. |
| `--help`, `-h` | Flag | Display CLI options and usage information. |

---

## 📂 Output Folder Structure

Media files are organized by year and month, with standardized timestamps applied to the file names and file system timestamps (`CreationTimeUtc` & `LastWriteTimeUtc`):

```
D:\PhotoLibrary_Organized\
├── 2021/
│   ├── 07/
│   │   ├── 20210718_143000_IMG_4501.jpg
│   │   └── 20210725_091522_VID_20210725_091522.mp4
│   └── 12/
├── 2025/
│   ├── 02/
│   │   ├── 20250228_154231_IMG_1237.HEIC
│   │   └── 20250228_154231_IMG_1237.MP4  <-- Paired Live Photo!
│   └── 03/
│       ├── 20250301_152922_IMG_1288.HEIC
│       └── 20250301_152922_IMG_1288.MP4
└── _Manual_Review/  <-- Only if a file cannot be reliably dated anywhere
    ├── manual_review.csv
    └── unknown_date.jpg
```

---

## 🧠 Automated Multi-Tier Date Resolution Pipeline

Every file is evaluated through an ordered cascade to determine its date and time:

1. **`JsonSidecar`**: Reads exact `photoTakenTime.timestamp` from matching `.supplemental-metadata.json`, `.supplemen.json`, or `.json` files.
2. **`LivePhotoPairing`**: If a video (`.mp4`/`.mov`) lacks a sidecar, inherits the sidecar and timestamps from its matching still image (`.heic`/`.jpg`).
3. **`FilenameDate`**: Regex parser recognizing standard timestamp formats (`YYYYMMDD_HHmmss`, `YYYY-MM-DD`, WhatsApp `IMG-YYYYMMDD-WA...`).
4. **`FilenameUnixEpoch`**: Regex recognizing 13-digit millisecond or 10-digit second Unix timestamps (filtering out random non-timestamp IDs like Snapchat).
5. **`FolderExactDate`**: Parent directory containing a full date (e.g., `Photos from 2018-07-20`).
6. **`FolderYearOnly`**: Parent directory containing a year (e.g., `Summer Vacation 2017`). Defaults to July 1 at noon and places in `YYYY/_Estimated_Year/`.
7. **`Failed`**: Quarantined to `_Manual_Review/` and logged to `manual_review.csv`.

---

## 📈 Real-World Benchmark

Tested on a real Google Takeout dataset:
* **Input Volume:** 36 zip files (~75 GB compressed / ~71 GB uncompressed)
* **Total Media Files:** 23,443 files
* **Duplicates Dropped:** 912 identical copies
* **Unique Files Processed:** 22,531
* **Execution Time:** ~21 minutes (on SSD with 6 workers)
* **Success Rate:** **100.0%** (0 files quarantined)

---

## ⚠️ Disclaimer

TakeoutEngine is an independent open-source utility created to assist users in organizing and preserving their personal photo archives.

- **Non-Affiliation:** This project is independent and is not affiliated with, authorized, maintained, sponsored, or endorsed by Google LLC or Alphabet Inc. Google, Google Photos, and Google Takeout are registered trademarks of Google LLC.
- **Backup Recommendation:** Always keep a separate, secure backup of your original Takeout archives or media before performing automated file operations on precious memories.
- **"AS IS" Warranty:** The software is provided under the [MIT License](LICENSE) "as is", without warranty of any kind, express or implied. The authors and contributors are not liable for any data loss, corruption, or damages resulting from the use of this software.
- **Third-Party Dependencies:** TakeoutEngine utilizes [ExifTool](https://exiftool.org/) by Phil Harvey for low-level metadata extraction and tagging.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

