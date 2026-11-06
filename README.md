# Clipboard History

A lightweight, open-source clipboard manager for macOS and Windows. It keeps up to **500 entries or 250 MB** by default. Change either limit in **Actions → History Limits…**. Older entries are removed when a limit is reached. History stays on your device; the apps do not sync clipboard contents.

Download the apps from [GitHub Releases](https://github.com/Artemiy729/ClipboardHistory/releases/latest): macOS Universal (Apple Silicon and Intel, macOS 14+) and Windows x64 (standalone, no .NET installation required). The macOS build is ad-hoc signed and not notarized; Windows builds are unsigned.

![Clipboard History showing a searchable text history](docs/images/history-text.png)

![Clipboard History previewing an image from the local history](docs/images/history-image.png)

## Features

- Search and browse recent text and images.
- Recognize images by real thumbnails and read two-line text previews.
- Pin entries, mark favorites, filter favorites, and delete individual entries.
- Paste a selected entry into the app you were using.
- Paste plain text with `⌘⇧V` on macOS or `Ctrl+Shift+V` on Windows while history is open.
- Choose a global shortcut and set how many entries and how much storage to keep.
- Run from the macOS menu bar or Windows system tray.
- Keep history local and encrypted. No account or network connection is used.

Clipboard history can contain passwords and other private information. Clear it from **Actions → Clear History** when needed.

## Privacy controls

History expires after **24 hours** by default, in addition to the entry and storage limits. **Actions → Privacy Settings…** lets you set retention from 1 to 168 hours and add excluded apps (bundle IDs on macOS; process names without `.exe` on Windows). Expired entries and recognizable secrets in existing history are removed on startup and during use. Pinned entries are exempt from time-based expiration but still count toward the entry and storage limits; sensitive-content filtering still applies.

**Actions → Pause Recording** temporarily stops new captures and stays paused after a restart. Common password-manager apps and clipboard items marked private are excluded. Private-key blocks, common token formats, and authorization headers are also filtered. These checks cannot recognize every secret: pause recording before copying unmarked passwords or other sensitive data. Text inside screenshots is not inspected.

The **macOS app encrypts history and images with AES-256-GCM**, with the key stored separately in the login Keychain. The **Windows app uses current-user DPAPI protection** for its history, including image bytes and metadata. At the first successful launch of this version, existing plaintext history is migrated, verified, and removed from the app's storage folder. Encryption failure stops recording; the apps never fall back to plaintext storage. See [security and migration details](docs/security.md).

Encryption protects copied history files without the key or original Windows user credentials. It does not protect plaintext in the system clipboard or an app's memory, a compromised user session, or old plaintext backups and snapshots. DPAPI is user-bound, not app-bound: software running as the same Windows user may decrypt the archive. Copies made outside this app are not expired automatically. Thumbnails are kept only in bounded memory caches and are never written as plaintext image files.

## macOS

Press `⌘⇧Space` to open the history. Use `↑` and `↓` to select an entry, then press `Enter` to paste. `Esc` or the red window button hides the app. Open **Actions → Keyboard Shortcut…** to record a different global shortcut. The new shortcut must include `⌘`, `⌥`, or `⌃`. If another app already uses it, the old shortcut stays active.

Open **Actions → History Limits…** to set the entry limit (100–5000) and storage limit (40–2048 MB). The defaults are 500 entries and 250 MB. The history file and images live in `~/Library/Application Support/ClipboardHistory/`; file access is restricted to your macOS account.

Use **Pin**, **Favorite**, and the trash button above the preview, or right-click an entry. Pinned entries appear first. The star beside the type filter shows only favorites. Favorites follow the regular retention rules unless also pinned. Recopying an entry preserves its pin and favorite status. When a size/count limit is reached, unpinned entries are removed first; if only pinned entries remain, older pins are removed to respect the limit.

**Actions → Paste as Plain Text** (`⌘⇧V` while history is open) pastes the selected text without formatting. Text entries are already captured as plain text; this command makes that operation directly accessible and is unavailable for images.

**Actions → Startup & Permissions…** shows Accessibility and encryption status and lets you enable or disable launch at login. macOS may require approval in Login Items. Use a stable app location (normally `/Applications`) before enabling it. Quit older copies before starting a new build.

Direct paste requires Accessibility permission for the app in **System Settings → Privacy & Security → Accessibility**. Without it, the selected entry is copied to the clipboard so you can paste with `⌘V`.

Opening history immediately checks the clipboard, so a recent copy does not have to wait for the background timer. Automatic paste waits for the previous app to regain focus and for shortcut/Enter keys to be released. If the destination is unavailable or does not become ready within 1.5 seconds, the entry stays copied and a message explains how to paste manually. Reopening history cancels any pending automatic paste; paste keystrokes are never retried.

Build on macOS 14 or later with Apple Command Line Tools:

```sh
./build.sh
open build/ClipboardHistory.app
```

## Windows

The Windows app runs in the system tray. Press `Ctrl+Shift+Space` to open it. Use `↑` and `↓` to select an entry, then press `Enter` to paste. `Esc` hides the window. Choose **Actions → Keyboard Shortcut…** to record a different shortcut with `Ctrl` or `Alt`. If the shortcut is already taken, the previous one remains active.

Choose **Actions → History Limits…** to set the entry limit (100–5000) and storage limit (40–2048 MB). The defaults are 500 entries and 250 MB. The history and settings are stored in `%LOCALAPPDATA%\ClipboardHistory\`.

Use the footer buttons or right-click an entry to pin, favorite, or delete it. **★ Favorites** filters the list. Pins appear first and follow the same expiration/quota rules described above. **Actions → Start with Windows** shows whether startup is enabled. Store the executable in its permanent location before enabling startup.

Opening history immediately checks for a recent copy. Paste waits for the original destination and released shortcut keys; if it cannot proceed, a tray message offers manual `Ctrl+V`. Text is captured and pasted without rich formatting. Missing or damaged images are skipped individually at startup. If encrypted history cannot be authenticated, recording stops and the existing files are preserved; use **Actions → Retry Encrypted History** after resolving the cause. DPAPI normally requires the original Windows account and profile to decrypt the archive.

Build with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
dotnet build windows/ClipboardHistory.Windows.csproj -c Release
```

To publish a standalone x64 executable:

```powershell
dotnet publish windows/ClipboardHistory.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Build checks

The macOS build runs serialization, icon, privacy-filter, paste-readiness, encryption, migration, recovery, and retention self-checks with disposable fixtures and in-memory test keys. It does not access your Keychain or history. See [manual regression checks](docs/testing.md) for UI and Keychain checks. GitHub Actions builds both platforms on every push and pull request. The Windows UI still needs hands-on testing on Windows.

Windows CI also runs `ClipboardHistory.exe --self-test` against disposable DPAPI/storage fixtures and renders a demo screenshot. Both CI jobs package downloadable app archives. Hands-on testing of actual destination apps and login/startup behavior remains necessary.

## License

MIT. See [LICENSE](LICENSE).
