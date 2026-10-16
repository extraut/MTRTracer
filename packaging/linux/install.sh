#!/usr/bin/env bash
# Установка MTRTracer для текущего пользователя (без root):
#   ~/.local/opt/mtrtracer/MTRTracer, команда mtrtracer, ярлык в меню приложений.
#
#   ./install.sh            установить
#   ./install.sh --setcap   ещё и дать право на raw-сокеты (нужен sudo), если ping-сокеты запрещены
#   ./install.sh --remove   удалить
set -euo pipefail
cd "$(dirname "$0")"

PREFIX="$HOME/.local/opt/mtrtracer"
BIN="$HOME/.local/bin"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor/256x256/apps"

if [[ "${1:-}" == "--remove" ]]; then
  rm -rf "$PREFIX" "$BIN/mtrtracer" "$APPS/mtrtracer.desktop" "$ICONS/mtrtracer.png"
  echo "MTRTracer удалён. Данные пользователя: ~/.local/share/MTRTracer (удалите вручную при желании)."
  exit 0
fi

mkdir -p "$PREFIX" "$BIN" "$APPS" "$ICONS"
install -m 755 MTRTracer "$PREFIX/MTRTracer"
install -m 644 mtrtracer.png "$ICONS/mtrtracer.png"
ln -sf "$PREFIX/MTRTracer" "$BIN/mtrtracer"
sed "s|@EXEC@|$PREFIX/MTRTracer|" mtrtracer.desktop > "$APPS/mtrtracer.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" >/dev/null 2>&1 || true

echo "Установлено: $PREFIX/MTRTracer (команда: mtrtracer)"

# Окно: WebKitGTK 4.1. Без него программа откроет интерфейс в браузере.
if ! ldconfig -p 2>/dev/null | grep -q "libwebkit2gtk-4.1.so.0"; then
  echo
  echo "Для собственного окна нужен WebKitGTK 4.1:"
  echo "  Debian/Ubuntu:  sudo apt install libwebkit2gtk-4.1-0 libnotify4"
  echo "  Fedora:         sudo dnf install webkit2gtk4.1 libnotify"
  echo "  Arch:           sudo pacman -S webkit2gtk-4.1 libnotify"
  echo "Без него запускайте: mtrtracer --browser"
fi

# ICMP без root: ping-сокеты разрешены, если группа пользователя входит в net.ipv4.ping_group_range.
RANGE=$(cat /proc/sys/net/ipv4/ping_group_range 2>/dev/null || echo "1 0")
LOW=${RANGE%%[[:space:]]*}; HIGH=${RANGE##*[[:space:]]}
GID=$(id -g)
if [[ "$GID" -ge "$LOW" && "$GID" -le "$HIGH" ]]; then
  echo "ICMP без root: разрешено (ping_group_range = $RANGE)."
elif [[ "${1:-}" == "--setcap" ]]; then
  sudo setcap cap_net_raw+ep "$PREFIX/MTRTracer" && echo "Выдано право cap_net_raw: трассировка через raw-сокеты."
else
  echo
  echo "ICMP без root сейчас запрещён (ping_group_range = $RANGE). Один из вариантов:"
  echo "  sudo sysctl -w net.ipv4.ping_group_range=\"0 2147483647\"     # до перезагрузки"
  echo "  echo 'net.ipv4.ping_group_range = 0 2147483647' | sudo tee /etc/sysctl.d/60-mtrtracer.conf"
  echo "  ./install.sh --setcap                                          # или право на raw-сокеты только для программы"
fi
