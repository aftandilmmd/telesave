# TeleSave for Windows

.NET 10 + Avalonia 11 port of TeleSave, with the same behaviour as the macOS app.

## Layout

| Project | Contents |
|---|---|
| `src/TeleSave.Core` | Platform-neutral rules, ported from the Swift Core with the same behaviour: upload queue, retries / FLOOD_WAIT, intake (zip / expand folders), chat-file search and filters, accent-insensitive search, remote config (Ed25519), update check, translations. |
| `src/TeleSave.TdLib` | TDLib JSON interface over P/Invoke (`tdjson.dll`), Telegram client, API key validator. Same TDLib version and requests as macOS. |
| `src/TeleSave.UI` | Avalonia views and the view model (`AppModel`). Cross-platform, so it can also be rendered headless and reused for Linux. |
| `src/TeleSave.App` | Windows host: tray icon, panel window (taskbar-button mode), edge drop strip, Explorer "Send to TeleSave", single instance, DPAPI secrets, toasts, autostart. |
| `src/TeleSave.Smoke` | Checks TDLib on Windows: reaches the login step and gets `API_ID_INVALID` from Telegram for a dummy key. |
| `tests/TeleSave.Core.Tests` | Core tests (also run on macOS/Linux). Includes a check that every `L10n.T("…")` key exists in every language file. |
| `tests/TeleSave.UI.Tests` | Headless UI tests: render the panel in each state to `snapshots/*.png`. |

Translations are the macOS app's `Sources/TeleSaveCore/Resources/Localization/*.json`, linked into the build: a new language file serves both apps.

## Build and test

```sh
dotnet test windows/tests/TeleSave.Core.Tests
dotnet test windows/tests/TeleSave.UI.Tests      # writes tests/TeleSave.UI.Tests/snapshots/*.png
```

The app itself needs `tdjson.dll` and its OpenSSL / zlib DLLs in `windows/native/win-x64` (or `win-arm64`). CI builds them from the same TDLib commit as the macOS app ([.github/workflows/windows.yml](../.github/workflows/windows.yml)); download the `tdjson-win-x64` artifact from a run, or build TDLib with vcpkg as the workflow does. Then:

```powershell
dotnet run --project windows/src/TeleSave.Smoke -r win-x64
dotnet publish windows/src/TeleSave.App -c Release -r win-x64 --self-contained -o windows/publish/win-x64
```

## Releases

Publishing a GitHub release runs the workflow, which builds x64 and ARM64, runs the smoke test on both, and attaches `TeleSave-<v>-windows-<arch>-setup.exe` (Inno Setup, per-user, no admin) and a portable `.zip` to the release. macOS and Windows share version numbers; each app's update check only reports releases that contain a download for its own platform.

Optional repository secrets `TG_API_ID`, `TG_API_HASH`, `TG_CONFIG_URLS`, `TG_CONFIG_PUBLIC_KEY` bundle an API key and the remote config into the build, the same as `Config/Secrets.env` on macOS.
