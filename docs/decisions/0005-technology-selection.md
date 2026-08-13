# ADR 0005: Технологии Floating LLM Limits Widget

Дата: 2026-08-12  
Статус: принято для MVP

## Решение

Основной стек:

- язык: C#;
- runtime: .NET 10 для Windows;
- UI: WPF/XAML;
- системная интеграция: небольшой слой Win32 interop;
- хранение локальных настроек: JSON в `%LocalAppData%`;
- секреты и токены: Windows Credential Manager или DPAPI, только если появится поддержанный способ авторизации;
- сетевой слой: `HttpClient` за интерфейсом provider’а;
- логирование: локальный rolling log без содержимого чатов и секретов;
- упаковка первого локального билда: self-contained single-file или обычный framework-dependent build; MSIX отложить до стабилизации MVP.

## Почему WPF

WPF хорошо подходит для персонального Windows-only overlay:

- зрелый XAML и data binding;
- простая реализация безрамочного окна;
- поддержка прозрачности, кастомных шаблонов и анимаций;
- встроенные свойства `Topmost`, позиционирование и mouse events;
- можно подключить Win32 API только там, где WPF не закрывает задачу.

Microsoft описывает WPF как Windows-only XAML UI framework с layout, styles, templates, graphics и data binding: [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).

## Win32 interop, который нужен MVP

- `RegisterHotKey` — глобальная горячая клавиша показать/скрыть;
- `SetWindowPos` / `HWND_TOPMOST` — контроль z-order и режима always-on-top;
- monitor/DPI APIs — сохранение позиции на правильном мониторе;
- `WM_NCHITTEST` или обычный mouse event — drag за область виджета;
- optional `Shell_NotifyIcon` — tray companion для настроек и выхода.

Microsoft рекомендует Win32 interop для always-on-top, позиционирования и глобальных hotkeys: [Windows app interop](https://learn.microsoft.com/en-us/windows/apps/develop/interop/).

## Почему не WinUI 3 как основной стек

WinUI 3 — хороший вариант для современной native Windows UI и остаётся возможным upgrade path. Но для первого личного overlay он добавляет Windows App SDK, packaging/runtime и больше инфраструктуры, тогда как WPF решает ключевую задачу окна меньшей ценой.

WinUI 3 стоит выбрать позже, если появятся:

- обязательная интеграция с Windows Widgets;
- сложные Fluent-компоненты;
- MSIX/Store-first distribution;
- несколько окон и более глубокая Windows App SDK integration.

Microsoft рекомендует WinUI 3 для новых native Windows desktop apps, а WPF называет зрелым XAML framework для .NET desktop apps: [Windows app development](https://learn.microsoft.com/en-us/windows/apps/).

## Почему не Electron/Tauri/Avalonia

- Electron слишком тяжёлый для маленького always-on-top utility.
- Tauri уменьшает размер, но добавляет webview/Rust/toolchain complexity, которая здесь не нужна.
- Avalonia полезна для cross-platform, но продукт сознательно Windows-only.
- WinForms остаётся хорошим fallback для tray host, но менее удобен для кастомного визуального overlay.

## Архитектурные границы

UI не должен знать детали ChatGPT или Claude. Используем provider abstraction:

```text
UsageProvider
  GetStatusAsync(CancellationToken) -> UsageSnapshot

UsageSnapshot
  Provider
  Plan
  Limits[]
  ObservedAt
  Freshness
  Confidence
  Source
```

Провайдеры сначала будут mock/manual-assisted, затем — только официально подтверждённый способ автоматического чтения usage. Никакого хранения паролей, чтения cookies или обхода внутренних endpoint’ов в MVP без отдельного security review.

## UX-поведение окна

- пользователь перетаскивает окно мышью;
- позиция и размер сохраняются;
- режим always-on-top переключается;
- `Ctrl+Alt+L` — предлагаемая глобальная горячая клавиша;
- двойной клик раскрывает подробности;
- после запуска окно появляется в последней валидной позиции;
- виджет разрешено размещать поверх taskbar — это один из основных целевых сценариев;
- безопасная область позиции равна физическим границам монитора (`rcMonitor`), а не рабочей области без taskbar (`rcWork`);
- внешние края мониторов защищены от ухода окна, общие края соседних мониторов остаются проходимыми;
- если монитор отключён, окно возвращается в физические границы ближайшего доступного монитора;
- окно не перехватывает ввод, когда пользователь включил compact/passive mode.

## Последствие решения

Начинаем с WPF overlay prototype и mock data. Интеграцию с личными usage-данными подключаем только после provider feasibility gate.
