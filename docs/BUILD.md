# Сборка

Windows x64, .NET Framework 4.8 и Python 3.9+. Скрипт bootstrap скачивает LLVM-MinGW и заголовки Windows SDK в `.tools`, сверяя SHA-256. Конечному пользователю эти инструменты не нужны.

Из корня проекта:

```powershell
python scripts\bootstrap-native.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-native.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1
```

Результат: `build/YukisPadSetup.exe`. Установщик содержит приложение, аудиокомпонент и стартовые звуки.

## Проверки

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-native.ps1
```

17 тестов приложения, 42 нативные проверки и проверка окон WPF: импорт, обрезка, сохранение, восстановление, микширование, горячие клавиши, поиск и трей. Результаты записываются в `build/`.

Установщик проверяет передачу тестового тона через выбранный физический микрофон в режимах Default, Communications, RAW и RAW Communications. При ошибке он восстанавливает исходные настройки. Голос в файл не записывается.

После установки `YukisPad.exe --route-test` проверяет весь путь от проигрывателя до захвата микрофона. Результаты: `%LOCALAPPDATA%\YukisPad\route-test`. Для проверки удаления и восстановления используется `YukisPadSetup.exe --test-lifecycle --endpoint={GUID}`; приложение нужно закрыть, звук Windows будет перезапущен.

Версия 0.2.1 проверена на Windows 10 22H2 с Graphite 2 Wireless. Работа звуков в Discord подтверждена пользователем. Другие устройства и Windows 11 отдельно не проверялись.

## Устройство проекта

- `src/` — WPF, библиотека, Media Foundation, проигрыватель.
- `native/` — микшер APO, транспорт PCM, тесты.
- `installer/` — установка, резервное копирование и восстановление настроек микрофона.
- `starter-sounds/` — стартовая подборка и источники записей.

Микшер подключается как EFX к физическому входу, поэтому работает и при RAW-захвате. В отсутствие воспроизведения голос проходит без изменения. При аварийном закрытии проигрывателя подмешивание прекращается в течение секунды.

MP3, WAV, AAC/M4A и WMA декодируются средствами Windows. Остальные форматы зависят от системных кодеков. Импорт ограничен 512 МБ, 30 минутами, моно/стерео и частотой 8–192 кГц. Эксклюзивный захват не проверен.
