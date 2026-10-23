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

      // Featured product cards: staggered entrance. Hidden state is only applied by JS (nh-pre), so content never stays invisible without it.
      const cardObserver = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add('nh-in');
            cardObserver.unobserve(entry.target);
          }
        });
      }, { threshold: 0.15, rootMargin: '0px 0px -40px 0px' });
      const cardsSeen = new WeakSet();
      const initCards = () => {
        document.querySelectorAll('.nh-product-card').forEach((el, i) => {
          if (cardsSeen.has(el)) return;
          cardsSeen.add(el);
          el.style.setProperty('--nh-delay', (i % 4) * 140 + 'ms');
          el.classList.add('nh-pre');
          cardObserver.observe(el);
        });
      };
      initCards();

      // Generic storefront reveal: [data-nh-reveal] fades/slides in once scrolled into view (hidden state only applied by JS via nh-rv-pre).
      const rvObserver = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add('nh-rv-in');
            rvObserver.unobserve(entry.target);
          }
        });
      }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
      const rvSeen = new WeakSet();
      const initReveal = () => {
        document.querySelectorAll('[data-nh-reveal]').forEach((el) => {
          if (rvSeen.has(el)) return;
          rvSeen.add(el);
          el.classList.add('nh-rv-pre');
          rvObserver.observe(el);
        });
      };
      initReveal();

      // Hero: pointer-driven 3D tilt + powder simulation. Per slide: powder pours in twin streams, piles up around the jar base, then the
      // pile splits apart into drifting dust with a shock ring. Two canvases per slide (behind / in front of the jar) give real depth.
      // Canvases only run while their slide is active and the hero is on screen.
      const setupPowder = (slide) => {
        const back = slide.querySelector('.nhx-fx-back');
        const front = slide.querySelector('.nhx-fx-front');
        const stage = slide.querySelector('.nhx-stage');
        const flash = slide.querySelector('.nhx-flash');
        const mouth = slide.querySelector('.nhx-mouth');
        if (!back || !front || !stage) return;
        const bctx = back.getContext('2d');
        const fctx = front.getContext('2d');
        const pal = (slide.dataset.powder || '#e6d3a3,#d2b57e,#f3e7c6').split(',');
        const baseRatio = parseFloat(slide.dataset.base || '0.8');
        const CYCLE = 10000, POUR_FROM = 600, POUR_TO = 4600, SPLIT_AT = 4800, BINS = 110;
        const rand = (a, b) => a + Math.random() * (b - a);
        const pick = () => pal[(Math.random() * pal.length) | 0];

        let W = 0, H = 0, stageH = 1, dpr = 1, cx = 0, baseY = 0, jarW = 0, jarH = 0, pileW = 0, x0 = 0, hMax = 1;
        const h = new Float32Array(BINS);
        const jit = new Float32Array(BINS).map(() => Math.random() * 2.2); // static surface roughness
        const cap = new Float32Array(BINS); // per-bin ceiling for a centered, peaked heap
        let parts = [], t0 = 0, last = 0, raf = 0, running = false, visible = true, splitting = false, splitStart = 0, splitDone = false;

        const resize = () => {
          const r = slide.getBoundingClientRect(), s = stage.getBoundingClientRect();
          if (!r.width || !s.width) return;
          dpr = Math.min(window.devicePixelRatio || 1, 2);
          W = r.width; H = r.height;
          [back, front].forEach((c) => { c.width = Math.round(W * dpr); c.height = Math.round(H * dpr); });
          bctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          fctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          cx = s.left - r.left + s.width / 2;
          stageH = s.height;
          jarW = s.width * 0.62;               // visible jar body is ~60% of the image width
          jarH = s.height;
          baseY = s.top - r.top + s.height * baseRatio;
          pileW = jarW * 1.7;
          x0 = cx - pileW / 2;
          hMax = jarH * 0.17;
          for (let i = 0; i < BINS; i++) { const d = Math.abs((i + 0.5) / BINS - 0.5) * 2; cap[i] = hMax * Math.max(0, 1 - d); }
        };

        const surface = (x) => {
          const i = Math.max(0, Math.min(BINS - 1, ((x - x0) / pileW * BINS) | 0));
          return h[i];
        };
        const addPile = (x, amt) => {
          const c = ((x - x0) / pileW * BINS) | 0;
          for (let k = -2; k <= 2; k++) {
            const i = c + k;
            if (i < 0 || i >= BINS) continue;
            h[i] = Math.min(cap[i], h[i] + amt * (k === 0 ? 0.5 : Math.abs(k) === 1 ? 0.2 : 0.05) * 2);
          }
        };
        const settle = () => { // angle of repose: spill steep columns onto neighbours
          for (let pass = 0; pass < 2; pass++) {
            for (let i = 0; i < BINS - 1; i++) {
              const d = h[i] - h[i + 1];
              if (d > 1.3) { const m = (d - 1.3) * 0.5; h[i] -= m; h[i + 1] += m; }
              else if (d < -1.3) { const m = (-d - 1.3) * 0.5; h[i] += m; h[i + 1] -= m; }
            }
          }
        };

        const emit = (o) => parts.push(Object.assign({ x: 0, y: 0, vx: 0, vy: 0, r: 1.5, a: 1, age: 0, life: 4000, g: 0.3, drag: 1, layer: 1, c: pick(), kind: 'free', sw: 0 }, o));

        const resetCycle = (now) => {
          t0 = now; parts = []; h.fill(0); splitting = false; splitDone = false;
        };

        const pour = (f) => {
          const per = Math.max(1, Math.round(11 * f));
          for (const side of [-1, 1]) {
            for (let k = 0; k < per; k++) {
              const sx = cx + side * jarW * 0.6 + rand(-16, 16);
              emit({
                x: sx, y: -12, vx: -side * rand(0.2, 0.75), vy: rand(2, 6), r: rand(1.2, 3.4), g: 0.34, drag: 0.998,
                layer: 0, kind: 'fall', life: 6000, c: pick(),
              });
            }
          }
        };

        const startSplit = (now) => {
          splitting = true; splitDone = false; splitStart = now;
          if (flash) { flash.classList.remove('go'); void flash.offsetWidth; flash.classList.add('go'); }
        };

        const erode = (f) => {
          let left = 0;
          for (let i = 0; i < BINS; i++) {
            const e = h[i] * 0.16 * f;
            if (e < 0.02) { left += h[i]; h[i] = Math.max(0, h[i] - e); continue; }
            h[i] -= e; left += h[i];
            const n = Math.min(4, Math.ceil(e / 1.6));
            const x = x0 + (i + Math.random()) * pileW / BINS;
            const dx = x - cx;
            const dir = dx === 0 ? (Math.random() < 0.5 ? -1 : 1) : Math.sign(dx);
            const d = Math.min(1, Math.abs(dx) / (pileW / 2));
            for (let k = 0; k < n; k++) {
              emit({
                x, y: baseY - h[i] - rand(0, 6), vx: dir * (1.6 + d * 6.5) * rand(0.6, 1.3), vy: -rand(2.5, 9) * (1.15 - d * 0.55),
                r: rand(0.9, 3.2), g: 0.045, drag: 0.987, life: rand(2600, 4600), layer: Math.random() < 0.55 ? 1 : 0, kind: 'free', sw: rand(0, 6.28),
              });
            }
          }
          if (left < 0.5) { h.fill(0); splitDone = true; }
        };

        const rgba = (hex, al) => {
          const n = parseInt(hex.replace('#', ''), 16);
          return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + al + ')';
        };
        // The mound is drawn into a small offscreen canvas and softly blurred so its silhouette reads as loose powder, then grain is sprinkled on top.
        const PAD = 12;
        let pcv = null, pctx = null;
        const drawPile = () => {
          let max = 0;
          for (let i = 0; i < BINS; i++) if (h[i] > max) max = h[i];
          if (max < 0.6) return;
          const step = pileW / BINS;
          const bot = baseY + jarH * 0.04;
          const top = baseY - hMax - PAD;
          const ow = Math.ceil(pileW + PAD * 2), oh = Math.ceil(bot - top + PAD);
          if (!pcv) { pcv = document.createElement('canvas'); pctx = pcv.getContext('2d'); }
          if (pcv.width !== Math.round(ow * dpr) || pcv.height !== Math.round(oh * dpr)) { pcv.width = Math.round(ow * dpr); pcv.height = Math.round(oh * dpr); }
          pctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          pctx.clearRect(0, 0, ow, oh);
          const ox = PAD - x0, oy = -top;       // world -> offscreen
          const ty = baseY - max;
          const g = pctx.createLinearGradient(0, ty + oy, 0, bot + oy);
          const mid = Math.min(0.95, Math.max(0.05, (baseY - ty) / (bot - ty)));
          g.addColorStop(0, pal[2] || pal[0]);
          g.addColorStop(mid * 0.55, pal[0]);
          g.addColorStop(mid, rgba(pal[1] || pal[0], 0.95));
          g.addColorStop(1, rgba(pal[1] || pal[0], 0));
          pctx.fillStyle = g;
          pctx.beginPath();
          pctx.moveTo(x0 + ox, bot + oy);
          for (let i = 0; i < BINS; i++) pctx.lineTo(x0 + i * step + ox, baseY - h[i] - (h[i] > 1 ? jit[i] : 0) + oy);
          pctx.lineTo(x0 + pileW + ox, bot + oy);
          pctx.closePath();
          pctx.fill();
          fctx.save();
          fctx.globalAlpha = 0.97;
          fctx.filter = 'blur(2.2px)';
          fctx.drawImage(pcv, 0, 0, pcv.width, pcv.height, x0 - PAD, top, ow, oh);
          fctx.filter = 'none';
          // grain on the surface
          for (let k = 0; k < 240; k++) {
            const i = (Math.random() * BINS) | 0;
            if (h[i] < 1) continue;
            fctx.globalAlpha = rand(0.3, 0.8);
            fctx.fillStyle = Math.random() < 0.55 ? (pal[2] || '#fff') : pal[1];
            fctx.fillRect(x0 + i * step + rand(-2, 2), baseY - h[i] + rand(-1, Math.min(h[i], 16)), rand(1, 2.6), rand(1, 2.2));
          }
          fctx.restore();
        };

        // Box animation timeline (ms): lid lifts away, powder is sprayed in from above and heaps up inside, lid settles back.
        const ease = (x) => { x = Math.min(1, Math.max(0, x)); return x * x * (3 - 2 * x); };
        const easeOut = (x) => 1 - Math.pow(1 - Math.min(1, Math.max(0, x)), 3);
        const lidAt = (t) => t < 1700 ? 0 : t < 2300 ? easeOut((t - 1700) / 600) : t < 5600 ? 1 : t < 6200 ? 1 - ease((t - 5600) / 600) : 0;
        // Once poured, powder remains in the jar as the lid closes and through the idle beat.
        const fillAt = (t) => t < 2300 ? 0 : t < 5000 ? ease((t - 2300) / 2700) : 1;
        let mouthPos = null;
        // Powder is sprayed from the top of the hero straight down into the open jar; the front canvas draws it until it reaches the heap surface.
        const spray = (f, t) => {
          if (!mouth) return;
          const mr = mouth.getBoundingClientRect(), sr = slide.getBoundingClientRect();
          mouthPos = { x: mr.left - sr.left + mr.width / 2, y: mr.top - sr.top + mr.height / 2, w: mr.width };
          if (t > 2300 && t < 5000) {
            const n = Math.max(1, Math.round(9 * f));
            for (let k = 0; k < n; k++) {
              emit({ x: mouthPos.x + rand(-mouthPos.w * 0.12, mouthPos.w * 0.12), y: -10, vx: rand(-0.05, 0.05), vy: rand(7, 12), r: rand(1, 3), g: 0.3, drag: 0.999,
                layer: 1, kind: 'spray', life: 4000, c: pick() });
            }
          }
        };

        const frame = (now) => {
          if (!running) return;
          const dt = Math.min(50, now - (last || now)); last = now;
          const f = dt / 16.667;
          if (!t0) t0 = now;
          let t = now - t0;
          if (t >= CYCLE) { resetCycle(now); t = 0; }

          // Keep the decorative outer powder fall and base mound alongside the pour into the jar.
          if (t > POUR_FROM && t < POUR_TO) pour(f);
          const fill = fillAt(t);
          const lid = lidAt(t);
          stage.style.setProperty('--lid', lid.toFixed(3));
          stage.style.setProperty('--lr', ease(lid * 2).toFixed(3));          // rise first
          stage.style.setProperty('--ls', ease(lid * 2 - 1).toFixed(3));      // then slide and tilt away
          stage.style.setProperty('--fill', fill.toFixed(3));
          // A restrained turn keeps the label legible; only the jar and its fitted lid move.
          stage.style.setProperty('--spin', (10 * Math.sin(2 * Math.PI * t / 6000)).toFixed(1));
          spray(f, t);
          // Split the decorative outer mound; the in-jar fill is a separate visual and remains in place.
          if (t >= SPLIT_AT && !splitting && !splitDone) startSplit(now);
          if (splitting && !splitDone) erode(f);
          settle();

          // ambient motes keep the stage alive between beats
          if (Math.random() < 0.5 * f && parts.length < 1500) {
            emit({
              x: rand(0, W), y: rand(H * 0.1, H), vx: rand(-0.2, 0.2), vy: rand(-0.35, -0.05), r: rand(0.7, 2), g: 0, drag: 1,
              life: rand(4000, 7000), layer: Math.random() < 0.5 ? 0 : 1, kind: 'mote', sw: rand(0, 6.28), c: pick(),
            });
          }

          bctx.clearRect(0, 0, W, H);
          fctx.clearRect(0, 0, W, H);

          for (let i = parts.length - 1; i >= 0; i--) {
            const p = parts[i];
            p.age += dt;
            if (p.age > p.life) { parts.splice(i, 1); continue; }
            if (p.kind === 'spray') {
              p.vy = Math.min(16, p.vy + p.g * f);
              p.x += p.vx * f; p.y += p.vy * f;
              if (mouthPos && p.y >= mouthPos.y - fill * 0.07 * stageH) {
                if (Math.random() < 0.08) emit({ x: p.x, y: p.y, vx: rand(-1.4, 1.4), vy: -rand(0.6, 2.2), r: p.r * 0.6, g: 0.1, drag: 0.97, life: rand(400, 800), layer: 1, c: p.c });
                parts.splice(i, 1);
                continue;
              }
            } else if (p.kind === 'fall') {
              p.vy = Math.min(15, p.vy + p.g * f);
              p.x += p.vx * f; p.y += p.vy * f;
              if (p.x > x0 && p.x < x0 + pileW && p.y >= baseY - surface(p.x)) {
                addPile(p.x, p.r * 0.2);
                if (Math.random() < 0.18) emit({ x: p.x, y: p.y, vx: rand(-1.8, 1.8), vy: -rand(0.8, 2.6), r: p.r * 0.6, g: 0.12, drag: 0.97, life: rand(500, 1000), layer: 1, c: p.c });
                parts.splice(i, 1);
                continue;
              }
              if (p.y > H + 20) { parts.splice(i, 1); continue; }
            } else {
              p.vy += p.g * f - (p.kind === 'free' ? 0.0016 * f : 0);
              p.vx += Math.sin(p.age * 0.003 + p.sw) * 0.02 * f;  // gentle swirl
              p.vx *= Math.pow(p.drag, f); p.vy *= Math.pow(p.drag, f);
              p.x += p.vx * f; p.y += p.vy * f;
              if (p.kind === 'mote') p.y += Math.sin(p.age * 0.002 + p.sw) * 0.15;
            }
            const k = p.age / p.life;
            const alpha = p.kind === 'mote' ? Math.sin(Math.PI * k) * 0.55 : (k < 0.1 ? k * 10 : 1 - Math.pow((k - 0.1) / 0.9, 1.6));
            if (alpha <= 0.01) continue;
            const ctx = p.layer ? fctx : bctx;
            ctx.globalAlpha = Math.min(1, alpha);
            ctx.fillStyle = p.c;
            ctx.beginPath();
            ctx.arc(p.x, p.y, p.r, 0, 6.2832);
            ctx.fill();
          }
          fctx.globalAlpha = 1; bctx.globalAlpha = 1;

          drawPile();

          if (splitting) { // shock ring on the floor
            const k = (now - splitStart) / 1100;
            if (k < 1) {
              const rx = jarW * (0.35 + k * 1.6);
              fctx.save();
              fctx.globalAlpha = 0.55 * (1 - k);
              fctx.strokeStyle = pal[2] || pal[0];
              fctx.lineWidth = 3 * (1 - k) + 1;
              fctx.beginPath();
              fctx.ellipse(cx, baseY + jarH * 0.02, rx, rx * 0.16, 0, 0, 6.2832);
              fctx.stroke();
              fctx.restore();
            }
          }
          raf = requestAnimationFrame(frame);
        };

        const isActive = () => slide.classList.contains('active');
        const start = () => {
          if (running || !visible || !isActive() || document.hidden) return;
          resize();
          if (!W) return;
          running = true; last = 0; t0 = 0; resetCycle(0); t0 = 0;
          raf = requestAnimationFrame(frame);
        };
        const stop = () => {
          running = false; cancelAnimationFrame(raf); parts = []; h.fill(0);
          stage.style.setProperty('--lid', '0'); stage.style.setProperty('--lr', '0'); stage.style.setProperty('--ls', '0'); stage.style.setProperty('--fill', '0'); stage.style.setProperty('--spin', '0');
          bctx.clearRect(0, 0, back.width, back.height); fctx.clearRect(0, 0, front.width, front.height);
        };
        const sync = () => { if (isActive() && visible && !document.hidden) start(); else if (running) stop(); };

        new MutationObserver(() => { if (isActive()) setTimeout(sync, 60); else setTimeout(() => { if (!isActive()) stop(); }, 700); })
          .observe(slide, { attributes: true, attributeFilter: ['class'] });
        new IntersectionObserver((es) => { es.forEach((e) => { visible = e.isIntersecting; sync(); }); }, { threshold: 0.15 }).observe(slide);
        new ResizeObserver(() => { if (running) resize(); else sync(); }).observe(slide); // also starts the show once the slide first has a size
        document.addEventListener('visibilitychange', sync);
        setTimeout(sync, 400);
      };

      const initHero = () => {
        const hero = document.querySelector('.nhx-hero');
        if (!hero || hero.dataset.fx) return;
        hero.dataset.fx = '1';
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        let tx = 0, ty = 0, cx = 0, cy = 0, raf = 0;
        const tick = () => {
          cx += (tx - cx) * 0.08; cy += (ty - cy) * 0.08;
          hero.style.setProperty('--rx', (-cy * 8).toFixed(2) + 'deg');
          hero.style.setProperty('--ry', (cx * 12).toFixed(2) + 'deg');
          hero.style.setProperty('--mx', cx.toFixed(3));
          hero.style.setProperty('--my', cy.toFixed(3));
          raf = (Math.abs(tx - cx) > 0.001 || Math.abs(ty - cy) > 0.001) ? requestAnimationFrame(tick) : 0;
        };
        hero.addEventListener('pointermove', (e) => {
          const r = hero.getBoundingClientRect();
          tx = ((e.clientX - r.left) / r.width - 0.5) * 2;
          ty = ((e.clientY - r.top) / r.height - 0.5) * 2;
          if (!raf) raf = requestAnimationFrame(tick);
        });
        hero.addEventListener('pointerleave', () => { tx = 0; ty = 0; if (!raf) raf = requestAnimationFrame(tick); });

        hero.querySelectorAll('.nhx-slide').forEach(setupPowder);
      };
      initHero();


      let timer = null;
      new MutationObserver(() => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { initElements(); initCards(); initReveal(); initHero(); }, 100);
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
    // Resizes customer portraits before they are stored in the account database.
    compressProfilePhoto(inputId) {
      return new Promise((resolve) => {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file || file.size > 8 * 1024 * 1024 || !['image/jpeg', 'image/png', 'image/webp'].includes(file.type)) {
          resolve(null);
          return;
        }

        const objectUrl = URL.createObjectURL(file);
        const image = new Image();
        const finish = (value) => { URL.revokeObjectURL(objectUrl); resolve(value); };
        image.onerror = () => finish(null);
        image.onload = () => {
          const scale = Math.min(1, 512 / Math.max(image.naturalWidth, image.naturalHeight));
          const canvas = document.createElement('canvas');
          canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
          canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
          const context = canvas.getContext('2d');
          if (!context) { finish(null); return; }
          context.fillStyle = '#ffffff';
          context.fillRect(0, 0, canvas.width, canvas.height);
          context.drawImage(image, 0, 0, canvas.width, canvas.height);
          const dataUrl = canvas.toDataURL('image/jpeg', 0.82);
          const size = Math.ceil((dataUrl.length - 'data:image/jpeg;base64,'.length) * 3 / 4);
          if (size > 512 * 1024) { finish(null); return; }
          finish({ name: file.name, type: 'image/jpeg', size: file.size, dataUrl });
        };
        image.src = objectUrl;
      });
    },  };
})();
