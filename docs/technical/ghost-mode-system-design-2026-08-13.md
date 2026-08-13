# System design: режим призрака

Дата: 2026-08-13  
Статус: спроектировано, не реализовано

## 1. Цель

Режим призрака оставляет FloatingOverlay полностью видимым и верхним окном интерактивного Windows desktop, но исключает его из пользовательского ввода:

- mouse move, hover, click, double-click и wheel получает окно под виджетом;
- touch и pen должны попадать в поверхность под виджетом;
- виджет не получает keyboard focus и не становится foreground window;
- виджет расположен выше обычных окон, Windows taskbar и notification area/system tray;
- если Shell или другое topmost-окно поднялось выше, верхняя позиция автоматически восстанавливается без активации виджета;
- визуальный layout, opacity, положение и обновление данных не меняются;
- включение и выключение доступны через tray menu;
- после перезапуска режим восстанавливается безопасно.

Главный пользовательский инвариант:

```text
Ghost = visible + shell-topmost + input-transparent + non-activating
```

`shell-topmost` означает строгую гарантию внутри текущего интерактивного desktop: виджет находится выше обычных приложений, taskbar и notification area. Это не распространяется на Secure Desktop/UAC, экран блокировки, `Ctrl+Alt+Delete` и поверхности настоящего exclusive fullscreen, которые не участвуют в том же пользовательском z-order.

## 2. Почему WPF недостаточно

`IsHitTestVisible="False"` исключает WPF-элементы только из WPF hit testing. Оно не убирает top-level HWND из системного hit testing между процессами. Поэтому окно другого приложения под overlay не обязано получить click.

Возврат `HTTRANSPARENT` из `WM_NCHITTEST` тоже нельзя считать единственным механизмом. Microsoft документирует передачу hit test к нижележащим окнам того же UI thread; приложения под нашим overlay работают в других процессах и потоках.

Следовательно, реализация должна менять native extended styles HWND и использовать WPF/WindowProc-проверки как дополнительные защитные слои.

## 3. Архитектурная граница

Создаётся отдельный `GhostModeController`:

```text
App / tray menu
       │ toggle
       ▼
MainWindow ─────────────── WidgetSettingsStore
       │                         │
       │                         └─ GhostModeEnabled
       ▼
GhostModeController
       ├─ native extended styles
       ├─ activation/focus policy
       ├─ HWND message defenses
       └─ state transition result

WindowPlacementController
       └─ position / bounds / DPI

OverlayZOrderSupervisor
       ├─ HWND_TOPMOST
       ├─ Shell/foreground event signals
       ├─ coalesced reassert burst
       └─ low-frequency watchdog
```

`GhostModeController` не вычисляет позицию и не управляет z-order. `WindowPlacementController` не решает, куда отправлять input. `OverlayZOrderSupervisor` не меняет координаты, размер и native input styles. Все три компонента используют один HWND, но владеют разными инвариантами.

Предлагаемый контракт:

```csharp
internal interface IGhostModeController : IDisposable
{
    bool IsEnabled { get; }
    GhostModeResult SetEnabled(bool enabled);
    void Attach();
    void EnsureApplied();
}
```

Все операции выполняются на WPF Dispatcher thread после `SourceInitialized`.

## 4. Native input policy

### 4.1 Extended styles

При включении режима к текущему `GWL_EXSTYLE` добавляются:

- `WS_EX_TRANSPARENT (0x00000020)` — основной native слой click-through для overlay;
- `WS_EX_NOACTIVATE (0x08000000)` — окно не становится foreground window от клика и keyboard navigation.

Microsoft отдельно рекомендует сочетать `WS_EX_TRANSPARENT` с `WS_EX_LAYERED` у top-level windows именно для hit testing. `AllowsTransparency="True"` уже создаёт layered WPF window, поэтому контроллер должен проверить наличие `WS_EX_LAYERED`, но не добавлять и не удалять его самостоятельно: этим владеет WPF. Если ожидаемого layered style нет, enable завершается ошибкой `UnsupportedWindowStyle`, а не включает частичный режим.

Mouse click-through для этой комбинации имеет платформенное основание, но touch/pen всё равно остаются обязательным integration gate на целевой Windows 11 конфигурации. Реализация не должна синтетически пересылать input в найденное нижнее окно: это ломает capture, double-click, wheel, pointer identity, privilege boundaries и порядок сообщений.

