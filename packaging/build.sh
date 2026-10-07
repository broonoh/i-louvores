#!/usr/bin/env bash
# Gera o pacote distribuível:
#   packaging/build.sh               → dist/i-louvores-<versão>-linux-x64.tar.gz
#   packaging/build.sh linux-arm64   → Raspberry Pi 4/5 e outros ARM 64-bit
#   packaging/build.sh win-x64       → dist/i-louvores-<versão>-win-x64.zip (portátil: extrai e corre, sem instalador)
#   packaging/build.sh --completo    → inclui os louvores, as versões da Bíblia e a configuração deste PC
#                                      (dist/i-louvores-<versão>-<rid>-completo.* — só para uso interno da igreja)
set -euo pipefail

RID="linux-x64"
FULL=0
for arg in "$@"; do
    case "$arg" in
        --completo) FULL=1 ;;
        *) RID="$arg" ;;
    esac
done
WINDOWS=0
[[ "$RID" == win-* ]] && WINDOWS=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/src/Projecao/Projecao.csproj")"
NAME="i-louvores-$VERSION-$RID"
(( FULL )) && NAME="$NAME-completo"
OUT="$ROOT/dist/$NAME"

rm -rf "$OUT" "$ROOT/dist/$NAME.tar.gz" "$ROOT/dist/$NAME.zip"
mkdir -p "$OUT"

# Self-contained: inclui o runtime .NET — o utilizador não precisa de instalar .NET.
dotnet publish "$ROOT/src/Projecao/Projecao.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:InvariantGlobalization=false \
    -o "$OUT/app"

if (( WINDOWS )); then
    : # zip portátil: sem install.sh/.desktop (o Windows usa o ícone do .exe, sem atalho a registar aqui)
else
    cp "$ROOT/packaging/install.sh" "$ROOT/packaging/i-louvores.desktop" "$OUT/"
    cp -r "$ROOT/packaging/icons" "$OUT/"
fi

if (( FULL )); then
    DATA="${XDG_DATA_HOME:-$HOME/.local/share}/i-louvores"
    [[ -f "$DATA/i-louvores.db" ]] || { echo "Não há dados em $DATA"; exit 1; }
    command -v sqlite3 >/dev/null || { echo "Precisa do sqlite3 (ex.: sudo dnf install sqlite)"; exit 1; }
    mkdir -p "$OUT/dados"
    # .backup: cópia consistente mesmo com o I-LOUVORES aberto (WAL)
    sqlite3 "$DATA/i-louvores.db" ".backup '$OUT/dados/i-louvores.db'"
    sqlite3 "$OUT/dados/i-louvores.db" "VACUUM;"
    [[ -f "$DATA/config.json" ]] && cp "$DATA/config.json" "$OUT/dados/"
    DOCS="$(xdg-user-dir DOCUMENTS 2>/dev/null || echo "$HOME/Documents")/I-LOUVORES/Galeria"
    [[ -d "$DOCS" ]] && cp -r "$DOCS" "$OUT/dados/Galeria"
    echo "Dados incluídos: $(sqlite3 "$OUT/dados/i-louvores.db" "select count(*) from Songs") louvores;" \
         "Bíblias: $(sqlite3 "$OUT/dados/i-louvores.db" "select group_concat(Abbreviation, ', ') from BibleVersions")"
fi
if (( WINDOWS )); then
    (cd "$ROOT/dist" && zip -rq "$NAME.zip" "$NAME")
    echo "Pacote: dist/$NAME.zip ($(du -h "$ROOT/dist/$NAME.zip" | cut -f1))"
else
    tar -C "$ROOT/dist" -czf "$ROOT/dist/$NAME.tar.gz" "$NAME"
    echo "Pacote: dist/$NAME.tar.gz ($(du -h "$ROOT/dist/$NAME.tar.gz" | cut -f1))"
fi
