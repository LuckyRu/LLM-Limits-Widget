# Architecture v2: M16 Claude statusLine provisioning

Status: implemented

## Outcome

Claude fast updates are now a deployable product capability rather than a manually assembled developer spike. The widget builds and ships a small independent executable at:

```text
<widget directory>/claude-statusline-bridge/LLMLimitsWidget.ClaudeStatusLineBridge.exe
```

The bridge accepts Claude Code's `statusLine` JSON on stdin, retains only `rate_limits.five_hour` and `rate_limits.seven_day`, atomically writes the redacted local snapshot, and pulses `Local\LLMLimitsWidget.ClaudeStatusLineUpdated`. It never reads provider credentials, prompts, transcripts, working directory, model, or account metadata.

## Configuration ownership

On Architecture v2 startup the widget checks `%USERPROFILE%\.claude\settings.json`.

- If `statusLine` is absent, it installs the widget command automatically. This activation is part of the user's explicit widget setup and applies only to the user-level Claude Code settings.
- If the command is already the widget bridge, it updates it to the executable deployed with the current widget build; the operation is idempotent.
- If another `statusLine` exists, it returns `ExistingUserStatusLine`, changes nothing, and retains the direct Claude CLI pipeline as the complete fallback.
- Invalid JSON, unavailable bridge binary, permissions, and I/O errors also leave the settings file unchanged and are logged with a typed configuration state.

Before any successful change the prior settings file is copied to:

```text
%USERPROFILE%\.claude\settings.llm-limits-widget.backup.json
```

The new file is written to a temporary sibling and moved atomically. The tray command **«Claude: восстановить быстрые обновления»** repeats the same safe check and shows its result without overwriting a user-owned command.

The installed command explicitly invokes PowerShell, so it works when Claude Code runs status lines through either Git Bash or PowerShell on Windows:

```text
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& '<bridge path>'"
```

Claude Code reloads settings automatically, but it runs the status-line command only after the next relevant Claude Code interaction. A restart or one interaction is therefore enough to make the fast channel start publishing.

## Delivery and recovery

```text
Claude Code statusLine stdin
          |
          v
independent bridge executable
          | atomic redacted snapshot
          +--> named event ---------------------+
          |                                     |
          +--> FileSystemWatcher fallback ------+--> AppStore observation ingress
                                                     |
                                                     +--> Claude direct CLI fallback / LKG cache
```

`ClaudeStatusLineSignalPump` now creates its snapshot directory before watching it, listens to the named event for low latency, and retains `FileSystemWatcher`, debounce, coalescing, and periodic reconciliation. Failure of the bridge, event handle, watcher, or settings configuration cannot block the independent `claude -p /usage` pipeline.

## Verification

- `LLMLimitsWidget.ClaudeStatusLineBridge.Tests`: valid JSON is redacted to the two supported windows; invalid data exits successfully and writes no plausible snapshot.
- `LLMLimitsWidget.Infrastructure.Windows.Tests`: settings merge preserves unrelated keys, writes a rollback backup, is idempotent, and refuses a user-owned status line.
- WPF build verifies the bridge executable is copied to the widget output directory.

## Operational diagnostics

Look in `%LOCALAPPDATA%\LLMLimitsWidget\logs` for `ClaudeStatusLine.configuration_checked` and the state `Configured`, `AlreadyConfigured`, `ExistingUserStatusLine`, or a repair state. The direct transport's own health remains independent; `StatusLineNotConfigured` is not a sign that Claude limits are unavailable when direct CLI is healthy.
