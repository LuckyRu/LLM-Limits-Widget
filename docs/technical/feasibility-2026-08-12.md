# Техническая проверка MVP

Дата: 2026-08-12  
Статус: preliminary feasibility  
Scope: личные планы ChatGPT Plus/Pro и Claude Pro; один личный аккаунт каждого сервиса; Windows 11.

## Краткий вывод

Полезный MVP возможен, но исходное обещание состоит из двух разных по риску частей:

1. Получать и показывать usage-статус личных подписок.
2. Реализовать нативный Windows Widget и, отдельно, проверить inline-представление в строке taskbar как у погоды.

Первая часть имеет рабочий пользовательский источник данных внутри обоих продуктов, но внешний официальный API для личного subscription usage не подтверждён. Windows Widgets официально поддерживаются, но Microsoft описывает их как карточки в Widgets Board. Публичный API для произвольного стороннего inline-контента в строке taskbar не подтверждён.

Рекомендация: продолжать технический spike вокруг плавающего overlay и планировать два уровня MVP:

- основной baseline: WPF overlay с mock/manual-assisted status;
- provider spike: отдельно проверить автоматическое чтение usage для ChatGPT Plus/Pro и Claude Pro.

До получения устойчивого и безопасного источника данных не строить продукт на автоматическом скрейпинге внутренних web-endpoint’ов.

## Feasibility matrix

| Область | Результат | Уверенность | Вывод |
|---|---|---:|---|
| ChatGPT Plus/Pro: увидеть remaining limit | Подтверждено внутри UI личного профиля; пользовательский скриншот показывает weekly remaining limit и дату | Средняя | Достаточно для ручной валидации, внешний канал ещё не доказан |
| ChatGPT Plus/Pro: официальный внешний consumer usage API | Не найден в проверенных официальных источниках | Средняя-высокая | Нельзя проектировать интеграцию как обычный API connector |
| Claude Pro: session/weekly usage | Подтверждено в `Settings > Usage`, включая next reset time | Высокая | Данные существуют внутри продукта |
| Claude Pro: официальный внешний personal usage API | Не найден; упомянутый Analytics API относится к Enterprise | Высокая | Не использовать Anthropic API как замену подписочному usage |
| Безопасная авторизация | Возможна только через официальный поддержанный flow, если он будет найден; API keys не подходят | Средняя | Не хранить пароль и не извлекать cookies без отдельного решения |
| WPF overlay | Подходит для borderless/topmost/custom window; доступен локальный .NET SDK | Высокая | Выбранный основной путь |
| Windows Widgets provider | Поддержан для packaged Win32 app или PWA; данные и шаблон отдаёт provider | Высокая | Альтернативный путь, не MVP |
| Inline-карточка в строке taskbar как погода | Публичный стабильный путь не подтверждён | Высокая | Не закладывать в MVP |
| Windows 11 system tray | Поддержано через `Shell_NotifyIcon` | Высокая | Companion/fallback |
| Отдельное appbar-окно у нижнего края | Поддержано через `SHAppBarMessage`, но это отдельная полоса, а не содержимое taskbar | Высокая | Возможный эксперимент, не равно исходному UX |

## ChatGPT Plus/Pro

### Что подтверждено

- В пользовательском интерфейсе ChatGPT уже существует компактный показатель `Оставшийся лимит` с weekly period и датой сброса; это видно на сохранённом пользовательском evidence: [chatgpt-profile-remaining-limit.png](../research/evidence/chatgpt-profile-remaining-limit.png).
- Официальная документация OpenAI указывает, что для доступных персональных функций Codex, ChatGPT Work, ChatGPT for Excel и Workspace Agents может использоваться общий agentic usage/credit pool.
- Расход зависит от размера и сложности задачи, модели и места выполнения, поэтому число сообщений не является универсальной единицей.
- Подробное usage можно проверять в Settings; часть Plus/Pro пользователей может продолжить работу через credits, если это доступно плану.

### Что не подтверждено