Алгоритм изменения:

1. прочитать текущий style через `GetWindowLongPtr(GWL_EXSTYLE)`;
2. вычислить `addedMask = requiredMask & ~currentStyle`;
3. записать `currentStyle | requiredMask` через `SetWindowLongPtr`;
4. применить cached styles вызовом `SetWindowPos` с:

```text
SWP_NOMOVE |
SWP_NOSIZE |
SWP_NOZORDER |
SWP_NOACTIVATE |
SWP_FRAMECHANGED
```

При выключении очищаются только биты из `addedMask`. Полный старый style не восстанавливается целиком: WPF или другой контроллер могли легитимно изменить остальные флаги после включения режима.

Для корректного определения ошибки `SetWindowLongPtr` необходимо перед вызовом сбросить last error в `0`. Нулевой return может означать как ошибку, так и успешную замену предыдущего нулевого значения.

### 4.2 Дополнительные WindowProc-защиты

Пока режим включён:

- `WM_NCHITTEST` возвращает `HTTRANSPARENT` как дополнительный слой;
- `WM_MOUSEACTIVATE` возвращает `MA_NOACTIVATE`, если сообщение всё же дошло до overlay;
- WPF root получает `IsHitTestVisible = false`;
- `Focusable = false`, keyboard focus внутри окна очищается.

`HTTRANSPARENT` не считается межпроцессной гарантией и не заменяет native style. Получение overlay-окном mouse/pointer сообщения в ghost mode следует считать нарушением инварианта и диагностировать.

### 4.3 Keyboard policy

Keyboard input направляется активному/focused окну. Ghost mode обеспечивает отсутствие этого состояния:

- `WS_EX_NOACTIVATE` запрещает активацию от мыши и keyboard navigation;
- при включении вызывается `Keyboard.ClearFocus()`;
- `ShowWidget` и `ResetWidgetPosition` не вызывают `Activate()`, пока ghost mode включён;
- скрытый ghost widget показывается без активации: `ShowActivated = false`, затем `SetWindowPos(... SWP_NOACTIVATE)`;
- глобальные hotkey/input hooks для режима призрака не регистрируются.

Если переключение через tray произошло, когда overlay ещё владел foreground focus, возврат фокуса предыдущему внешнему HWND выполняется best-effort. Отсутствие надёжного внешнего HWND не должно отменять включение режима; после первого пользовательского действия foreground всё равно перейдёт целевому приложению.

## 5. Z-order policy: shell-topmost

### 5.1 Почему одного `Topmost = true` недостаточно

`HWND_TOPMOST` гарантирует положение выше non-topmost окон и сохраняет topmost state после деактивации. Однако Windows хранит несколько окон внутри topmost band: Shell flyouts, taskbar, notification area и другие приложения могут оказаться выше виджета после собственного `SetWindowPos`, показа нового HWND, перезапуска Explorer или смены foreground window.

Поэтому topmost — не одноразовая настройка, а поддерживаемый runtime-инвариант. Его владелец — отдельный `OverlayZOrderSupervisor`:

```csharp
internal interface IOverlayZOrderSupervisor : IDisposable
{
    bool IsRunning { get; }
    TopmostHealth Health { get; }
    void Attach();
    void SetVisible(bool visible);
    void Reassert(TopmostReason reason);
}
```

Supervisor работает и в interactive mode, и в ghost mode. Режим призрака добавляет input transparency, но не является причиной существования topmost policy: любой видимый FloatingOverlay должен оставаться поверх Shell.

### 5.2 Источники восстановления

Reassert запускается после:

