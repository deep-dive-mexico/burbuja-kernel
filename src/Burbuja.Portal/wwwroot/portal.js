// The portal's entire client-side script. The SSE client is server-side (EventSource cannot send
// x-api-key), so nothing here talks to the kernel: clipboard, autoscroll, the theme, the rail, and a
// jump-to-bottom hint.
window.portal = (() => {
    // A back/forward-cache restoration must recheck the server session instead of showing a snapshot
    // from before another tab signed out. Normal navigation is unaffected.
    window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });
    function nearBottom(el) {
        return el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    }

    // Adopt dynamically mounted scrollers. Products opt in through data attributes, so their component
    // class names remain private. New content follows only while the reader is near the bottom.
    const SCROLLERS = '.chat, [data-scroll-follow]';
    const state = new WeakMap();

    function toEnd(el) { el.scrollTop = el.scrollHeight; }

    function adopt(el) {
        if (state.has(el)) return;
        state.set(el, { pinned: true });
        toEnd(el);
        el.addEventListener('scroll', () => { state.get(el).pinned = nearBottom(el); }, { passive: true });
        new MutationObserver(() => { if (state.get(el).pinned) toEnd(el); })
            .observe(el, { childList: true, subtree: true, characterData: true });
    }

    function adoptAll() { document.querySelectorAll(SCROLLERS).forEach(adopt); }

    function repinAll() {
        document.querySelectorAll(SCROLLERS).forEach(el => { adopt(el); state.get(el).pinned = true; toEnd(el); });
        // The in-flight bubble and the progress line sit under the thread, above the composer.
        const composer = document.querySelector('[data-scroll-composer]');
        if (composer) composer.scrollIntoView({ block: 'end' });
    }

    new MutationObserver(adoptAll).observe(document.documentElement, { childList: true, subtree: true });

    document.addEventListener('click', e => {
        if (e.target.closest && e.target.closest('[data-scroll-send]')) setTimeout(repinAll, 0);
    }, true);
    document.addEventListener('keydown', e => {
        if (e.key === 'Enter' && !e.shiftKey && e.target.matches && e.target.matches('[data-scroll-input]'))
            setTimeout(repinAll, 0);
    }, true);

    // M12 R1 — the theme. The palette switches on `body[data-theme="dark"]` (our own attribute now; it
    // was dsh's `data-ds-dark-theme` until the identity pass), so the whole theme is one attribute:
    // App.razor sets it at boot — the only place the OS preference is read — and this owns every change
    // after that. Delegated from the document, because the button is Blazor's node and a re-render may
    // replace it.
    //
    // The document's registered theme key is shared by the bootstrap and every later toggle.
    function themeKey() {
        return document.documentElement.getAttribute('data-theme-key') || 'burbuja.theme';
    }

    function isDark() {
        return !!document.body && document.body.getAttribute('data-theme') === 'dark';
    }

    function setTheme(dark) {
        if (dark) document.body.setAttribute('data-theme', 'dark');
        else document.body.removeAttribute('data-theme');
        try { localStorage.setItem(themeKey(), dark ? 'dark' : 'light'); } catch { /* storage denied: the choice lasts the page */ }
    }

    // M12 R1 — the rail's collapsed state, kept beside the theme key for the same reason: it is a
    // preference about how the console looks, it belongs to this browser, and a Host restart must not
    // reset it. The key is spelled once, in this file, like the theme's.
    //
    // The state is ONE attribute on <html>, written by App.razor's bootstrap before the first paint and
    // flipped here. It is deliberately not a Blazor field: a component flag would have to be hydrated
    // from localStorage after the circuit came up, so an expanded rail would paint first and snap — and
    // the CSS is the only thing that has to agree, so CSS is where it lives.
    const RAIL_KEY = 'burbuja.rail';

    function toggleRail() {
        const html = document.documentElement;
        const collapsed = html.getAttribute('data-rail') !== 'collapsed';
        html.setAttribute('data-rail', collapsed ? 'collapsed' : 'expanded');
        try { localStorage.setItem(RAIL_KEY, collapsed ? 'collapsed' : 'expanded'); } catch { /* storage denied */ }
    }

    // Under 900px the rail is a drawer over the page. The class is on the shell so the scrim, the rail
    // and the content column all read one flag.
    function toggleDrawer() {
        const body = document.querySelector('.shell-body');
        if (body) body.classList.toggle('rail-open');
    }

    document.addEventListener('click', (event) => {
        if (event.target.closest('[data-theme-toggle]')) {
            setTheme(!isDark());
            return;
        }

        // Wave 2 (2026-09-26) — AJUSTES sets the theme explicitly (Oscuro / Claro) through the same writer.
        const themeSet = event.target.closest('[data-theme-set]');
        if (themeSet) {
            setTheme(themeSet.getAttribute('data-theme-set') === 'dark');
            return;
        }

        if (event.target.closest('[data-rail-toggle]')) {
            toggleRail();
            return;
        }

        if (event.target.closest('[data-rail-drawer]')) {
            toggleDrawer();
            return;
        }

        // A click anywhere outside an open drawer closes it, which is what every drawer on every platform
        // does and the only way back to the page without hunting for the button again.
        const open = document.querySelector('.shell-body.rail-open');
        if (open && !event.target.closest('.rail-nav')) open.classList.remove('rail-open');
    });

    // M13 R0 — F26. The Create menu is Blazor's node, so opening it is a circuit round trip and there is no
    // server-side click to hang a "close" on. Escape and a click outside are what every menu on every
    // platform does; `portal.js:74-76` closed only the DRAWER, so the menu stayed open until the button was
    // pressed again. The menu is told apart from the button by `[data-create-menu]`, and closing is a click
    // on that same button — one path, not two, so the circuit's own state stays the only state.
    function closeCreateMenu() {
        const button = document.querySelector('[data-create-menu]');
        if (button && button.getAttribute('aria-expanded') === 'true') button.click();
    }

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') closeCreateMenu();
    });

    document.addEventListener('click', (event) => {
        const button = document.querySelector('[data-create-menu]');
        if (!button || button.getAttribute('aria-expanded') !== 'true') return;
        if (event.target.closest('[data-create-menu]') || event.target.closest('.menu-panel')) return;
        closeCreateMenu();
    });

    // The circuit's reconnect element (App.razor). Blazor sets components-reconnect-{show,hide,retrying,failed,
    // rejected,paused,resume-failed}; once the server no longer has the session, retrying is pointless, so reload.
    function watchReconnect() {
        const modal = document.getElementById('components-reconnect-modal');
        if (!modal) return;
        let probing = null;
        const visible = () => /components-reconnect-(show|retrying|paused|failed|rejected|resume-failed)/.test(modal.className);
        // Blazor's own retry backs off for minutes after a redeploy, and a new host never holds the old session:
        // while the notice is up, ask the host whether it is back, and reload as soon as it answers.
        const probe = async () => {
            if (!visible()) { clearInterval(probing); probing = null; return; }
            try {
                const r = await fetch('/health/plugins', { cache: 'no-store' });
                if (r.ok) location.reload();
            } catch { /* still down */ }
        };
        new MutationObserver(() => {
            const c = modal.classList;
            if (c.contains('components-reconnect-failed') || c.contains('components-reconnect-rejected')
                || c.contains('components-reconnect-resume-failed')) {
                setTimeout(() => location.reload(), 600);
                return;
            }
            if (visible() && !probing) probing = setInterval(probe, 2000);
        }).observe(modal, { attributes: true, attributeFilter: ['class'] });
    }

    document.addEventListener('DOMContentLoaded', watchReconnect);
    document.addEventListener('DOMContentLoaded', adoptAll);
    adoptAll();

    return {
        // Copy a record's JSON; the button is the only reason this exists.
        copy: async (text) => {
            try { await navigator.clipboard.writeText(text); } catch { /* clipboard denied */ }
        },
        // The trajectory ring can grow past the viewport: keep the newest record in view.
        scrollToEnd: (selector) => {
            const el = document.querySelector(selector);
            if (el) el.scrollTop = el.scrollHeight;
        },
        // Read-only, for the browser facts and for a human in the console.
        theme: () => (isDark() ? 'dark' : 'light'),
        rail: () => (document.documentElement.getAttribute('data-rail') === 'collapsed' ? 'collapsed' : 'expanded'),
    };
})();