В проверенных официальных документах не найден публичный consumer endpoint или SDK, который позволял бы desktop-приложению безопасно получить именно остаток ChatGPT Plus/Pro. OpenAI API использует API keys и отдельную модель API billing; это не даёт остаток ChatGPT-подписки.

### Варианты реализации

| Вариант | Надёжность | Безопасность | Решение |
|---|---:|---:|---|
| Официальный consumer usage API | Неизвестно | Потенциально высокая | Искомый вариант; нужно подтвердить существование |
| Пользователь вручную вводит остаток/reset | Высокая | Высокая | Fallback для раннего UX-прототипа, не автоматический MVP |
| Локальное чтение авторизованной web-сессии | Низкая/неизвестно | Рискованно | Не использовать до отдельной проверки разрешённости, стабильности и хранения токенов |
| OpenAI API key | Высокая для API usage | Неподходящая модель | Не является источником ChatGPT Plus/Pro usage |

Источник: [Using Codex with your ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan), [OpenAI API authentication](https://platform.openai.com/docs/api-reference/backward-compatibility?lang=ruby).

## Claude Pro

### Что подтверждено

- Claude Pro имеет пятичасовой session limit и weekly usage limit.
- Следующее weekly reset time видно в `Settings > Usage`.
- Usage settings показывают прогресс текущей сессии, оставшееся время сессии и недельные лимиты.
- Claude Pro usage не равен Claude API usage: официальная справка отдельно говорит, что Pro не включает API usage через Claude Console.
- Для Pro и Claude Code лимиты могут быть общими между Claude и Claude Code; usage credits — отдельный платный механизм.

### Что не подтверждено

Официальный способ читать personal Claude Pro usage из внешнего desktop-приложения не найден. Analytics API в проверенных материалах относится к Enterprise analytics, не к личному Pro dashboard.

### Варианты реализации

| Вариант | Надёжность | Безопасность | Решение |
|---|---:|---:|---|
| Официальный personal usage API | Неизвестно | Потенциально высокая | Искомый вариант; нужно подтвердить существование |
| Пользователь вручную вводит остаток/reset | Высокая | Высокая | Fallback для UX-прототипа |
| Локальное чтение авторизованной web-сессии | Низкая/неизвестно | Рискованно | Не использовать без отдельного security/legal review |
| Anthropic API/Console | Высокая для API usage | Неподходящая модель | Не является источником Claude Pro usage |

Источники: [What is the Pro plan?](https://support.claude.com/en/articles/8325606-what-is-the-pro-plan), [Usage limit best practices](https://support.claude.com/en/articles/9797557-usage-limit-best-practices), [Use Claude Code with your Pro or Max plan](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan), [Team and Enterprise analytics](https://support.claude.com/en/articles/12883420-view-usage-analytics-for-team-and-enterprise-plans).

## Windows 11 placement

### Альтернативный путь: Windows Widgets

Microsoft описывает Windows Widgets как небольшие UI-контейнеры, которые отображаются в Widgets Board. В текущей платформенной модели Widgets host — встроенный Widgets Board; стороннее приложение может быть provider’ом для packaged Win32 app или PWA. Provider возвращает JSON с layout и data для Adaptive Cards.

Это соответствует платформенной концепции Windows Widget, но не даёт постоянного inline-статуса в строке taskbar. После выбора overlay этот путь оставляем как возможное будущее расширение.

Для Win32 C# provider официальный walkthrough требует Windows App SDK, target OS 10.0.19041.0 или новее и package manifest extension с именем `com.microsoft.windows.widgets`. Provider реализует `IWidgetProvider`, а packaged app регистрирует CLSID provider’а и widget definitions.

Источники: [Windows Widgets overview](https://learn.microsoft.com/en-us/windows/apps/design/widgets/), [Widget providers](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers), [Implement a widget provider in a C# Windows app](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs), [Widget provider manifest](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-provider-manifest).

### Поддержанный путь: system tray

Microsoft документирует `Shell_NotifyIcon` для добавления, изменения и удаления иконки в notification area/taskbar status area. Это стабильная основа для компактного статуса, tooltip, клика и собственного flyout.

Источник: [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona), [Notifications and the Notification Area](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area).

### Отдельное appbar-окно

Windows поддерживает application desktop toolbar/appbar через `SHAppBarMessage`. Такое окно привязывается к краю экрана и сообщает системе занимаемую область. Оно может быть расположено рядом с taskbar, но это отдельная полоса, а не дочерний элемент штатной панели задач. Визуально оно может отличаться от цели на пользовательском скриншоте и влиять на рабочую область.

Источник: [Using Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars), [SHAppBarMessage](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage).

### Не рекомендуемый путь: overlay поверх taskbar

Окно, которое вычисляет координаты панели задач и рисуется поверх неё, может выглядеть близко к target screenshot, но это не официальное встраивание. Такой overlay нужно будет постоянно синхронизировать с автоскрытием, масштабированием, несколькими мониторами, сменой положения/размера taskbar и перезапуском Explorer. Для личного эксперимента возможно, для надёжного MVP — нет.

## Выбранная архитектура MVP

1. WPF desktop app на C#/.NET 10.
2. Borderless window с кастомным XAML-дизайном, Topmost и drag behavior.
3. Небольшой Win32 interop layer для global hotkey, DPI/monitor placement и z-order.
4. Provider service, который отдаёт UsageSnapshot.
5. Интерфейс провайдера:

```text
UsageProvider
  get_status() -> UsageSnapshot

UsageSnapshot
  provider
  plan
  limits[]
  observed_at
  freshness
  confidence
  source
```

6. На первом этапе — mock provider и ручной импорт snapshot; затем отдельная авторизация/provider spike.
7. Tray companion остаётся optional fallback для настроек, выхода и восстановления окна.
8. Никогда не хранить пароль; токены, если официальный flow их выдаёт, хранить только в Windows Credential Manager/DPAPI после подтверждения допустимого flow.

### Инфраструктурный статус

- .NET 10 SDK и Windows desktop targeting pack доступны локально.
- WPF выбран как основной UI framework; отдельный Windows App SDK runtime не нужен для первого overlay prototype.
- Windows App SDK package отсутствует в локальном NuGet-кэше.
- Для native Widget provider в будущем потребуется restore Windows App SDK и создание packaged/MSIX проекта; текущий WinForms spike это не заменяет.

## Gate перед автоматическим MVP

Автоматический MVP можно начинать только если для каждого сервиса одновременно выполнены условия:

- есть понятный и допустимый способ авторизации;
- нет необходимости хранить пароль;
- источник данных стабильно возвращает remaining/reset или явный unavailable;
- есть обработка stale data и изменения схемы;
- пользователь может отозвать доступ;
- источник не является хрупким обходом внутренних endpoint’ов.

Если эти условия не выполняются, запускаем UX-прототип с mock/manual snapshots и не выдаём его за автоматический мониторинг.

## Следующие технические эксперименты

1. Создать WPF overlay prototype с mock UsageSnapshot.
2. Проверить drag, always-on-top toggle, сохранение позиции, DPI и несколько мониторов.
3. На тестовом аккаунте вручную проверить, какие именно страницы и состояния показывают remaining/reset у ChatGPT Plus/Pro и Claude Pro.
4. Проверить официальные support/developer каналы на наличие personal usage API; не переходить к чтению cookies без отдельного решения.
5. Реализовать provider interfaces и manual-assisted flow.
6. После этого принять решение по provider strategy: automatic, manual-assisted или unavailable.

## Проверенный локальный baseline

В репозитории создан и собран `spikes/TaskbarHost`: минимальный WinForms host с tray icon, контекстным меню и mock status window. Сборка на Windows 11 environment прошла успешно: 0 ошибок после restore. Этот spike подтверждает только fallback-слой system tray; native Widgets provider и inline taskbar surface ещё не проверены.