- `SourceInitialized`, `Show()` и восстановления из hidden state;
- `WM_ACTIVATEAPP`/WPF `Deactivated`;
- `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, resume/unlock и восстановления Explorer;
- `EVENT_SYSTEM_FOREGROUND` через `SetWinEventHook`;
- `EVENT_OBJECT_SHOW` для нового top-level window (`idObject == OBJID_WINDOW`, `idChild == CHILDID_SELF`);
- завершения app-owned tray menu;
- срабатывания watchdog, если событий оказалось недостаточно.

WinEvent hooks регистрируются как `WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`. Callback не вызывает WPF и `SetWindowPos` напрямую: он только ставит coalesced request в Dispatcher, чтобы не выполнять native/WPF работу на чужом event callback и не создавать рекурсию от собственных событий.

`EVENT_OBJECT_REORDER` не используется как основной глобальный сигнал: он шумный и документирован прежде всего для изменения порядка дочерних объектов. Пропущенные перестановки верхнего уровня закрывает watchdog.

### 5.3 Алгоритм reassert

Для видимого и созданного HWND выполняется:

```text
SetWindowPos(
  hwnd,
  HWND_TOPMOST,
  0, 0, 0, 0,
  SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
```

Координаты, размер, monitor affinity и placement при этом не меняются. `SWP_NOACTIVATE` обязателен: восстановление z-order никогда не должно забирать foreground focus.

Каждый внешний сигнал запускает coalesced burst: немедленно, затем примерно через `75`, `150` и `300 ms`. Burst закрывает последовательность Shell-окон, создаваемых несколькими шагами. Одновременно работает watchdog с периодом `1000 ms`, но только пока overlay видим. Повторный запрос во время активного burst объединяется с текущим, а не создаёт ещё один timer chain.

Целевые показатели:

- после foreground/Shell event виджет снова сверху не позднее `300 ms`;
- событие, не замеченное hooks, исправляется watchdog не позднее `1100 ms`;
- ни один reassert не активирует виджет и не меняет его bounds;
- taskbar и notification area продолжают получать mouse/touch/pen input сквозь ghost widget.

### 5.4 Tray menu и системные flyouts

Обычные Shell surfaces — taskbar, notification area, Start, Search, Quick Settings и tray overflow — остаются под виджетом. В ghost mode input проходит к ним, хотя виджет визуально расположен выше.

App-owned tray context menu является единственным разрешённым исключением: это recovery channel для выключения ghost mode. На время его показа supervisor приостанавливает watchdog и поднимает menu HWND выше overlay. После закрытия меню немедленно восстанавливается shell-topmost. По возможности меню позиционируется рядом, а не поверх виджета; если их rectangles пересеклись, меню имеет приоритет управления.

### 5.5 Границы гарантии

Нельзя корректно обещать абсолютное «выше вообще всего» средствами обычного desktop-приложения:

- Secure Desktop/UAC, lock screen и `Ctrl+Alt+Delete` находятся на другом desktop;
- настоящий exclusive fullscreen может обходить desktop composition;
- другое приложение в topmost band может непрерывно переставлять собственное окно наверх;
- окна с более высоким integrity level или защищённые системные поверхности не должны обходиться через injection/UIAccess.

Для обычных окон и Windows Shell действует строгая `shell-topmost` policy. Для корректно ведущих себя сторонних topmost-окон действует eventual guarantee с указанной задержкой восстановления. Если сторонняя программа намеренно ведёт z-order war, supervisor ограничивает частоту reassert и пишет throttled diagnostic event, не использует process injection, глобальные low-level input hooks, `UIAccess`, elevation или отключение UAC.

## 6. State model

Visibility и interaction mode — независимые оси:

| Visibility | Interaction | Поведение |
|---|---|---|
| Visible | Interactive | обычный виджет |
| Visible | Ghost | виден, shell-topmost, input проходит вниз |
| Hidden | Interactive | скрыт, следующий show интерактивный |
| Hidden | Ghost | скрыт, следующий show сразу passive |

Переходы:

```text
Tray: Ghost ON
  -> apply native policy
  -> update WPF input flags
  -> verify effective style
  -> persist true
  -> check tray item

Tray: Ghost OFF
  -> restore only owned native bits
  -> restore WPF input flags
  -> persist false
  -> uncheck tray item
```

Пункт tray menu называется `Режим призрака` и показывает `Checked = true`, только когда native transition завершился успешно. `CheckOnClick` не используется как источник истины: UI обновляется из фактического состояния контроллера.

## 7. Tray как recovery channel

В ghost mode контекстное меню самого WPF-виджета недоступно по определению. Единственный обязательный управляющий канал — tray icon, который принадлежит отдельному `NotifyIcon`/menu HWND и не становится input-transparent вместе с overlay.

Требования безопасности от lockout:

1. tray icon и menu создаются до восстановления persisted ghost mode;
2. если tray initialization не удалась, ghost mode в текущей сессии не включается;
3. tray menu всегда содержит `Режим призрака`, `Показать виджет`, `Сбросить позицию`, `Выйти`;
4. `Показать виджет` не выключает ghost mode и не активирует ghost window;
5. `Сбросить позицию` работает в ghost mode без активации;
6. аварийный аргумент запуска `--no-ghost` игнорирует persisted ghost mode на одну сессию, не перезаписывая профиль автоматически.

Double-click по tray icon должен только показать окно. Скрытое изменение interaction mode через double-click нежелательно: пользователь должен видеть checked-state в menu.

## 8. Persistence

В `WidgetSettings` добавляется:

```json
{
  "SchemaVersion": 3,
  "GhostModeEnabled": true
}
```

Миграция `v2 -> v3` безопасна: отсутствующее поле десериализуется как `false`. Значение сохраняется только после успешного native transition.

Startup sequence:

1. загрузить и нормализовать settings;
2. создать `MainWindow`, но не активировать;
3. создать tray icon и menu;
4. создать HWND (`SourceInitialized`);
5. восстановить layout и placement;
6. применить ghost mode, если он сохранён и нет `--no-ghost`;
7. показать окно с activation policy выбранного режима;
8. обновить checked-state tray menu.

Так не возникает кадра, в котором persisted ghost widget сначала забирает focus, а затем становится passive.

## 9. Совместимость с существующими подсистемами

### Topmost и taskbar

Существующий `WindowPlacementController.ReassertTopmost()` становится начальной реализацией `OverlayZOrderSupervisor`. Native вызов `SetWindowPos(HWND_TOPMOST, ... SWP_NOACTIVATE)` совместим с ghost mode и не должен снимать extended styles.

Click по taskbar или notification area под overlay проходит к Shell. Event-driven burst и watchdog возвращают виджет на вершину topmost band без активации, даже если Shell создал или поднял несколько HWND последовательно.

### Drag и resize

В ghost mode drag, resize, context menu и tooltips overlay недоступны. Это ожидаемое поведение. Положение и масштаб меняются только после отключения режима либо через разрешённые tray-команды.

### Hide/show

`Hide()` не меняет persisted interaction mode. При повторном show контроллер вызывает `EnsureApplied()` до первого интерактивного кадра.

### DPI и topology

`WM_DPICHANGED`, display topology и placement normalization не выключают ghost mode. После возможного изменения HWND/style controller повторно проверяет owned flags.

### Обновление данных

Provider polling и визуальный render продолжаются. Ghost mode относится только к input/activation и не должен приостанавливать timers или data acquisition.

## 10. Ошибки и rollback

`SetEnabled` возвращает структурированный результат:

```text
Success
AlreadyInRequestedState
HandleUnavailable
StyleReadFailed
StyleWriteFailed(errorCode)
StyleApplyFailed(errorCode)
UnsupportedWindowStyle
VerificationFailed
TopmostApplyFailed(errorCode)
```

Если enable не завершился полностью:

- WPF hit testing не выключается либо возвращается в interactive state;
- добавленные native bits откатываются best-effort;
- persisted setting не меняется;
- tray item остаётся unchecked;
- ошибка записывается в локальный log без координат кликов и содержимого input.

Если disable частично не удался, приложение не должно сообщать, что режим выключен. Tray item остаётся checked, а пользователю доступен `Выйти` и перезапуск с `--no-ghost`.

Ошибка установки WinEvent hook не отключает overlay: supervisor переходит в degraded mode с существующими local signals и watchdog. Ошибка самого `SetWindowPos(HWND_TOPMOST)` нарушает обязательный ghost-инвариант: включение ghost mode не подтверждается, а уже включённый режим показывает в tray состояние `Ошибка удержания поверх окон` и повторяет попытки с bounded backoff.

На `Dispose` controller снимает hook. Восстановление styles перед уничтожением HWND необязательно для ОС, но полезно для детерминированного lifecycle и тестов.

## 11. Проверка реализации

### Unit tests

- required mask добавляется к произвольному existing style;
- disable снимает только `addedMask`;
- неизвестные/добавленные позднее style bits сохраняются;
- state transition не сохраняет настройку при ошибке;
- миграция settings v2 даёт `GhostModeEnabled = false`;
- `--no-ghost` переопределяет профиль только для текущей сессии.
- coalescing объединяет event storm в один reassert burst;
- watchdog остановлен для hidden/destroyed HWND и app-owned tray menu;
- z-order reassert никогда не меняет position/size и не запрашивает activation.

### Win32 integration harness

Нужен тестовый host с button/textbox под реальным overlay и счётчиками input:

1. в interactive mode click получает overlay, нижняя кнопка — нет;
2. в ghost mode нижняя кнопка получает одиночный и двойной click;
3. wheel прокручивает нижнюю поверхность;
4. mouse move/hover и tooltip срабатывают под overlay;
5. touch/pen pointer попадает в нижний host;
6. textbox под overlay сохраняет keyboard focus и получает ввод;
7. overlay не получает WPF mouse/key events;
8. визуальный pixel output до и после toggle идентичен.
9. test host, taskbar и notification area остаются ниже overlay, но получают input;
10. новое competing topmost window исправляется event burst/watchdog;
11. Explorer restart и повторное создание taskbar восстанавливают shell-topmost.

### Product acceptance matrix

- toggle через tray в visible и hidden состояниях;
- persisted ghost mode после restart;
- `Показать виджет` и `Сбросить позицию` не забирают focus;
- widget расположен поверх обычных окон, taskbar и notification area на каждом мониторе;
- click, wheel и hover по taskbar/system tray проходят вниз, widget остаётся shell-topmost;
- Start, Search, Quick Settings и tray overflow не выталкивают widget вниз;
- foreground switch, Alt+Tab, Win+D и Show Desktop восстанавливают верхнюю позицию в пределах SLO;
- restart Explorer, sleep/resume, lock/unlock и display topology change не теряют topmost policy;
- app-owned tray menu доступно для recovery и после закрытия возвращает widget наверх;
- competing topmost app проверяется на отсутствие focus stealing и неограниченного event storm;
- работа на мониторах с разным DPI и отрицательными координатами;
- переключение layout/topology не снимает ghost policy;
- tray init failure запускает interactive fallback;
- `--no-ghost` гарантированно возвращает управление;
- secure desktop/UAC не перекрывается и не обходится.

## 12. Этапы реализации

1. Выделить существующий topmost reassert в `OverlayZOrderSupervisor`.
2. Добавить WinEvent foreground/show hooks, coalesced burst и visible-only watchdog.
3. Добавить `GhostModeController` и чистые функции style-mask.
4. Добавить schema v3 и startup migration.
5. Перестроить startup: tray до восстановления ghost state.
6. Добавить checked tray command и passive show/reset paths.
7. Реализовать rollback, degraded health и throttled diagnostics.
8. Добавить unit tests style/state/persistence/z-order coalescing.
9. Создать Win32 integration harness и пройти input/z-order matrix.
10. Провести реальную проверку поверх taskbar, notification area, Shell flyouts, VS Code и браузера.

## 13. Риски и решение

| Риск | Мера |
|---|---|
| WPF-only hit testing не пропускает input в другой процесс | native extended styles являются основным механизмом |
| `HTTRANSPARENT` ограничен тем же UI thread | используется только как дополнительная защита |
| потеря управления виджетом | tray-first startup и `--no-ghost` |
| ghost window случайно получает focus | `WS_EX_NOACTIVATE`, passive show, очистка WPF focus |
| style update затирает WPF flags | изменяется только owned mask |
| Shell поднимается над overlay | event-driven burst + visible-only watchdog с `SWP_NOACTIVATE` |
| Explorer пересоздал taskbar/tray HWND | foreground/show events и watchdog не зависят от сохранённого Shell HWND |
| WinEvent hook недоступен | degraded mode на local signals + watchdog |
| competing topmost создаёт z-order war | coalescing, rate limit и throttled diagnostics без injection/elevation |
| tray menu оказалось под overlay | app-owned menu — временное разрешённое исключение с immediate reassert после close |
| неполный transition ошибочно сохраняется | verify + rollback до persistence |

## 14. Источники платформенных контрактов

- [Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)
- [DWM performance considerations and hit-testing guidance](https://learn.microsoft.com/en-us/windows/win32/dwm/bestpractices-ovw)
- [SetWindowLongPtr](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptra)
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [Window Features: Z-Order](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#z-order)
- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)
- [WinEvent constants](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants)
- [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)
- [WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)
- [UAC settings and Secure Desktop](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/settings-and-configuration)
