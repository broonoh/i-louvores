#!/usr/bin/env bash
# Instalador do I-LOUVORES (por utilizador, sem root).
#   ./install.sh            instala/atualiza em ~/.local
#   ./install.sh --remover  desinstala (mantém dados e Galeria)
#   ./install.sh --substituir-dados   (pacote "completo") troca os dados deste PC pelos do pacote
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA_HOME/i-louvores-app"
BIN_LINK="$HOME/.local/bin/i-louvores"
DESKTOP_FILE="$DATA_HOME/applications/i-louvores.desktop"
ICON_ROOT="$DATA_HOME/icons/hicolor"
USER_DATA="$DATA_HOME/i-louvores"          # louvores, Bíblias, avisos (i-louvores.db) e config.json

# Instalações antigas (nome "Projeção", v0.1–0.3)
remove_legacy() {
    rm -rf "$DATA_HOME/projecao-app" "$HOME/.local/bin/projecao" "$DATA_HOME/applications/projecao.desktop" \
           "$ICON_ROOT/scalable/apps/projecao.svg"
}

if [[ "${1:-}" == "--remover" ]]; then
    rm -rf "$APP_DIR" "$BIN_LINK" "$DESKTOP_FILE"
    find "$ICON_ROOT" -name "i-louvores.png" -delete 2>/dev/null || true
    remove_legacy
    echo "I-LOUVORES removido. Os dados continuam em ~/.local/share/i-louvores e ~/Documentos/I-LOUVORES."
    exit 0
fi

# ---------- Dependências de sistema (variam por distribuição) ----------
distro_id() {
    [[ -r /etc/os-release ]] || { echo unknown; return; }
    . /etc/os-release
    echo "${ID:-} ${ID_LIKE:-}"
}

has_lib() { ldconfig -p 2>/dev/null | grep -q "$1" || compgen -G "/usr/lib*/**/$1*" >/dev/null 2>&1 || compgen -G "/usr/lib*/$1*" >/dev/null 2>&1; }

missing=()
has_lib "libgtk-3.so.0"          || missing+=("GTK 3")
has_lib "libwebkit2gtk-4.1.so.0" || missing+=("WebKitGTK 4.1")

ids="$(distro_id)"
case " $ids " in
    *" debian "*|*" ubuntu "*) deps="sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0"
                               codecs="sudo apt install gstreamer1.0-plugins-good gstreamer1.0-libav" ;;
    *" fedora "*|*" rhel "*)   deps="sudo dnf install gtk3 webkit2gtk4.1"
                               codecs="sudo dnf install gstreamer1-plugin-openh264   (ou gstreamer1-libav do RPM Fusion)" ;;
    *" arch "*)                deps="sudo pacman -S gtk3 webkit2gtk-4.1"
                               codecs="sudo pacman -S gst-plugins-good gst-libav" ;;
    *" suse "*|*" opensuse "*) deps="sudo zypper install libgtk-3-0 libwebkit2gtk-4_1-0"
                               codecs="sudo zypper install gstreamer-plugins-good gstreamer-plugins-libav" ;;
    *)                         deps="instale GTK 3 e WebKitGTK 4.1 (libwebkit2gtk-4.1) pelo gestor de pacotes"
                               codecs="instale os plugins GStreamer 'good' e 'libav'" ;;
esac

if (( ${#missing[@]} )); then
    echo "⚠  Faltam bibliotecas do sistema: ${missing[*]}"
    echo "   Instale com:  $deps"
    echo "   Depois volte a correr este instalador."
    exit 1
fi

# ---------- Dados incluídos (pacote "completo": louvores e versões da Bíblia) ----------
install_bundled_data() {
    [[ -f "$HERE/dados/i-louvores.db" ]] || return 0
    local db="$USER_DATA/i-louvores.db"

    if [[ -f "$db" && "${1:-}" != "--substituir-dados" ]]; then
        echo "ℹ  Este PC já tem dados do I-LOUVORES — foram mantidos."
        echo "   Para os trocar pelos louvores e Bíblias deste pacote:  ./install.sh --substituir-dados"
        return 0
    fi
    mkdir -p "$USER_DATA"
    if [[ -f "$db" ]]; then
        if pgrep -x i-louvores >/dev/null 2>&1; then
            echo "⚠  Feche o I-LOUVORES antes de substituir os dados e volte a correr o instalador."
            exit 1
        fi
        local copy="$USER_DATA/copias/antes-do-pacote-$(date +%Y%m%d-%H%M%S)"
        mkdir -p "$copy"
        mv "$db" "$copy/"
        for f in "$db-wal" "$db-shm" "$USER_DATA/config.json"; do [[ -f "$f" ]] && mv "$f" "$copy/"; done
        echo "   Dados anteriores guardados em: $copy"
    fi
    cp "$HERE/dados/i-louvores.db" "$db"
    [[ -f "$HERE/dados/config.json" && ! -f "$USER_DATA/config.json" ]] && cp "$HERE/dados/config.json" "$USER_DATA/"
    if [[ -d "$HERE/dados/Galeria" ]]; then
        local gallery
        gallery="$(xdg-user-dir DOCUMENTS 2>/dev/null || echo "$HOME/Documents")/I-LOUVORES/Galeria"
        mkdir -p "$gallery"
        cp -rn "$HERE/dados/Galeria/." "$gallery/"   # não substitui ficheiros que já lá estejam
    fi
    echo "✔ Louvores e versões da Bíblia do pacote instalados."
}

# ---------- Instalação ----------
remove_legacy
mkdir -p "$APP_DIR" "$(dirname "$BIN_LINK")" "$(dirname "$DESKTOP_FILE")" "$ICON_ROOT"
rm -rf "$APP_DIR"/*
cp -a "$HERE/app/." "$APP_DIR/"
chmod +x "$APP_DIR/i-louvores"
ln -sf "$APP_DIR/i-louvores" "$BIN_LINK"
cp -r "$HERE/icons/hicolor/." "$ICON_ROOT/"
sed "s|@EXEC@|$APP_DIR/i-louvores|" "$HERE/i-louvores.desktop" > "$DESKTOP_FILE"
command -v update-desktop-database >/dev/null && update-desktop-database "$(dirname "$DESKTOP_FILE")" 2>/dev/null || true
# (sem gtk-update-icon-cache na pasta do utilizador: uma cache sem index.theme esconde ícones novos)
rm -f "$ICON_ROOT/icon-theme.cache"
command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 --noincremental >/dev/null 2>&1 || true
install_bundled_data "${1:-}"

echo "✔ I-LOUVORES instalado. Abra pelo menu de aplicações ou com o comando: i-louvores"
echo "  Galeria de fundos: ~/Documentos/I-LOUVORES/Galeria"
echo "  Tem uma instalação antiga neste PC (Bottles/Wine)? Aba Louvores → \"Importar ▾ → De instalação antiga\"."
echo "  Vídeos MP4 (H.264) de fundo precisam de codecs: $codecs"
echo "  (WebM funciona sem codecs extra.)"
