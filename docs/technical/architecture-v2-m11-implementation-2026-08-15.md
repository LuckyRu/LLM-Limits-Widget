# Architecture v2: M11 WPF ViewModel cutover

Дата: 2026-08-15  
Статус: feature-flagged UI cutover implemented

## Реализовано

- feature-flagged `MainWindow` получает новый `WidgetViewModel` через composition root;
- legacy provider coordinator не стартует при `--arch-v2`/`LLM_WIDGET_ARCH_V2=1`;
- Codex row отображает weekly window как единственный metric;
- Claude row отображает 5-hour и 7-day windows;
- отсутствующие данные отображаются пустыми metric slots без нулевых placeholder-значений;
- WPF rows получают проценты, period labels, countdown, reset date/time, tooltip и health state из VM;
- manual refresh из меню dispatch-ится в новый AppStore для обоих providers;
- countdown timer использует `WidgetViewModel.GetNextVisualChangeAt`, а не постоянную секундную перерисовку;
- legacy UI path и legacy coordinator остаются доступными для rollback.

## Важное ограничение

Включение нового контура всё ещё требует feature flag. Это позволяет отдельно провести реальный smoke на пользовательских Codex/Claude сессиях и проверить поведение виджета перед переключением default path.

## Проверка

```powershell
dotnet build .\spikes\FloatingOverlay\FloatingOverlay.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Presentation.Tests\LLMLimitsWidget.Presentation.Tests.csproj -p:UseSharedCompilation=false
```

Ожидаемый результат:

```text
FloatingOverlay -> Build succeeded
Presentation M9: all cases passed.
```
