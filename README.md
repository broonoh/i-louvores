# I-LOUVORES — louvores, Bíblia e avisos em projetor ou TV (Linux)

App desktop para **qualquer distribuição Linux** (X11 ou Wayland; KDE, GNOME, XFCE, Cinnamon…),
tendo como referência o Glorifica v6.0.0.1. Feito em .NET 9 + Blazor, com janelas nativas GTK/WebKit (Photino).

## Instalar (utilizador final)

```bash
tar -xzf i-louvores-0.4.0-linux-x64.tar.gz
cd i-louvores-0.4.0-linux-x64
./install.sh            # sem sudo; instala em ~/.local
```

Depois é só abrir **I-LOUVORES** no menu de aplicações, ou correr `i-louvores` no terminal.
O pacote já inclui o .NET. Se faltar alguma coisa do sistema, o instalador mostra o comando da sua distro:

| Distro | Dependências | Codecs p/ vídeo MP4 (opcional) |
|---|---|---|
| Ubuntu / Debian / Mint / Pop!_OS (22.04+ / 12+) | `sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0` | `gstreamer1.0-plugins-good gstreamer1.0-libav` |
| Fedora / RHEL | `sudo dnf install gtk3 webkit2gtk4.1` | `gstreamer1-plugin-openh264` |
| Arch / Manjaro | `sudo pacman -S gtk3 webkit2gtk-4.1` | `gst-plugins-good gst-libav` |
| openSUSE | `sudo zypper install libgtk-3-0 libwebkit2gtk-4_1-0` | `gstreamer-plugins-good gstreamer-plugins-libav` |

Vídeos **WebM** funcionam sem codecs extra. Para desinstalar: `./install.sh --remover` (os dados ficam guardados).

## Levar para outro computador

**Pacote completo (mais simples):** no PC que já tem tudo, gere

```bash
packaging/build.sh --completo    # dist/i-louvores-<versão>-linux-x64-completo.tar.gz
```

Leva o programa **e** os louvores, as versões da Bíblia (ARC, ARA, ACF, KJF…), os avisos, a configuração do telão e a
Galeria. Instala-se da mesma forma (`./install.sh`). Num PC que já tenha dados, eles são **mantidos**; para os trocar
pelos do pacote: `./install.sh --substituir-dados` (com o I-LOUVORES fechado; os anteriores vão para `…/copias`).

> ⚠ O pacote completo contém Bíblias e coletâneas com direitos de autor: use-o só nos computadores da igreja,
> **não o publique** na internet. Para distribuir publicamente, use o pacote normal.

**Ou, com o pacote normal + cópia de dados** (o pacote normal traz só o programa e a Bíblia Livre):

1. **No PC de origem:** barra superior → **💾 Dados** → **📤 Exportar** (marque *Incluir a Galeria* para levar também
   as imagens e os vídeos de fundo). É criado um ficheiro `.zip`.
2. Instale o I-LOUVORES no outro PC (secção acima) e copie para lá o `.zip`.
3. **No outro PC:** **💾 Dados** → **📥 Importar** → escolha o `.zip` → **⟳ Reiniciar agora e aplicar**.

Antes de restaurar, é guardada uma cópia dos dados atuais em `~/.local/share/i-louvores/copias`.

## Usar com projetor ou TV

1. Ligue o projetor/TV e, nas definições de ecrã do sistema, escolha **Estender** (não *Espelhar/Duplicar*).
2. Abra a Projeção. O seletor mostra os monitores com modelo, resolução e Hz
   (ex.: `Monitor 2 · LF24T35 — 1920x1080 @ 75Hz`) e escolhe sozinho o que **não** tem o painel.
3. **TV cortando as bordas?** Use *Margem TV (overscan)* no painel (2–7 %).
4. Enquanto projeta, o **protetor de ecrã e a suspensão ficam desativados**, e voltam ao normal quando clica em Parar.

| Tecla | Ação |
|---|---|
| `Alt+F10` | Freeze (congela o telão enquanto navega) |
| `Alt+F11` | Blank Screen (ecrã preto) |
| `→` `↓` `PageDown` `Espaço` | Próximo |
| `←` `↑` `PageUp` | Anterior |

### Vindo do Glorifica?
Aba **Louvores → Importar ▾ → Trazer do Glorifica**. Se o Glorifica estiver instalado neste PC (Bottles, Wine ou PlayOnLinux)
ou se copiar a pasta `Documentos\Glorifica` do Windows para `~/Documentos/Glorifica`, o I-LOUVORES copia num clique:
as letras (pasta `Louvores/raw`), as imagens da Galeria e os modelos, e os fundos que estavam escolhidos
(1.º slide, restantes, Bíblia, cronómetro). As marcações do Glorifica (`Coro`, `*Coro`, `Final`, `(H)`/`(M)`, `/`)
são projetadas da mesma forma. Pode repetir sem criar duplicados.

