# FloatingOverlay spike

WPF visual prototype for the approved compact limits overlay.

The visual system is defined in `Themes/Theme.xaml`: color tokens, typography defaults, menu, separator, tooltip, button, and meter styles live there so new controls can use the same dark overlay language. Provider marks are rendered as native vector geometry in `ProviderLogo.cs`, so they remain sharp during corner scaling. Every limit window uses the reusable `LimitMetricControl` (circular value + period label), while `WidgetAppearance` holds the reusable size, scale, opacity, and corner-radius settings.

The window is borderless, transparent outside the acrylic-like surface, always on top, starts near the bottom edge, and can be dragged with the mouse. Right-click opens the widget menu with vertical and horizontal layout choices, visible proportional scale presets, refresh, position reset, hide, and exit. Provider names and inline refresh controls are intentionally omitted to keep the compact layout; forced refresh is available from the context menu. Dragging any corner scales the widget while preserving the selected layout aspect ratio. The vertical layout is 285:112; the horizontal layout measures itself from the visible provider rows (about 415:64 for the current sample), uses tighter metric columns, and places Codex and Claude on one line with a vertical divider, without reserving space for hidden metrics or distributing unused width between them. It currently uses the real sample values captured during the provider spikes:

- Codex: 62% weekly remaining, reset 18 Aug at 07:03.
- Claude: 5% in the 5-hour window and 70% in the 7-day window, nearest reset 13 Aug at 00:30.

Run from the repository root:

```powershell
dotnet run --project .\spikes\FloatingOverlay\FloatingOverlay.csproj
```

Provider adapters are intentionally not wired into this visual spike yet.
