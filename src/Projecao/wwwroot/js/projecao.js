// Funções JS usadas pelos componentes Blazor (painel e projeção).
window.projecao = (() => {
    let hotkeyRef = null;
    let hotkeyHandler = null;
    let lastFit = null;

    const isEditable = (el) =>
        !!el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));

    // ------------------------------------------------------------------
    // Atalhos de teclado
    //   Alt+F10 → Freeze | Alt+F11 → Blank Screen
    //   → ↓ PageDown Espaço → Próximo | ← ↑ PageUp → Anterior
    // As setas são ignoradas quando o foco está num campo de texto.
    // Capturamos em "capture phase" para antecipar o WebKit (F10 abre menus no GTK).
    // ------------------------------------------------------------------
    function registerHotkeys(dotnetRef) {
        hotkeyRef = dotnetRef;
        if (hotkeyHandler) return;

        hotkeyHandler = (e) => {
            let action = null;

            if (e.altKey && e.key === 'F10') action = 'freeze';
            else if (e.altKey && e.key === 'F11') action = 'blank';
            else if (!e.altKey && !e.ctrlKey && !e.metaKey && !isEditable(e.target)) {
                switch (e.key) {
                    case 'ArrowRight': case 'ArrowDown': case 'PageDown': action = 'next'; break;
                    case 'ArrowLeft': case 'ArrowUp': case 'PageUp': action = 'previous'; break;
                    case ' ': if (e.target.tagName !== 'BUTTON') action = 'next'; break;
                }
            }

            if (!action || !hotkeyRef) return;
            e.preventDefault();
            e.stopPropagation();
            hotkeyRef.invokeMethodAsync('OnHotkey', action).catch(console.error);
        };

        document.addEventListener('keydown', hotkeyHandler, true);
    }

    function unregisterHotkeys() {
        if (hotkeyHandler) document.removeEventListener('keydown', hotkeyHandler, true);
        hotkeyHandler = null;
        hotkeyRef = null;
    }

    // ------------------------------------------------------------------
    // Ajuste automático do tamanho do texto (pesquisa binária).
    // Encontra o maior font-size em que o texto cabe no elemento-pai.
    // ------------------------------------------------------------------
    // maxVh: tamanho máximo em % da altura do ecrã (aba Configuração); maxPx tem prioridade.
    function fitText(el, maxPx, minPx, maxVh) {
        if (!el || !el.parentElement) return;
        lastFit = { el, maxPx, minPx, maxVh };

        const box = el.parentElement;
        const max = maxPx > 0 ? maxPx : Math.round(window.innerHeight * ((maxVh > 0 ? maxVh : 11) / 100));
        const min = minPx > 0 ? minPx : 14;
        let lo = min, hi = Math.max(min, max), best = min;

        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            el.style.fontSize = mid + 'px';
            if (el.scrollHeight <= box.clientHeight && el.scrollWidth <= box.clientWidth) {
                best = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        el.style.fontSize = best + 'px';
    }

    // A janela muda de tamanho ao ser movida para o projetor → reajustar.
    let resizeTimer = 0;
    window.addEventListener('resize', () => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => {
            if (lastFit && lastFit.el.isConnected) fitText(lastFit.el, lastFit.maxPx, lastFit.minPx, lastFit.maxVh);
        }, 100);
    });

    // Página do projetor: esconde o aviso de "a religar" do Blazor (nunca no telão)
    // e o cursor do rato.
    function markProjector() {
        document.documentElement.classList.add('projector');
    }

    // ------------------------------------------------------------------
    // Editor de avisos (contenteditable): formatação da SELEÇÃO, padrão do I-LOUVORES.
    // A seleção é guardada porque clicar nas listas da barra tira o foco do editor.
    // ------------------------------------------------------------------
    const editors = new WeakMap();

    function editorInit(el, dotnetRef, html) {
        el.innerHTML = html || '';
        const state = { range: null, ref: dotnetRef, timer: 0 };
        editors.set(el, state);
        const save = () => {
            const sel = window.getSelection();
            if (sel.rangeCount && el.contains(sel.anchorNode)) state.range = sel.getRangeAt(0).cloneRange();
        };
        document.addEventListener('selectionchange', save);
        el.addEventListener('input', () => {
            clearTimeout(state.timer);
            state.timer = setTimeout(() => state.ref.invokeMethodAsync('OnEditorChanged', el.innerHTML).catch(console.error), 250);
        });
        // Colar como texto simples (evita estilos estranhos vindos de sites/Word).
        el.addEventListener('paste', e => {
            e.preventDefault();
            document.execCommand('insertText', false, (e.clipboardData || window.clipboardData).getData('text/plain'));
        });
    }

    function restore(el) {
        const state = editors.get(el);
        el.focus();
        if (state && state.range) {
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(state.range);
        }
    }

    function notify(el) {
        const state = editors.get(el);
        if (state) state.ref.invokeMethodAsync('OnEditorChanged', el.innerHTML).catch(console.error);
    }

    function editorExec(el, command, value) {
        restore(el);
        document.execCommand('styleWithCSS', false, true);
        document.execCommand(command, false, value ?? null);
        notify(el);
    }

    // Tamanho relativo em "em" (o projetor ajusta a base; as proporções mantêm-se).
    function editorFontSize(el, em) {
        restore(el);
        document.execCommand('styleWithCSS', false, false);
        document.execCommand('fontSize', false, '7');
        el.querySelectorAll('font[size="7"]').forEach(f => {
            const span = document.createElement('span');
            span.style.fontSize = em + 'em';
            while (f.firstChild) span.appendChild(f.firstChild);
            f.replaceWith(span);
        });
        notify(el);
    }

    function editorSetHtml(el, html) {
        el.innerHTML = html || '';
        const state = editors.get(el);
        if (state) state.range = null;
    }

    // Pré-visualização (aba Configuração): mesmo ajuste do telão, mas relativo à miniatura.
    // maxPct = tamanho máximo em % da altura do "ecrã" (a miniatura 16:9).
    // maxWFrac = fração da largura do ecrã disponível para o texto (0.94 = quase a largura toda;
    // menor para faixas estreitas, como o título/referência, que não ocupam o ecrã inteiro).
    function fitPreview(el, maxPct, maxWFrac) {
        if (!el) return;
        const screen = el.closest('.cfg-screen');
        if (!screen) return;
        const maxW = screen.clientWidth * (maxWFrac > 0 ? maxWFrac : 0.94), maxH = el.clientHeight || screen.clientHeight * 0.8;
        let lo = 4, hi = Math.max(4, Math.round(screen.clientHeight * maxPct / 100)), best = lo;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            el.style.fontSize = mid + 'px';
            if (el.scrollWidth <= maxW && el.scrollHeight <= maxH + 1) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        el.style.fontSize = best + 'px';
    }

    // Editor de louvores: cor só no texto selecionado da letra → [cor=#hex]…[/cor].
    // hex = null remove a cor da seleção. Devolve false se não houver seleção.
    const COLOR_TAG = /\[(?:cor=[^\]\s]{1,20}|\/cor)\]/gi;
    function colorSelection(textarea, hex) {
        if (!textarea) return false;
        let start = textarea.selectionStart, end = textarea.selectionEnd;
        if (start === end) return false;
        const value = textarea.value;

        // Se a seleção está logo dentro de marcas existentes, inclui-as (para trocar/remover a cor).
        const before = value.slice(0, start), after = value.slice(end);
        const openBefore = before.match(/\[cor=[^\]\s]{1,20}\]$/i);
        const closeAfter = after.match(/^\[\/cor\]/i);
        if (openBefore && closeAfter) { start -= openBefore[0].length; end += closeAfter[0].length; }

        const selected = value.slice(start, end).replace(COLOR_TAG, '');
        const wrapped = hex
            ? selected.split('\n').map(line => {
                  const m = line.match(/^(\s*)(.*?)(\s*)$/);
                  return m[2] ? `${m[1]}[cor=${hex}]${m[2]}[/cor]${m[3]}` : line;
              }).join('\n')
            : selected;

        textarea.value = value.slice(0, start) + wrapped + value.slice(end);
        textarea.setSelectionRange(start, start + wrapped.length);
        textarea.focus();
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        return true;
    }

    // Editor de louvores: tamanho só no texto selecionado da letra → [tam=NN]…[/tam] (NN = % do tamanho normal).
    // delta = pontos percentuais (+10/-10). Parte do tamanho já aplicado à seleção (ou 100%). Devolve false se não houver seleção.
    const SIZE_TAG = /\[(?:tam=\d{2,3}|\/tam)\]/gi;
    function sizeSelection(textarea, delta) {
        if (!textarea) return false;
        let start = textarea.selectionStart, end = textarea.selectionEnd;
        if (start === end) return false;
        const value = textarea.value;

        // Se a seleção está logo dentro de uma marca existente, inclui-a (para ajustar o tamanho já aplicado).
        const before = value.slice(0, start), after = value.slice(end);
        const openBefore = before.match(/\[tam=(\d{2,3})\]$/i);
        const closeAfter = after.match(/^\[\/tam\]/i);
        if (openBefore && closeAfter) { start -= openBefore[0].length; end += closeAfter[0].length; }

        const current = openBefore ? parseInt(openBefore[1], 10) : 100;
        const next = Math.min(300, Math.max(50, current + delta));

        const selected = value.slice(start, end).replace(SIZE_TAG, '');
        const wrapped = selected.split('\n').map(line => {
            const m = line.match(/^(\s*)(.*?)(\s*)$/);
            return m[2] ? `${m[1]}[tam=${next}]${m[2]}[/tam]${m[3]}` : line;
        }).join('\n');

        textarea.value = value.slice(0, start) + wrapped + value.slice(end);
        textarea.setSelectionRange(start, start + wrapped.length);
        textarea.focus();
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        return true;
    }

    // Aba Configuração: clique = escolher elemento (data-t); arrastar = selecionar trecho (data-l/data-o).
    function cfgBind(container, dotnetRef) {
        if (!container) return;
        container._cfgRef = dotnetRef;
        if (container._cfgBound) return;
        container._cfgBound = true;

        // Converte um ponto do DOM em (linha, posição) no texto original do slide.
        const pos = (node, offset, isEnd) => {
            let span = null, off = 0;
            if (node.nodeType === 3) {
                span = node.parentElement.closest('[data-o]');
                off = offset;
            } else {
                const child = node.childNodes[isEnd ? offset - 1 : offset] || node;
                const spans = child.querySelectorAll ? [...child.querySelectorAll('[data-o]')] : [];
                if (child.matches && child.matches('[data-o]')) spans.unshift(child);
                span = isEnd ? spans.at(-1) : spans[0];
                off = span && isEnd ? span.textContent.length : 0;
            }
            if (!span || !container.contains(span)) return null;
            return { l: +span.dataset.l, o: +span.dataset.o + off };
        };

        container.addEventListener('click', e => {
            const sel = window.getSelection();
            if (sel && !sel.isCollapsed && container.contains(sel.anchorNode)) { e.stopPropagation(); return; }
            const t = e.target.closest('[data-t]');
            if (t && container.contains(t)) container._cfgRef.invokeMethodAsync('OnPickTarget', t.dataset.t).catch(console.error);
        }, true);

        document.addEventListener('selectionchange', () => {
            const sel = window.getSelection();
            if (!sel || sel.isCollapsed || !sel.rangeCount) return;
            const r = sel.getRangeAt(0);
            if (!container.contains(r.commonAncestorContainer)) return;
            const a = pos(r.startContainer, r.startOffset, false), b = pos(r.endContainer, r.endOffset, true);
            if (!a || !b) return;
            clearTimeout(container._selTimer);
            container._selTimer = setTimeout(() =>
                container._cfgRef.invokeMethodAsync('OnTextSelected', a.l, a.o, b.l, b.o, sel.toString().trim()).catch(console.error), 200);
        });
    }

    function clearSelection() { const s = window.getSelection(); if (s) s.removeAllRanges(); }

    function scrollIntoView(selector) {
        const el = document.querySelector(selector);
        if (el) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }

    function setTheme(theme) {
        document.documentElement.dataset.theme = theme;
    }

    return {
        registerHotkeys, unregisterHotkeys, fitText, fitPreview, markProjector, setTheme, scrollIntoView, colorSelection, sizeSelection,
        cfgBind, clearSelection,
        editorInit, editorExec, editorFontSize, editorSetHtml
    };
})();
