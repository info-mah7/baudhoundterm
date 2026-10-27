// The agent's view of a web page, for a Web tab (and the test bridge, which runs this same file).
// window.__bht.outline() turns the page into text: headings, table rows with " | " between cells,
// and every control as [N] kind "label" = value. N is what the agent names to act on one.
// act() then finds that control again: its point on screen for a real click, or sets a list or
// clears a field. Nothing here sends anything anywhere; the host reads the return values.
(() => {
  if (window.__bht) return;
  let els = [];
  let last = Date.now();
  try {
    new MutationObserver(() => { last = Date.now(); })
      .observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
  } catch (e) { }

  const ROLES = new Set(['button', 'link', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'switch',
    'checkbox', 'radio', 'option', 'treeitem', 'combobox', 'textbox', 'searchbox', 'spinbutton', 'slider']);
  const BLOCK = new Set(['block', 'flex', 'grid', 'list-item', 'table', 'table-row', 'table-caption', 'flow-root']);
  const MAX = 120000;

  const clean = s => (s || '').replace(/\s+/g, ' ').trim();
  const cut = (s, n) => (s.length > n ? s.slice(0, n - 1) + '…' : s);

  function visible(el) {
    const st = el.ownerDocument.defaultView.getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || st.visibility === 'collapse') return false;
    if (el.getClientRects().length === 0 && st.display !== 'contents') return false;
    return true;
  }

  function labelOf(el) {
    const doc = el.ownerDocument;
    const aria = el.getAttribute('aria-label');
    if (aria) return clean(aria);
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const t = by.split(/\s+/).map(id => doc.getElementById(id)).filter(Boolean).map(e => e.innerText || e.textContent).join(' ');
      if (clean(t)) return clean(t);
    }
    if (el.id) {
      const l = doc.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (l && clean(l.innerText)) return clean(l.innerText);
    }
    const wrap = el.closest('label');
    if (wrap && wrap !== el && clean(wrap.innerText)) return clean(wrap.innerText);
    const tag = el.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
      return clean(el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('name') || '');
    }
    const text = clean(el.innerText || el.textContent);
    if (text) return text;
    const img = el.querySelector && el.querySelector('img[alt],svg[aria-label]');
    if (img) return clean(img.getAttribute('alt') || img.getAttribute('aria-label'));
    return clean(el.getAttribute('title') || el.getAttribute('value') || '');
  }

  // What kind of control, or null for anything that is not one.
  function kindOf(el) {
    const tag = el.tagName;
    const role = (el.getAttribute('role') || '').toLowerCase();
    if (tag === 'A' && el.hasAttribute('href')) return 'link';
    if (tag === 'BUTTON' || tag === 'SUMMARY') return 'button';
    if (tag === 'SELECT') return 'select';
    if (tag === 'TEXTAREA') return 'textbox';
    if (tag === 'INPUT') {
      const t = (el.getAttribute('type') || 'text').toLowerCase();
      if (t === 'hidden') return null;
      if (t === 'checkbox' || t === 'radio') return t;
      if (t === 'submit' || t === 'button' || t === 'reset' || t === 'image') return 'button';
      return 'textbox';
    }
    if (ROLES.has(role)) return role;
    if (el.isContentEditable && !(el.parentElement && el.parentElement.isContentEditable)) return 'textbox';
    // Script-driven controls: a pointer cursor where the parent has none, with its own text.
    const view = el.ownerDocument.defaultView;
    if (view.getComputedStyle(el).cursor === 'pointer'
        && !(el.parentElement && view.getComputedStyle(el.parentElement).cursor === 'pointer')
        && clean(el.innerText).length > 0 && clean(el.innerText).length <= 80) return 'clickable';
    return null;
  }

  function stateOf(el, kind) {
    const parts = [];
    if (kind === 'textbox') {
      const v = el.isContentEditable ? el.innerText : el.value;
      const t = (el.getAttribute('type') || '').toLowerCase();
      parts.push(t === 'password' ? (v ? '= (password set)' : '= (empty)') : `= "${cut(clean(v), 120)}"`);
    } else if (kind === 'select') {
      const opts = Array.from(el.options || []);
      const chosen = opts.filter(o => o.selected).map(o => clean(o.text));
      parts.push(`= "${chosen.join(', ')}"`);
      parts.push(`(options: ${cut(opts.map(o => clean(o.text)).join(', '), 300)})`);
    } else if (kind === 'checkbox' || kind === 'radio' || kind === 'switch' || kind === 'menuitemcheckbox' || kind === 'menuitemradio') {
      const on = el.checked === true || el.getAttribute('aria-checked') === 'true';
      parts.push(on ? '(on)' : '(off)');
    } else if (kind === 'tab' || kind === 'option' || kind === 'treeitem') {
      if (el.getAttribute('aria-selected') === 'true') parts.push('(selected)');
      if (el.getAttribute('aria-expanded') === 'true') parts.push('(expanded)');
    }
    if (el.disabled || el.getAttribute('aria-disabled') === 'true') parts.push('(disabled)');
    return parts.join(' ');
  }

  // Changes something on the device when used: the host asks before acting on one of these.
  function submits(el, kind) {
    if (kind !== 'button' && kind !== 'clickable' && kind !== 'link' && kind !== 'menuitem') return false;
    const t = (el.getAttribute('type') || '').toLowerCase();
    return el.tagName === 'INPUT' ? t === 'submit' || t === 'image'
         : el.tagName === 'BUTTON' ? (t === '' || t === 'submit') && !!el.form
         : false;
  }

  function outline() {
    els = [];
    const lines = [];
    let line = '';
    let size = 0;
    const flush = () => { const l = line.replace(/\s+/g, ' ').trim(); if (l && l !== '|') { lines.push(l); size += l.length; } line = ''; };
    const put = s => { line += s; };

    function walk(node, depth) {
      if (size > MAX || depth > 200) return;
      if (node.nodeType === 3) { put(node.nodeValue); return; }
      if (node.nodeType !== 1) return;
      const el = node;
      const tag = el.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEMPLATE' || tag.toUpperCase() === 'SVG' && !el.getAttribute('aria-label')) return;
      if (!visible(el)) return;

      if (tag === 'IFRAME' || tag === 'FRAME') {
        let doc = null;
        try { doc = el.contentDocument; } catch (e) { }
        flush();
        if (doc && doc.body) { walk(doc.body, depth + 1); flush(); }
        else { lines.push(`(a frame from another site: ${cut(el.src || '', 100)}; not readable here)`); }
        return;
      }

      const kind = kindOf(el);
      if (kind) {
        const ref = els.push(el);
        const label = cut(labelOf(el), 100);
        const extra = [stateOf(el, kind), submits(el, kind) ? '(submits)' : ''].filter(Boolean).join(' ');
        put(` [${ref}] ${kind} "${label}"${extra ? ' ' + extra : ''} `);
        return;
      }

      const view = el.ownerDocument.defaultView;
      const display = view.getComputedStyle(el).display;
      const role = (el.getAttribute('role') || '').toLowerCase();
      const heading = /^H[1-6]$/.test(tag) || role === 'heading';
      const cell = display === 'table-cell' || role === 'gridcell' || role === 'cell' || role === 'columnheader' || role === 'rowheader';
      const row = display === 'table-row' || role === 'row';
      const block = heading || row || BLOCK.has(display) || tag === 'BR' || tag === 'LI' || tag === 'P';

      if (block) flush();
      if (heading) put('# ');
      if (row) put('| ');
      if (tag === 'IMG' && el.getAttribute('alt')) put(` [image: ${clean(el.getAttribute('alt'))}] `);
      for (const child of el.childNodes) walk(child, depth + 1);
      if (el.shadowRoot) for (const child of el.shadowRoot.childNodes) walk(child, depth + 1);
      if (cell) put(' | ');
      else if (!block) put(' ');
      if (block) flush();
    }

    walk(document.body || document.documentElement, 0);
    flush();
    // Runs of blank-ish lines and long repeats add nothing for the reader.
    const out = [];
    for (const l of lines) if (out.length === 0 || out[out.length - 1] !== l) out.push(l);
    return JSON.stringify({
      url: location.href, title: document.title, text: out.join('\n'), truncated: size > MAX,
      controls: els.map((el, i) => {
        const kind = kindOf(el) || 'clickable';
        return { ref: i + 1, kind, label: cut(labelOf(el), 100), submits: submits(el, kind) };
      })
    });
  }

  // The element's centre in the top page's pixels, adding the offsets of the frames it sits in.
  function pointOf(el) {
    el.scrollIntoView({ block: 'center', inline: 'center' });
    const r = el.getBoundingClientRect();
    let x = r.left + r.width / 2, y = r.top + r.height / 2;
    let w = el.ownerDocument.defaultView;
    while (w && w.frameElement) {
      const fr = w.frameElement.getBoundingClientRect();
      x += fr.left; y += fr.top;
      w = w.parent;
    }
    return { x, y, w: r.width, h: r.height };
  }

  function act(ref, action, value) {
    const el = els[ref - 1];
    if (!el || !el.isConnected) return JSON.stringify({ ok: false, error: `no control [${ref}] on the page now: read the page again` });
    const kind = kindOf(el) || 'clickable';
    const desc = `${kind} "${cut(labelOf(el), 80)}"`;
    if (action === 'point') {
      const p = pointOf(el);
      const password = el.tagName === 'INPUT' && (el.getAttribute('type') || '').toLowerCase() === 'password';
      // A control too small to hit (a hidden checkbox behind a styled one) is clicked by script.
      if (p.w < 1 || p.h < 1) return JSON.stringify({ ok: true, desc, kind, password, submits: submits(el, kind), point: null });
      // Something else on top (a dialog, a menu) would take a real click. A styled sibling inside
      // the same parent (a switch drawn over its check box) is the control itself, as far as a click goes.
      if (el.ownerDocument === document) {
        const hit = document.elementFromPoint(p.x, p.y);
        const same = !hit || hit === el || el.contains(hit) || hit.contains(el) || (el.parentElement && el.parentElement.contains(hit));
        if (!same) p.covered = cut(clean(hit.innerText || hit.getAttribute('aria-label') || hit.tagName.toLowerCase()), 60);
      }
      return JSON.stringify({ ok: true, desc, kind, password, submits: submits(el, kind), point: p });
    }
    if (action === 'scriptclick') { el.click(); return JSON.stringify({ ok: true, desc }); }
    if (action === 'focusclear') {
      el.focus();
      if (el.isContentEditable) { document.execCommand && document.execCommand('selectAll'); }
      else if ('value' in el) {
        const proto = Object.getPrototypeOf(el);
        const setter = Object.getOwnPropertyDescriptor(proto, 'value');
        setter && setter.set ? setter.set.call(el, '') : (el.value = '');
        el.dispatchEvent(new Event('input', { bubbles: true }));
      }
      return JSON.stringify({ ok: true, desc });
    }
    if (action === 'select') {
      if (el.tagName !== 'SELECT') return JSON.stringify({ ok: false, error: `${desc} is not a list; click it instead` });
      const want = clean(value).toLowerCase();
      const opt = Array.from(el.options).find(o => clean(o.text).toLowerCase() === want || o.value.toLowerCase() === want)
               || Array.from(el.options).find(o => clean(o.text).toLowerCase().includes(want));
      if (!opt) return JSON.stringify({ ok: false, error: `no option "${value}" in ${desc}` });
      el.value = opt.value;
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return JSON.stringify({ ok: true, desc: `${desc} = "${clean(opt.text)}"` });
    }
    return JSON.stringify({ ok: false, error: 'unknown action ' + action });
  }

  window.__bht = {
    outline,
    act,
    // Milliseconds since the page last changed, for waiting until it settles.
    quiet: () => JSON.stringify({ ms: Date.now() - last, ready: document.readyState })
  };
})();