### Abas
| Aba | Para quê |
|---|---|
| **Louvores** | procurar, ver os slides e projetar/adicionar à fila |
| **Bíblia** | versão → livro → capítulo → versículo(s); "Ir para: Jo 3:16-18"; importar versões (OSIS, Zefania, OpenSong, JSON, CSV, TXT, ZIP) |
| **Geral** | cronómetro (h/m/s, ou progressivo a partir de 00:00:00) e relógio, em ecrã inteiro ou em *Overlay* |
| **Avisos** | editor formatado (tamanho, atributos, fonte, cor, fundo, alinhamento), modelos, abrir/salvar |
| **Galeria** | fundos: louvor 1.º slide, louvor restantes, Bíblia e Geral |
| **Editor** | **Louvores:** corrigir/criar/duplicar/apagar, colorir palavras (🎨), pré-visualização dos slides. **Bíblia:** corrigir o texto de um versículo. O que está na fila atualiza na hora |
| **Configuração** | fonte, tamanho e cores do telão para louvores, Bíblia e relógio. Mostra uma pré-visualização, e nada muda no telão até clicar em **✔ Aplicar no telão** |

Ao mudar de aba, a **fila de exibição** é limpa (o que está no telão continua lá).

**Busca de louvores:** pelo nome, por um trecho da letra ou pelo **número** (`12` = nº 12 de todas as coletâneas) ou por **coletânea e número** (`CIAS 12`, `12 CIAS`, `2018 7`). A busca ignora acentos,
maiúsculas/minúsculas, pontuação e "oh"/"ó" (`jesus e o caminho` encontra "JESUS, É O CAMINHO!"). Aguenta
milhares de louvores (a lista é virtualizada).

### Cores só numa parte do texto
- **Configuração → Louvor:** escolha o louvor na busca, **arraste o rato** sobre as palavras (ex.: só "EM NÓS")
  e clique numa cor. Só esse trecho, e só nesse louvor, fica colorido. **Clicar sem selecionar** muda o elemento
  (título, letra, coro, (H)/(M), parênteses) em **todos** os louvores.
- **Editor → Louvores:** selecione o texto na letra e use a barra 🎨.
- Na letra fica guardado como `[cor=#facc15]EM NÓS[/cor]` (pode escrever à mão).

### Marcações da letra (compatíveis com o Glorifica)
| Marcação | Efeito |
|---|---|
| linha em branco | novo slide |
| `Coro` na 1.ª linha da estrofe | refrão, repetido automaticamente depois de cada estrofe |
| `*Coro` | refrão sem repetição automática (o louvor já tem as repetições escritas) |
| `Final` | estrofe final |
| `(H)` / `(M)` no início da linha | homens / mulheres (cores próprias) |
| `(texto)` | parênteses com estilo próprio (ex.: `(BIS)`) |
| `/` no início da linha | linha em branco dentro do mesmo slide |
| `[cor=#RRGGBB]…[/cor]` | cor só nesse trecho |

As letras são projetadas **em maiúsculas**, alinhadas à esquerda dentro de um bloco centrado, sem quebrar linhas
(o tamanho da letra ajusta-se para caber).

Tema do painel: **Automático / Claro / Escuro** (botão na barra superior). O telão fica sempre escuro.

**Bíblia:** vem com a **Bíblia Livre (BLIVRE)** — CC BY 3.0 BR. ARC, ARA, ACF e KJF têm direitos de autor
(Sociedade Bíblica do Brasil e outras), por isso **não vão no pacote**: importe-as pela aba Bíblia → *Versões / Importar*
se a igreja tiver os ficheiros e o direito de uso, ou leve-as com a cópia de dados (💾 Dados).
Ao projetar, **Próximo** avança versículo a versículo e passa sozinho para o capítulo seguinte; a aba Bíblia acompanha
o versículo que está no telão.

### Galeria (fundos)
- Clique num cartão para o usar como **fundo dos slides** (marca **SELECIONADO!**).
- **1.º slide** define o fundo só do primeiro slide de cada louvor (ex.: a imagem com a faixa "LOUVOR", como no Glorifica).
- **＋ Adicionar ficheiros** copia imagens/vídeos para a Galeria; também pode copiá-los diretamente para a pasta, e a grelha atualiza-se sozinha.
- **Projetar** num cartão põe só essa mídia na fila (ex.: vídeo de abertura).

### Importar louvores (aba Louvores → 📥 Importar louvores)
- **TXT**: um ou vários louvores por ficheiro (separados por `---`), linha em branco = novo slide,
  cabeçalho opcional (`Título:`, `Número:`, `Coletânea:`, `Autor:`, `Tom:`, `Ritmo:`) ou 1.ª linha `12 - Título`.
- **CSV** (Excel/LibreOffice, `;` ou `,`): colunas `titulo` e `letra` + opcionais.
- **OpenLyrics (.xml)**: exportado do OpenLP e outros; respeita a ordem de execução (refrão repetido).
- **PowerPoint (.pptx)**: coletâneas em que cada louvor começa num slide com o título no topo (`12 – TÍTULO`).
  As anotações ao lado da letra (BIS, 2X, VARÕES, SERVAS) passam a `(BIS)`, `(H)`, `(M)`. Use a versão **sem animações**
  da apresentação. A coletânea sai da capa ("COLETÂNEA … EDIÇÃO 2018") ou pode ser indicada na importação.
