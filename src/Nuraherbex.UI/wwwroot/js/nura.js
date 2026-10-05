// Nura Herbex — browser interop for the Blazor UI (web + MAUI WebView).
// Port of useScrollEffects.js, router.js#updateMetaTags and small DOM helpers.
(function () {
  const stores = {
    local: () => window.localStorage,
    session: () => window.sessionStorage,
  };

  let scrollInitialised = false;

  function ensureMeta(selector, create) {
    let el = document.querySelector(selector);
    if (!el) { el = create(); document.head.appendChild(el); }
    return el;
  }

  // Header chrome: past-hero detection, Escape-to-close and body scroll lock (called from Header.razor).
  (function () {
    let ref = null, isHome = false, last = null;
    function compute() {
      let past;
      if (!isHome) past = true;
      else {
        const hero = document.getElementById('hero');
        past = hero ? hero.getBoundingClientRect().bottom <= 80 : window.scrollY > 80;
      }
      if (past !== last && ref) { last = past; ref.invokeMethodAsync('SetPastHero', past); }
    }
    window.addEventListener('scroll', compute, { passive: true });
    window.addEventListener('resize', compute, { passive: true });
    window.addEventListener('keydown', (e) => { if (e.key === 'Escape' && ref) ref.invokeMethodAsync('OnEscape'); });
    window.nuraHeader = {
      init(r, home) { ref = r; isHome = home; last = null; compute(); setTimeout(compute, 100); },
      setHome(home) { isHome = home; last = null; compute(); setTimeout(compute, 100); },
      lock(on) { document.body.style.overflow = on ? 'hidden' : 'unset'; },
      dispose() { ref = null; document.body.style.overflow = 'unset'; },
    };
  })();

  // Helpers for the pinned/scroll-driven home sections and background videos (called via HomeJs.cs).
  window.__nuraProbeMany = function (ids) {
    return ids.map(function (id) {
      const e = document.getElementById(id);
      if (!e) return null;
      const r = e.getBoundingClientRect();
      return [r.top, r.height, window.innerHeight, window.scrollY, window.innerWidth, e.scrollWidth];
    });
  };
  window.__nuraScrollToProgress = function (id, p) {
    const e = document.getElementById(id);
    if (!e) return;
    const r = e.getBoundingClientRect();
    const total = e.offsetHeight - window.innerHeight;
    window.scrollTo({ top: window.scrollY + r.top + p * total, behavior: 'smooth' });
  };
  window.__nuraPlayVideo = function (id) {
    const v = document.getElementById(id);
    if (!v) return;
    v.muted = true;
    const p = v.play();
    if (p && p.catch) p.catch(function () {});
  };

  window.nura = {
    storage: {
      get(kind, key) { try { return stores[kind]().getItem(key); } catch { return null; } },
      set(kind, key, value) { try { stores[kind]().setItem(key, value); } catch { } },
      remove(kind, key) { try { stores[kind]().removeItem(key); } catch { } },
    },

    // --- Scroll progress bar, parallax variable, cinematic reveal (IntersectionObserver) ---
    initScrollEffects() {
      if (scrollInitialised) return;
      scrollInitialised = true;

      let ticking = false;
      const onScroll = () => {
        const y = window.scrollY;
        const total = document.documentElement.scrollHeight - window.innerHeight;
        const progress = total > 0 ? (y / total) * 100 : 0;
        if (!ticking) {
          window.requestAnimationFrame(() => {
            document.documentElement.style.setProperty('--scroll-progress', progress + '%');
            document.documentElement.style.setProperty('--scroll-y', y + 'px');
            ticking = false;
          });
          ticking = true;
        }
      };
      window.addEventListener('scroll', onScroll, { passive: true });
      onScroll();

      const observer = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add('is-visible');
            observer.unobserve(entry.target);
          }
        });
      }, { threshold: 0.08, rootMargin: '0px 0px -40px 0px' });

      const selectors = [
        '.reveal-on-scroll', '.cinematic-reveal', '.cinematic-box', '.cinematic-text', '.premium-card', '.glass-pill',
        'section:not(#hero) h2', 'section:not(#hero) h3', 'section:not(#hero) .section-header',
        '#why-stamix .group', '#why-stamix .relative.rounded-2xl', '#solution .group', '#solution .rounded-3xl',
        '#clinical-proof .group', '#formula .group', '#how-to-use .group', '#testimonials .group',
        '#site-footer > div', '.product-card', '.bundle-card',
      ].join(', ');

      const seen = new WeakSet();
      const initElements = () => {
        document.querySelectorAll(selectors).forEach((el) => {
          if (seen.has(el)) return;
          seen.add(el);
          if (!el.classList.contains('cinematic-reveal') && !el.classList.contains('reveal-on-scroll')) {
            const isHeading = ['H1', 'H2', 'H3', 'H4'].includes(el.tagName);
            el.classList.add(isHeading ? 'cinematic-text' : 'cinematic-box');
          }
          const parent = el.parentElement;
          if (parent) {
            const idx = Array.from(parent.children).indexOf(el);
            if (idx > 0 && idx <= 6 && !el.style.transitionDelay) el.style.transitionDelay = idx * 75 + 'ms';
          }
          observer.observe(el);
        });
      };
      initElements();

      let timer = null;
      new MutationObserver(() => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(initElements, 100);
      }).observe(document.body, { childList: true, subtree: true });
    },

    // Calls dotnetRef.OnVisible() once when the element is >= threshold visible (port of useCountUp's IntersectionObserver).
    whenVisible(el, dotnetRef, threshold) {
      if (!el) return;
      const observer = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            observer.disconnect();
            dotnetRef.invokeMethodAsync('OnVisible');
          }
        });
      }, { threshold: threshold || 0.15 });
      observer.observe(el);
    },

    scrollToTop(smooth) { window.scrollTo({ top: 0, behavior: smooth ? 'smooth' : 'auto' }); },

    scrollToId(id, delay) {
      setTimeout(() => {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ behavior: 'smooth' });
      }, delay || 0);
    },

    // --- SEO / document head ---
    setMeta(title, description, canonical, ogTitle) {
      if (title) document.title = title;
      if (description) {
        ensureMeta('meta[name="description"]', () => { const m = document.createElement('meta'); m.name = 'description'; return m; }).content = description;
        ensureMeta('meta[property="og:description"]', () => { const m = document.createElement('meta'); m.setAttribute('property', 'og:description'); return m; }).content = description;
      }
      if (ogTitle) ensureMeta('meta[property="og:title"]', () => { const m = document.createElement('meta'); m.setAttribute('property', 'og:title'); return m; }).content = ogTitle;
      const url = canonical || (window.location.origin + window.location.pathname);
      ensureMeta('link[rel="canonical"]', () => { const l = document.createElement('link'); l.rel = 'canonical'; return l; }).href = url;
      ensureMeta('meta[property="og:url"]', () => { const m = document.createElement('meta'); m.setAttribute('property', 'og:url'); return m; }).content = url;
    },

    copyText(text) {
      if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text);
      const ta = document.createElement('textarea');
      ta.value = text; document.body.appendChild(ta); ta.select();
      try { document.execCommand('copy'); } finally { ta.remove(); }
    },

    // Hidden form POST — hands the browser to PayU's hosted checkout.
    postForm(action, fields) {
      const form = document.createElement('form');
      form.method = 'POST'; form.action = action; form.style.display = 'none';
      Object.keys(fields || {}).forEach((k) => {
        const input = document.createElement('input');
        input.type = 'hidden'; input.name = k; input.value = fields[k];
        form.appendChild(input);
      });
      document.body.appendChild(form);
      form.submit();
    },

    // [left, top, width, height] of an element's viewport rect, or null.
    getRect(id) {
      const el = document.getElementById(id);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return [r.left, r.top, r.width, r.height];
    },

    // Native share sheet where available, otherwise copies the URL.
    share(title, url) {
      if (navigator.share) return navigator.share({ title, url }).catch(() => {});
      return window.nura.copyText(url);
    },

    openUrl(url, target) { window.open(url, target || '_blank', 'noopener'); },

    downloadText(filename, content, mime) {
      const blob = new Blob([content], { type: mime || 'text/plain' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob); a.download = filename;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    },

    // Reads the first file of <input type="file" id="..."> as a data: URL.
    readFileAsDataUrl(inputId) {
      return new Promise((resolve) => {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) { resolve(null); return; }
        const reader = new FileReader();
        reader.onload = () => resolve({ name: file.name, type: file.type || '', size: file.size, dataUrl: String(reader.result || '') });
        reader.onerror = () => resolve(null);
        reader.readAsDataURL(file);
      });
    },
  };
})();
