Трассировка маршрута в духе WinMTR с современным интерфейсом: статистика по каждому хопу, карта маршрута, лог пакетов, VPN-режим и история замеров.

| Файл | Для чего |
|---|---|
| `MTRTracer-windows-x64.exe` | Windows 10/11 x64. Один файл, установка не нужна, нужен WebView2 Runtime (в Windows 11 уже есть) |
| `mtrtracer-linux-x64.tar.gz` | Linux x64: программа, иконка, ярлык, `install.sh` |
| `mtrtracer-linux-arm64.tar.gz` | Linux ARM64 (Raspberry Pi 4/5, ARM-серверы) |
| `SHA256SUMS.txt` | контрольные суммы |

**Windows:** скачать exe и запустить. Файл не подписан, SmartScreen может спросить подтверждение: «Подробнее» → «Выполнить в любом случае».

**Linux:**

```bash
sudo apt install libwebkit2gtk-4.1-0 libnotify4    # окно; без него работает mtrtracer --browser
tar -xzf mtrtracer-linux-x64.tar.gz && cd linux-x64 && ./install.sh
mtrtracer
```

Файлы собраны GitHub Actions из исходников этого тега (workflow `Release`), без отладочных символов.
Документация: https://extraut.github.io/MTRTracer/docs.html
