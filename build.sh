#!/usr/bin/env bash
# Сборка MTRTracer для Linux.
#
#   ./build.sh                 linux-x64, самодостаточный бинарник + архив
#   ./build.sh --arch arm64    linux-arm64 (Raspberry Pi 4/5, ARM-серверы)
#   ./build.sh --skip-tests    без юнит-тестов
#
# Результат: dist-linux/mtrtracer-linux-<arch>.tar.gz
#   внутри MTRTracer, иконка, ярлык .desktop и install.sh
set -euo pipefail
cd "$(dirname "$0")"

ARCH=x64
SKIP_TESTS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --skip-tests) SKIP_TESTS=1; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Неизвестный параметр: $1" >&2; exit 1 ;;
  esac
done
RID="linux-$ARCH"

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "Не найдено: $1. $2" >&2; exit 1; }; }

need dotnet "Установите .NET 10 SDK: https://dotnet.microsoft.com/download (или пакет dotnet-sdk-10.0 вашего дистрибутива)."
need npm "Установите Node.js 20+ (пакет nodejs или https://nodejs.org)."

step "1/3 Интерфейс (React + Vite) -> ui/dist"
( cd ui && if [[ -f package-lock.json ]]; then npm ci; else npm install; fi && npm run build )

if [[ $SKIP_TESTS -eq 0 ]]; then
  step "2/3 Юнит-тесты (в сеть не ходят)"
  dotnet test src/MTRTracer.Tests -c Release --nologo
else
  step "2/3 Тесты пропущены"
fi

step "3/3 Публикация $RID"
OUT="dist-linux/$RID"
rm -rf "$OUT" && mkdir -p "$OUT"
dotnet publish src/MTRTracer.Linux -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o "$OUT" --nologo

cp src/MTRTracer.Linux/mtrtracer.png "$OUT/"
cp packaging/linux/mtrtracer.desktop packaging/linux/install.sh "$OUT/"
chmod +x "$OUT/MTRTracer" "$OUT/install.sh"

TAR="dist-linux/mtrtracer-$RID.tar.gz"
tar -czf "$TAR" -C dist-linux "$RID"
printf '\n\033[32mГотово: %s\033[0m\n' "$TAR"
echo "Установка: tar -xzf $TAR && cd $RID && ./install.sh"