- **Atualizações:** se um louvor já existir e a letra mudou, a pré-visualização
  mostra-o como *atualizar*. Ao confirmar, a letra antiga é **substituída**, e antes disso é feita uma cópia de segurança.
- Aceita UTF-8 e Windows-1252 (ficheiros antigos). Mostra a pré-visualização (novo / atualizar / ignorar) e grava tudo numa transação.

Pastas: fundos em `~/Documentos/I-LOUVORES/Galeria`, base de dados e configuração em `~/.local/share/i-louvores` (as pastas antigas "Glorifica" da v0.3 são migradas sozinhas).

## Arquitetura

```
 Processo principal                                   Processo do projetor
 ┌─────────────────────────────────────────┐          ┌──────────────────────────┐
 │ Kestrel 127.0.0.1:<porta aleatória>     │  stdin   │ Janela Photino sem bordas│
 │  ├─ Blazor Server (2 circuitos)         │◄─────────┤ gtk_window_fullscreen_   │
 │  ├─ ProjectionService (Singleton) ★     │ monitor/ │   on_monitor()           │
 │  ├─ SQLite (EF Core)                    │  close   │ carrega /projetor ───────┼──┐
 │  └─ D-Bus: inibe protetor de ecrã       │          └──────────────────────────┘  │
 │ Janela Photino do painel ── carrega / ──┼──────────────── WebSocket ─────────────┘
 └─────────────────────────────────────────┘
```

- **Porquê dois processos?** No Linux, o Photino corre cada janela extra num loop GTK aninhado que bloqueia a UI.
  Com um processo por janela isso não acontece, e se o projetor falhar o painel continua de pé.
  Se o painel morrer, o projetor fecha-se sozinho (EOF no stdin), por isso nunca fica imagem presa no telão.
- **Porquê `gtk_window_fullscreen_on_monitor`?** No Wayland as janelas não podem posicionar-se sozinhas;
  o pedido "fullscreen neste monitor" é a forma suportada por KWin, Mutter e wlroots, e também funciona em X11.
- **Segurança:** o servidor só escuta em loopback e exige um token aleatório por sessão (cookie HttpOnly).

```
src/Projecao/
├── Program.cs                 Entrada: painel ou --projetor
├── Desktop/                   ★ Camada Linux
│   ├── DesktopHost.cs         Servidor + janela do painel
│   ├── ProjectorHost.cs       Processo do projetor (comandos por stdin)
│   ├── ProjectorProcessManager.cs  Abre/move/fecha o projetor
│   ├── Gtk.cs                 P/Invoke GTK3/GDK (monitores, fullscreen)
│   ├── GtkDisplayService.cs   Lista de monitores (modelo, resolução, Hz)
│   ├── ScreenSaverInhibitor.cs  D-Bus freedesktop/GNOME
│   └── LocalAccess.cs         Token de acesso local
├── Services/                  ProjectionService ★, Galeria, configurações…
├── Models/ Data/              EF Core + SQLite
└── Components/                Razor: painel, ProjectionView ★, módulo Louvores
packaging/                     build.sh, install.sh, .desktop, ícone
tests/Projecao.Tests/          xUnit
```

## Desenvolvimento

```bash
dotnet run --project src/Projecao        # abre o painel
dotnet test tests/Projecao.Tests         # testes
packaging/build.sh                       # gera dist/i-louvores-<versão>-linux-x64.tar.gz
packaging/build.sh --completo            # + louvores, Bíblias, configuração e Galeria deste PC
packaging/build.sh linux-arm64           # Raspberry Pi 4/5
```

Em Debug, o log mostra a URL do painel com token, útil para depurar num browser.

> Depois de mudar `AssemblyName`, faça uma compilação limpa (`rm -rf bin obj`): o CSS isolado dos componentes fica em cache com o nome antigo.

## Notas

- **Louvores do Glorifica (`PT_*.xbY`)**: são ficheiros proprietários cifrados e **não são importados** (nem se contorna a cifra).
  Use a pasta `Louvores/raw` do Glorifica, PPTX, TXT, CSV ou OpenLyrics.
- **Bíblia**: a Bíblia Livre vem embutida (`Resources/Bible`); as outras versões são importadas por cada igreja.
- **Schema**: `EnsureCreated()` + `SchemaUpgrader` (`PRAGMA user_version`, atualmente v2) para as alterações seguintes.
- **Dados**: base de dados em `~/.local/share/i-louvores/i-louvores.db` (SQLite, WAL); cópias automáticas em `…/copias`.

## Próximos passos
1. Flatpak (Flathub) como alternativa ao tar.gz
2. Versão para Windows (por agora, no Windows continua-se a usar o Glorifica)
