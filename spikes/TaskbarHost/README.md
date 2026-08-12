# TaskbarHost spike

Минимальная проверка Windows baseline:

- процесс живёт в фоне;
- иконка появляется в system tray;
- двойной клик и пункт меню открывают статусное окно;
- приложение завершается через контекстное меню и корректно освобождает tray icon.

Данные намеренно mock. Авторизация и подключение к ChatGPT/Claude в этот spike не входят.

Запуск из корня проекта:

`powershell
dotnet run --project .\spikes\TaskbarHost\TaskbarHost.csproj
`
