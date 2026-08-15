# Limits domain and widget data flow

Status: accepted implementation foundation

## Goal

Keep provider communication independent from the WPF overlay. Codex and Claude adapters must be replaceable without changing the visual controls, window placement, ghost mode, or persistence code.

## Boundaries

```text
provider transport / parser
        |
        v
ILimitsDataSource
        |
        v
LimitsCoordinator -- SnapshotChanged --> MainWindow
        |                                   |
        v                                   v
  LimitsSnapshot                    ProviderRowControl
```

The domain uses no WPF types. A provider returns a `ProviderLimitsSnapshot` containing one or more `LimitWindowSnapshot` values. Percentages are remaining percentages from 0 to 100. A reset is represented by `ResetAt`, not by a preformatted string, so the widget can format it for the user's locale and update it without coupling the provider to presentation.

## Coordinator behavior

- `LimitsCoordinator` deduplicates sources by provider and polls them independently every 30 seconds.
- The first refresh runs when the widget loads; a forced refresh from the context menu uses the same path.
- Provider reads run concurrently. A failure from one source does not block the other source.
- If a provider has a previous successful snapshot, a later failure keeps that data as `Stale` and records the error. If there is no previous data, the provider is `Unavailable`.
- Values are normalized at the domain boundary: percentages are clamped to 0..100 and missing values remain missing instead of becoming a misleading zero.
- `SnapshotChanged` is the only hand-off from the coordinator to the UI. The window marshals that event to the WPF dispatcher and updates both layout variants.

## Current implementation

The widget is now wired to real local adapters:

- `CodexAppServerLimitsDataSource` starts the already-installed `codex app-server --stdio`, performs `initialize`/`initialized`, requests `account/rateLimits/read`, selects the `codex` bucket, and maps `windowDurationMins` to supported windows.
- `ClaudeUsageLimitsDataSource` starts the authorized Claude Code executable with `/usage`, `--output-format json`, no tools, no session persistence, and plan permission mode. It parses only the result text for the 5-hour and weekly windows.
- Neither adapter reads credentials, cookies, prompts, transcripts, or API keys. Codex receives the user's normal `.codex` home through `CODEX_HOME`; Claude uses its normal first-party subscription login.
- `DemoCodexLimitsDataSource` and `DemoClaudeLimitsDataSource` remain available for deterministic visual/domain tests but are no longer wired into the running widget.

Claude statusLine bridge is now implemented in `spikes/ClaudeStatusLineBridge`. It reads only stdin JSON, extracts `rate_limits`, and atomically writes a redacted snapshot to `%LOCALAPPDATA%\LLMLimitsWidget\claude-statusline-snapshot.json`. It never writes the prompt, transcript path, cwd, model, account, or credentials, and exits with code 0 even for malformed input so Claude's own statusLine is not broken.

`ClaudeHybridLimitsDataSource` reads that snapshot first. A snapshot younger than three minutes is used immediately. If it is stale, direct `/usage` is allowed after a five-minute cooldown whether a recent statusLine session is known or not. This keeps the widget updating in the background when the user has not enabled Claude Code statusLine, while still avoiding a request on every 30-second coordinator tick. Manual refresh bypasses the cooldown and runs `/usage` once. If direct refresh fails, the last statusLine value remains visible as `Stale`.

Architecture v2 provisions the bridge only when the user-level `statusLine` is absent or already managed by this widget. A different user-owned command is never overwritten; direct `/usage` remains available in that case. The widget makes a backup before its atomic settings update. The resulting command is:

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& '<widget directory>/claude-statusline-bridge/LLMLimitsWidget.ClaudeStatusLineBridge.exe'\""
  }
}
```

Direct `/usage` remains the authoritative fallback and startup/manual refresh source; the bridge does not change the domain contract.

## Lifecycle

The window owns one coordinator, starts it after loading, and disposes it during window closing. The coordinator owns its polling cancellation token and `PeriodicTimer`; no provider work continues after the window is closed.

## UI mapping

The window maps provider-neutral windows to the existing compact rows:

- Codex: one `Weekly` window, labeled `W`.
- Claude: `FiveHour` and `SevenDay`, labeled `5h` and `7d`.
- The countdown block uses the nearest available `ResetAt`.
- Unknown percentages render as `—`; the domain never turns an unknown value into `0%`.

## Integration verification

The provider test project supports a real local smoke mode:

```powershell
dotnet run --project .\spikes\FloatingOverlay.DomainTests\FloatingOverlay.DomainTests.csproj --configuration Debug -- --real
```

The smoke output is redacted to provider, status, percentage, and reset time. It does not print raw provider responses or authentication material.

The bridge can be tested by piping one statusLine JSON object to `LLMLimitsWidget.ClaudeStatusLineBridge.exe`; it writes one atomic snapshot and exits successfully.
