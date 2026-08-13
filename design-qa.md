# Design QA

## Target

Approved reference: `C:\Users\Leshc\.codex\generated_images\019ff747-0590-7302-820a-e52e214a8ded\exec-c91c1e60-3a9a-4ee5-9bd0-3289a6339b79.png`.

The implementation target is a compact 620×112 px Windows overlay with two equal provider rows, no header, circular remaining-limit meters, compact time-window bars, nearest reset information, refresh affordances, and freshness dots.

## QA result

- `dotnet build .\spikes\FloatingOverlay\FloatingOverlay.csproj --no-restore`: passed, 0 warnings, 0 errors.
- Layout and interactions are implemented in WPF: borderless transparent always-on-top window, starts near the lower-right edge, drag-to-move, Escape to hide, refresh affordances, right-click context menu, and proportional scale presets.
- Theme primitives are centralized in `spikes/FloatingOverlay/Themes/Theme.xaml`; menu and tooltip use custom dark templates rather than default Windows control chrome.
- Provider initials were replaced with native vector OpenAI/ChatGPT and Claude marks in `spikes/FloatingOverlay/ProviderLogo.cs`; no raster scaling is involved.
- Compact pass: provider labels and inline refresh/freshness controls are removed; the base layout is now 285×112 with fixed reset columns and no trailing empty stretch, while forced refresh remains available from the context menu.
- Information hierarchy: each limit window is encoded once by a circular meter; short window labels (`W`, `5h`, `7d`) identify the period without duplicating the percentage. The exact weekly value remains available on hover.
- Reusable component pass: Codex and Claude windows now use the same `LimitMetricControl`; Codex renders one instance (`W`), Claude renders two (`5h`, `7d`). Appearance primitives are grouped in `WidgetAppearance`.
- Visual inspection was attempted through the Windows desktop inspector. The WPF process creates and loads the window, but the sandbox launches it outside the interactive desktop session, so the inspector cannot capture the HWND. Screenshot-level visual QA remains to be run by launching the executable directly in the user's Windows session.

## Known prototype boundary

The overlay uses the approved sample values from the provider spikes. Provider adapters are intentionally not wired into this visual spike yet.
