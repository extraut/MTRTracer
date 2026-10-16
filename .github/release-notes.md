**MTRTracer 1.1.1** — [Русский](#русский) · [English](#english)

## Русский

Трассировка маршрута в духе WinMTR с современным интерфейсом: статистика по каждому хопу, карта маршрута, лог пакетов, VPN-режим и история замеров.

**Что нового**

- **Настройки и данные** (шестерёнка в заголовке): версии офлайн-базы DB-IP — скачать новейшую, выбрать месяц, закрепить, удалить; своя база MaxMind GeoLite2 / DB-IP / IPinfo из файла или по ссылке; адрес загрузки DB-IP можно поменять.
- **Источники**: каждый источник геоданных включается отдельно, видно, что уходит в сеть; очистка кэша.
- **Обновления**: проверка последнего релиза на GitHub по кнопке; можно указать свой форк.
- **Английский язык** интерфейса и всех сообщений.
- **Темы**: светлая, Catppuccin Mocha, Nord, системная.

Проект выложен как публичный архив: регулярных обновлений не будет, но базы и источники обновляются прямо в программе.

## English

A route tracer in the spirit of WinMTR with a modern interface: per-hop statistics, a route map, a packet log, VPN mode and measurement history.

**What's new**

- **Settings and data** (gear icon in the title bar): DB-IP offline database releases — download the newest, pick a month, pin, delete; your own MaxMind GeoLite2 / DB-IP / IPinfo database from a file or a link; editable DB-IP download address.
- **Sources**: every geo source has its own switch and shows what it sends; clear the cache.
- **Updates**: check the latest GitHub release with one button; point it at your own fork.
- **English** interface and messages.
- **Themes**: Light, Catppuccin Mocha, Nord, System.

The project is published as a public archive: no regular updates are planned, but databases and sources are updated from inside the app.

## Files / Файлы

| File | For |
|---|---|
| `MTRTracer-windows-x64.exe` | Windows 10/11 x64, single file, no install; needs WebView2 Runtime (built into Windows 11) |
| `mtrtracer-linux-x64.tar.gz` | Linux x64: program, icon, desktop entry, `install.sh` |
| `mtrtracer-linux-arm64.tar.gz` | Linux ARM64 (Raspberry Pi 4/5, ARM servers) |
| `SHA256SUMS.txt` | checksums |

**Windows:** download and run. The exe is not signed, SmartScreen may ask: "More info" → "Run anyway" / «Подробнее» → «Выполнить в любом случае».

**Linux:**

```bash
sudo apt install libwebkit2gtk-4.1-0 libnotify4    # window; without it: mtrtracer --browser
tar -xzf mtrtracer-linux-x64.tar.gz && cd linux-x64 && ./install.sh
mtrtracer
```

Built by GitHub Actions from this tag's sources (workflow `Release`), without debug symbols.
Docs: https://extraut.github.io/MTRTracer/docs.html
