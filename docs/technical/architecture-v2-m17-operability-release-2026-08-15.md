# Architecture v2: M17 operability and release delivery

Status: implemented

## Claude hybrid scheduling

The Claude provider uses two deliberately different schedules:

- direct CLI refresh runs at startup and after every normal direct success on the five-minute healthy interval;
- a new valid `ClaudeStatusLine` observation invalidates the former direct wake and creates a new reconciliation wake fifteen minutes later;
- if the interactive Claude Code session keeps emitting status-line data, every event postpones the costly direct CLI call again;
- if no event arrives for fifteen minutes, one direct request rechecks the data, after which the ordinary five-minute fallback continues until a new status-line push appears.

`WakeId` makes a displaced timer inert, and an active attempt still stays single-flight. This keeps direct CLI autonomous when Claude Code is idle, but avoids polling it every five minutes while the low-latency channel is alive.

## Diagnostics

The tray now provides **«Диагностика источников»**. The window projects the global domain state into a presentation view model and shows, for each provider:

- freshness, aggregate health, last valid data time;
- pipeline phase and next scheduled refresh;
- normalized remaining limits and reset times;
- individual transport health, last success, and typed error code;
- cache health and last write.

It can request a manual refresh and open the rotated JSONL logs. It never displays raw CLI/statusLine output or credentials.

## Windows autostart

The tray option **«Запускать с Windows»** writes only the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\LLMLimitsWidget` entry. No administrator permission is required. The command launches the current widget executable with `--autostart`; that mode does not steal focus on sign-in. Disabling removes only a Run entry that belongs to this widget and leaves unrelated entries intact.

The preference is persisted in `widget-settings.json`. If it is enabled, startup refreshes the executable path in the registry — useful after installing a newer version.

## Release delivery

`scripts/publish-release.ps1` produces a portable `win-x64` release under `artifacts/publish`. The publish target explicitly copies the independent Claude statusLine bridge into the release output, so the configured fast channel remains deployable.

`packaging/LLMLimitsWidget.iss` builds an Inno Setup installer from that published directory. Installation intentionally does not force autostart; the user chooses it from the tray.

GitHub Actions:

- `CI` runs all domain, application, provider, infrastructure, presentation, architecture, and bridge tests on `windows-latest`; it also publishes and stores the portable build artifact.
- `Release` runs for `v*` tags (or dispatch), builds the portable ZIP and Inno Setup installer, then creates the GitHub release with both artifacts.

## Verification

- Domain test T-013 proves a valid Claude statusLine defers direct reconciliation for 15 minutes and makes stale wake identities harmless.
- Infrastructure tests cover safe current-user autostart registration and preserve unrelated Run entries.
- Presentation test V-009 proves diagnostics consume normalized domain state rather than raw provider data.
