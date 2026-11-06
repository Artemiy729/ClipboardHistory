# macOS regression checks

Build with `./build.sh`. The build includes `--self-test`, which checks serialization,
image encoding, privacy filters, and paste readiness: delayed activation, held keys,
focus loss, timeout, terminated destination, and prevention of a second paste.
It also checks authenticated encryption, nonce uniqueness, wrong keys, modified
ciphertext, encrypted image cleanup, interrupted migration, lost-key behavior,
recovery around damaged images, concurrent-writer rejection, backwards-compatible
metadata decoding, pin ordering, retention, and quotas. All storage fixtures use
an isolated temporary directory and disposable in-memory keys. These checks do
not read your history or Keychain, inject keyboard events, or change the clipboard.

Run `open build/ClipboardHistory.app` after quitting other copies of Clipboard History.
For automatic paste, grant Accessibility to the exact app being tested. A locally
rebuilt, ad-hoc signed app may need its Accessibility permission granted again.

## Manual checks

Use disposable text in TextEdit and a browser text field. Repeat the normal paste
flow several times and confirm that each selection produces exactly one insertion.

| Scenario | Expected result |
| --- | --- |
| Copy text and immediately press ⌘⇧Space | The new text is at the top, search has keyboard focus, Enter pastes into the original field. |
| Open from the menu bar | The destination is the previously active application, not Clipboard History. |
| Hold ⌘⇧Space briefly while opening, then release and press Enter | Shortcut modifiers do not leak into the paste. |
| Hold Enter for about half a second after selecting an entry | Paste happens once after release. |
| Hold a modifier or Enter longer than 1.5 seconds | Automatic paste is cancelled with a manual-paste message; no late insertion occurs. |
| Use a destination on another Space or in full screen | Paste waits for activation; on timeout the entry remains available for ⌘V. |
| Quit the destination while history is open | The app explains that the entry was copied; no keystroke is sent to another app. |
| Reopen history while an automatic paste is pending | The old paste is cancelled. |
| Change the clipboard from another app during the focus wait | The pending paste is cancelled instead of inserting different content. |
| Revoke Accessibility and select an entry | The app explains manual ⌘V and the required permission. |
| Select a saved image whose file is missing | An error is shown, history stays open, the clipboard is not cleared. |
| Select an image that is available | The image pastes into an image-capable destination. |
| Scroll through text and images | Images show real thumbnails with correct proportions; text uses two-line previews; selection, search, and double-click still work. |
| Copy from an excluded app, or pause recording before copying | The entry is not recorded, including when opening history immediately. |
| Press Esc, then open history from another application | The next paste targets the new application. |
| Pin and favorite an entry, recopy it, then restart | One entry remains, with both flags preserved and pins ordered first. |
| Toggle the star beside the type filter | Only favorites appear; selection and preview follow the filter. |
| Delete an individual text/image entry, then restart | The entry stays deleted; other entries remain. |
| Select text and press ⌘⇧V | Only plain text is pasted into the original field. |
| Open Startup & Permissions and change launch at login | The OS status is shown; registration changes only on Save; approval is opened if needed. |
| Cancel Startup & Permissions | Launch-at-login registration is unchanged. |

## Keychain and migration (hands-on)

Use a disposable macOS user account with synthetic history for failure and
tamper checks; do not delete or modify your real history or encryption key.

1. Run the old version, copy synthetic text and an image, then quit it. Launch
   the new app. Allow the app's Keychain request if one appears. Confirm text
   and images remain usable and `history-v3.enc`/`encrypted-images/` replace
   the plaintext archives and old image directory.
2. Restart the same build. Confirm it reuses the Keychain key and history.
3. In the disposable account only, deny Keychain access or remove the test key.
   Confirm recording stops, an error is visible, and ciphertext is preserved.
   Restoring access and choosing **Retry Unlock** should reload it. A deleted
   key must be restored from the original; generating a new key cannot recover it.
4. In that account only, alter one encrypted image while the app is stopped.
   On restart, confirm a recovery notice appears and the text/other images stay.
5. Alter the encrypted index instead. The app must stop recording and preserve
   the damaged file instead of opening an empty history or loading plaintext.
6. After rebuilding, verify any renewed Keychain and Accessibility prompts
   apply to the app you intended to launch. Do not allow unrelated applications.

Keychain ACL prompts, launch at login across an actual logout/login, and the
destination application's handling of ⌘V require interactive testing. Automated
storage tests deliberately avoid touching the real Keychain and user history.

App activation does not guarantee that the destination has an editable field.
Verify that a field is focused before opening history. The app sends one ⌘V and
cannot confirm whether the destination consumed it; it deliberately does not retry.

## Windows

Build with `dotnet build windows/ClipboardHistory.Windows.csproj -c Release`.
Run `windows/bin/Release/net10.0-windows/ClipboardHistory.exe --self-test` on
Windows. It tests DPAPI round trips, randomized ciphertext, tampering, migration,
corrupt-image recovery, failed-migration preservation, concurrency, thumbnail
dimensions, pin retention/quotas, privacy flags, paste readiness, and the native
`INPUT` layout. Fixtures use a disposable temporary directory. It does not read
real history, change startup registration, or inject keys.

`ClipboardHistory.exe --demo --screenshot=C:\\Temp\\clipboard-demo.png` renders
synthetic entries without loading real history or recording clipboard contents.
CI renders this demo and uploads it with the build artifacts. The screenshot
mode exits automatically.

Repeat the focus, quick copy/open, one-paste, privacy, favorite, pin, deletion,
and image checks above using `Ctrl+Shift+Space` and `Ctrl+Shift+V`. Check scrolling
and keyboard selection at 100%, 150%, and 200% DPI. Verify startup after a real
logout/login and manual `Ctrl+V` fallback when the destination runs elevated.
In a disposable Windows account, test migration from the old JSON, deliberate
archive damage, and inability to decrypt from another user account. These
interactive behaviors cannot be established by the macOS cross-build alone.
