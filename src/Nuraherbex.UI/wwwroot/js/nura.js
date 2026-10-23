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

      // Hero slides: product opening sequence (GUT 365 on slide 1, STAMIX on slide 2). Each hero photo is split into independent layers:
      // background patch, lid cut-out (badge-free), sticker, and a procedurally drawn interior. The lid lifts away, fine herbal powder is
      // simulated (drag, gravity, swirl) from the powder inside the jar, then the lid settles back. Canvas only draws while animating and
      // only while its own slide is showing.
      const HEROES = [
        ['.nh-hero-slide-1', {
          d: { name: 'hero', iw: 1448, ih: 680, hull: [750, 128, 432, 164], lidTop: 139, rim: [966, 253, 200, 23], badge: [1114, 28], lift: 60, drift: 115, rot: -2.5 },
          m: { name: 'hero-m', iw: 451, ih: 316, hull: [34, 26, 268, 94], lidTop: 37, rim: [167.5, 97.5, 119, 8.5], badge: null, lift: 40, drift: 8, rot: -2.5 },
        }],
        ['.nh-hero-slide-2', {
          d: { name: 'stamix', iw: 1448, ih: 680, hull: [848, 126, 422, 166], lidTop: 140, rim: [1059, 258, 199, 19], badge: [1180, 29], lift: 60, drift: 115, rot: -2.5 },
          m: { name: 'stamix-m', iw: 500, ih: 401, hull: [141, 78, 259, 94], lidTop: 83, rim: [270.4, 153, 117.6, 9], badge: [341, 18], lift: 40, drift: 8, rot: -2.5 },
        }],
      ];

      const setupHero = (slideSel, MODES) => {
        const box = document.querySelector(slideSel);
        if (!box) return;
        let cv = box.querySelector('.nh-hero-powder');
        if (!cv) { // markup normally ships the canvas; add it (at the end, so Blazor's child indexes are untouched) if the build predates it
          cv = document.createElement('canvas'); cv.className = 'nh-hero-powder'; cv.setAttribute('aria-hidden', 'true'); box.appendChild(cv);
        }
        if (cv.dataset.powder) return;
        cv.dataset.powder = '1';
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        const ctx = cv.getContext('2d');
        const base = '_content/Nuraherbex.UI/images/';
        const assets = {};
        const load = (mode) => assets[mode] || (assets[mode] = Promise.all(['-lid.png', '-lid-patch.png'].concat(MODES[mode].badge ? ['-badge.png'] : []).map((s) => new Promise((res, rej) => {
          const im = new Image(); im.onload = () => res(im); im.onerror = rej;
          im.src = new URL(base + MODES[mode].name + s, document.baseURI).href;
        }))));

        const mkSprite = (rgb, soft) => {
          const c = document.createElement('canvas'); c.width = c.height = 32;
          const g = c.getContext('2d'), gr = g.createRadialGradient(16, 16, 0, 16, 16, 16);
          gr.addColorStop(0, `rgba(${rgb},1)`); gr.addColorStop(soft ? 0.25 : 0.55, `rgba(${rgb},${soft ? 0.7 : 0.9})`); gr.addColorStop(1, `rgba(${rgb},0)`);
          g.fillStyle = gr; g.fillRect(0, 0, 32, 32); return c;
        };
        // herbal tones: olive, moss, ochre, dried-leaf brown, pale cream
        const TONES = ['104,120,58', '132,128,64', '176,150,84', '140,104,62', '92,108,62', '226,208,158'];
        const sprites = TONES.map((t) => mkSprite(t, false)), spritesSoft = TONES.map((t) => mkSprite(t, true));
        const WEIGHTS = [0.24, 0.2, 0.2, 0.12, 0.12, 0.12];
        const pickTone = () => { let r = Math.random(); for (let i = 0; i < WEIGHTS.length; i++) { if ((r -= WEIGHTS[i]) < 0) return i; } return 0; };

        const clamp01 = (v) => Math.min(1, Math.max(0, v));
        const easeIO = (v) => { v = clamp01(v); return v < 0.5 ? 4 * v * v * v : 1 - Math.pow(-2 * v + 2, 3) / 2; };
        const easeO = (v) => 1 - Math.pow(1 - clamp01(v), 3);
        const smooth = (a, b, v) => { v = clamp01((v - a) / (b - a)); return v * v * (3 - 2 * v); };

        // timeline (s). Sized to fit inside the 6s the slider keeps a slide on screen.
        const T_RISE0 = 0.2, T_RISE = 1.25, T_EMIT0 = 0.85, T_EMIT1 = 2.7, T_RET0 = 3.9, T_RET = 1.3, T_END = 5.35;
        const lowPower = (navigator.hardwareConcurrency || 4) <= 4 || window.innerWidth < 900;

        let G = null, parts = [], raf = 0, last = 0, t0 = 0, running = false, visible = true, loopTimer = 0, interior = null, emitCarry = 0;
        let W = 0, H = 0, dpr = 1, imgs = null;
        const isActive = () => box.classList.contains('active');

        const geometry = () => {
          const m = window.innerWidth > 900 ? 'd' : 'm', M = MODES[m];
          let s, ox, oy, minTop;
          if (m === 'd') {
            s = Math.max(W / M.iw, H / M.ih); ox = W - M.iw * s; oy = (H - M.ih * s) / 2; minTop = 6;
          } else {
            const img = box.querySelector('.nh-hero-mobile-img');
            if (!img) return null;
            const r = img.getBoundingClientRect(), b = box.getBoundingClientRect();
            if (!r.width) return null;
            s = r.width / M.iw; ox = r.left - b.left; oy = r.top - b.top; minTop = Math.max(4, oy - 14);
          }
          // keep the lifted lid inside the hero
          const hw = M.hull[2], rad = Math.abs(M.rot) * Math.PI / 180;
          const headroom = (oy + M.lidTop * s - minTop) / s - (hw / 2) * Math.sin(rad) - 8 - 4;
          return { m, M, s, ox, oy, lift: Math.max(8, Math.min(M.lift, headroom)) };
        };

        const resize = () => {
          dpr = Math.min(window.devicePixelRatio || 1, 2);
          W = box.clientWidth; H = box.clientHeight;
          cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
          G = geometry();
        };

        // The jar's interior, drawn once per play at 3x source resolution and matched to the rim ellipse of the photo:
        // rim with thickness, shaded inner wall, and a matte, granular powder surface sitting just below the rim
        // (domed, with clumps, grain, contact shadow and ambient occlusion). Never moves with the lid.
        const buildInterior = (M) => {
          const [cx, cy, rx, ry] = M.rim, K = 3, pad = 4;
          const lipT = rx * 0.03, rxi = rx - lipT, ryi = ry * 0.9, oyi = 0.3, depth = ry * 0.22;
          const c = document.createElement('canvas'); c.width = Math.ceil((rx * 2 + pad * 2) * K); c.height = Math.ceil((ry * 2 + pad * 2) * K);
          const g = c.getContext('2d'); g.scale(K, K); g.translate(rx + pad, ry + pad);
          const ell = (x, y, a, b) => { g.beginPath(); g.ellipse(x, y, a, b, 0, 0, Math.PI * 2); };
          const rnd = (a, b) => a + Math.random() * (b - a);

          // rim: warm white lip with a fine gold bead on the outer edge
          ell(0, 0, rx, ry); const lip = g.createLinearGradient(0, -ry, 0, ry); lip.addColorStop(0, '#fdfaf5'); lip.addColorStop(1, '#e6ddd0'); g.fillStyle = lip; g.fill();

          // inner wall (only a narrow band shows above the powder): shadowed under the lip, left-lit
          ell(0, oyi, rxi, ryi);
          const wall = g.createLinearGradient(0, -ryi, 0, -ryi + depth * 2.2);
          wall.addColorStop(0, '#6c6180'); wall.addColorStop(1, '#a9a1b7'); g.fillStyle = wall; g.fill();
          g.save(); ell(0, oyi, rxi, ryi); g.clip();

          // powder surface, just below the rim
          const py = oyi + depth, prx = rxi * 0.998, pry = ryi * 0.985;
          g.save(); ell(0, py, prx, pry); g.clip();
          const surf = g.createRadialGradient(-prx * 0.2, py - pry * 0.2, 2, 0, py, prx);
          surf.addColorStop(0, '#dccaa0'); surf.addColorStop(0.55, '#c2ac80'); surf.addColorStop(1, '#a08d64'); g.fillStyle = surf; g.fillRect(-prx, py - pry, prx * 2, pry * 2);
          g.save(); g.translate(-prx * 0.18, py - pry * 0.3); g.scale(1, pry / (prx * 0.7)); // soft dome: powder heaped slightly toward the middle
          const dome = g.createRadialGradient(0, 0, 0, 0, 0, prx * 0.7); dome.addColorStop(0, 'rgba(252,242,212,0.4)'); dome.addColorStop(1, 'rgba(252,242,212,0)');
          g.fillStyle = dome; g.beginPath(); g.arc(0, 0, prx * 0.7, 0, 6.283); g.fill(); g.restore();
          for (let i = 0; i < 90; i++) { // clumps: a lit crest and a soft shadow side, so the surface has relief
            const lx = rnd(-prx * 0.92, prx * 0.92), ly = py + rnd(-pry * 0.8, pry * 0.85), lw = rnd(rx * 0.015, rx * 0.085), lh = lw * rnd(0.2, 0.45);
            for (const [ox, oy2, col] of [[-lw * 0.25, -lh * 0.3, 'rgba(244,230,194,0.36)'], [lw * 0.3, lh * 0.35, 'rgba(88,68,44,0.26)']]) {
              const x = lx + ox, y = ly + oy2, lg = g.createRadialGradient(x, y, 0, x, y, lw);
              lg.addColorStop(0, col); lg.addColorStop(1, 'rgba(0,0,0,0)');
              g.save(); g.translate(x, y); g.scale(1, lh / lw); g.translate(-x, -y); g.fillStyle = lg; g.beginPath(); g.arc(x, y, lw, 0, 6.283); g.fill(); g.restore();
            }
          }
          const grains = Math.round(rx * 36); // fine grain: cream, brown, golden and a few green flecks, each with a tiny shadow
          for (let i = 0; i < grains; i++) {
            const a = Math.random() * 6.283, r = Math.sqrt(Math.random()), u = Math.random(), gx = Math.cos(a) * r * prx, gy = py + Math.sin(a) * r * pry, sz = rnd(0.35, 0.95);
            if (Math.random() < 0.3) { g.fillStyle = 'rgba(60,44,28,0.22)'; g.fillRect(gx + 0.35, gy + 0.35, sz, sz * 0.8); }
            g.fillStyle = u < 0.4 ? `rgba(246,234,198,${rnd(0.25, 0.65)})` : u < 0.7 ? `rgba(86,66,40,${rnd(0.22, 0.55)})` : u < 0.88 ? `rgba(196,164,96,${rnd(0.25, 0.55)})` : `rgba(96,110,54,${rnd(0.25, 0.5)})`;
            g.fillRect(gx, gy, sz, sz * 0.8);
          }
          g.restore();

          // contact shadow where the powder meets the wall, rim shadow fading into the powder, soft darkening toward the edges
          g.save(); g.lineWidth = 1.6; g.strokeStyle = 'rgba(46,34,24,0.38)'; g.beginPath(); g.ellipse(0, py, prx, pry, 0, Math.PI * 1.02, Math.PI * 1.98); g.stroke(); g.restore();
          const top = g.createLinearGradient(0, -ryi, 0, -ryi + depth * 1.8 + ry * 0.2); top.addColorStop(0, 'rgba(30,20,40,0.34)'); top.addColorStop(1, 'rgba(30,20,40,0)');
          g.fillStyle = top; g.fillRect(-rxi, -ryi - 1, rxi * 2, depth * 1.8 + ry * 0.2 + 1);
          g.save(); g.translate(0, oyi); g.scale(1, ryi / rxi);
          const ao = g.createRadialGradient(0, 0, rxi * 0.6, 0, 0, rxi); ao.addColorStop(0, 'rgba(34,24,28,0)'); ao.addColorStop(1, 'rgba(34,24,28,0.22)');
          g.fillStyle = ao; g.fillRect(-rxi, -rxi, rxi * 2, rxi * 2); g.restore();
          g.restore(); // end inner clip

          // edges: dark inner lip line, bright top of the rim, gold bead
          ell(0, oyi, rxi, ryi); g.lineWidth = 0.9; g.strokeStyle = 'rgba(70,52,64,0.5)'; g.stroke();
          g.beginPath(); g.ellipse(0, 0, rx - 0.6, ry - 0.5, 0, Math.PI * 1.02, Math.PI * 1.98); g.lineWidth = 1.2; g.strokeStyle = 'rgba(255,255,255,0.9)'; g.stroke();
          g.beginPath(); g.ellipse(0, 0, rx, ry, 0, Math.PI * 1.0, Math.PI * 2.0); g.lineWidth = 0.9; g.strokeStyle = 'rgba(204,160,62,0.5)'; g.stroke();
          return { c, x: cx - rx - pad, y: cy - ry - pad, w: c.width / K, h: c.height / K, inner: [cx, cy + oyi, rxi, ryi], rxi, ryi };
        };

        const spawn = (n) => {
          const [cx, cy] = G.M.rim, I = interior;
          const yMid = cy + 3, yR = I.ryi * 0.5;
          for (let i = 0; i < n; i++) {
            const ang = Math.random() * 6.283, rr = Math.sqrt(Math.random());
            const z = Math.random(), cloud = Math.random() < 0.06, size = cloud ? 8 + Math.random() * 9 : (0.8 + Math.random() * 1.7) * (0.65 + 0.75 * z) * (Math.random() < 0.05 ? 1.7 : 1);
            const px = cx + Math.cos(ang) * rr * I.rxi * 0.84, py = yMid + Math.sin(ang) * rr * yR;
            const jet = (110 + Math.random() * 330) * (0.55 + 0.45 * (1 - rr * 0.6)) * (G.m === 'm' ? 0.42 : 1);
            parts.push({
              x: px, y: py, vx: ((px - cx) / I.rxi) * 70 + (Math.random() - 0.5) * 80, vy: -jet,
              z, size, tone: cloud ? (Math.random() < 0.5 ? 5 : 1) : pickTone(), soft: cloud || Math.random() < 0.18, seed: Math.random() * 6.283,
              age: 0, life: 2.2 + Math.random() * 1.2 + (1 - z) * 0.3, base: cloud ? 0.05 + Math.random() * 0.06 : 0.5 + Math.random() * 0.45, mass: cloud ? 0.5 : 0.7 + size * 0.25, a: 0, sz: 0,
            });
          }
        };

        // lid: straight up out of the jar, then a little further back (smaller, tilted away) and hovering
        const pose = (t) => {
          const rise = easeIO((t - T_RISE0) / T_RISE), ret = easeIO((t - T_RET0) / T_RET), p = rise * (1 - ret);
          const A = easeO(p / 0.5), B = easeIO((p - 0.35) / 0.65);
          const bob = Math.sin(t * 1.9) * 1.6 * B * (1 - ret);
          let dy = -G.lift * A - 8 * B + bob;
          const st = t - (T_RET0 + T_RET);
          if (st > 0 && st < 0.9) dy += 1.6 * Math.exp(-st * 8) * Math.sin(st * 24); // settles back with a soft click
          return { p, dx: G.M.drift * B, dy, rot: G.M.rot * B * Math.PI / 180, sc: 1 - 0.04 * B, sq: 1 - 0.14 * B };
        };

        const frame = (now) => {
          const dt = Math.min((now - last) / 1000, 0.05); last = now;
          const t = (now - t0) / 1000, M = G.M, s = G.s;
          const [hx, hy, hw, hh] = M.hull, [cx, cy, rx] = M.rim;
          ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
          ctx.setTransform(dpr * s, 0, 0, dpr * s, dpr * G.ox, dpr * G.oy);
          const ps = pose(t);

          // 1. background rebuilt behind the lid, then the jar's interior
          ctx.drawImage(imgs[1], hx, hy);
          ctx.drawImage(interior.c, interior.x, interior.y, interior.w, interior.h);

          // 2. shadow the lid casts into the open jar while it hovers above it
          if (ps.p > 0.03) {
            ctx.save(); ctx.beginPath(); ctx.ellipse(interior.inner[0], interior.inner[1], interior.inner[2], interior.inner[3], 0, 0, 6.283); ctx.clip();
            ctx.fillStyle = `rgba(34,22,56,${0.15 * Math.min(1, ps.p * 1.6)})`; ctx.fillRect(cx - rx, cy - 40, rx * 2, 80); ctx.restore();
          }

          // 3. soft light catching the plume
          const glow = smooth(T_EMIT0 - 0.2, T_EMIT0 + 0.6, t) * (1 - smooth(T_EMIT1 + 0.3, T_EMIT1 + 2, t));
          if (glow > 0.01) {
            ctx.save(); ctx.translate(cx, cy - 70); ctx.scale(1, 0.62);
            const gr = ctx.createRadialGradient(0, 0, 0, 0, 0, rx * 1.15);
            gr.addColorStop(0, `rgba(255,247,222,${0.2 * glow})`); gr.addColorStop(0.55, `rgba(255,244,220,${0.07 * glow})`); gr.addColorStop(1, 'rgba(255,244,220,0)');
            ctx.fillStyle = gr; ctx.beginPath(); ctx.arc(0, 0, rx * 1.15, 0, 6.283); ctx.fill(); ctx.restore();
          }

          // 4. powder simulation. The lid floats in front of the plume, so particles are drawn behind it.
          if (t > T_EMIT0 && t < T_EMIT1) {
            const N = lowPower ? 1200 : 2600, e = Math.exp(-(t - T_EMIT0) * 1.15) / 0.82;
            emitCarry += N * e * dt; const n = Math.floor(emitCarry); emitCarry -= n; if (n) spawn(n);
          }
          const minPx = 0.7 / s;
          for (let i = parts.length - 1; i >= 0; i--) {
            const p = parts[i];
            p.age += dt;
            if (p.age >= p.life) { parts[i] = parts[parts.length - 1]; parts.pop(); continue; }
            // air: swirling flow field + warm uplift above the mouth + faint breeze
            const a = p.x * 0.0105 + t * 0.55 + p.seed * 0.3, b = p.y * 0.0126 - t * 0.4;
            const core = Math.exp(-Math.pow((p.x - cx) / 230, 2)) * Math.exp(-p.age * 0.55);
            const wx = Math.sin(a) * Math.cos(b) * 36 + 7, wy = -Math.cos(a) * Math.sin(b) * 28 - 52 * core;
            const k = 0.85 + p.mass * 0.45;
            p.vx += (wx - p.vx) * k * dt;
            p.vy += (wy - p.vy) * k * dt + 20 * p.mass * dt; // gravity: settles slowly once the jet has spent
            p.x += p.vx * dt; p.y += p.vy * dt;
            const u = p.age / p.life;
            p.a = p.base * (0.55 + 0.45 * p.z) * Math.min(1, p.age / 0.12) * (1 - smooth(0.5, 1, u));
            if (p.a < 0.01) continue;
            p.sz = Math.max(p.size * (1 + u * 0.35), minPx) * 1.15;
            ctx.globalAlpha = p.a; ctx.drawImage((p.soft ? spritesSoft : sprites)[p.tone], p.x - p.sz, p.y - p.sz, p.sz * 2, p.sz * 2);
          }

          // 5. the lid: cut-out of the product photo (lid pixels only), then the sticker on top so the lid passes behind it
          const lx = hx + hw / 2, ly = hy + hh / 2;
          ctx.globalAlpha = 1; ctx.save();
          ctx.translate(lx + ps.dx, ly + ps.dy); ctx.rotate(ps.rot); ctx.scale(ps.sc, ps.sc * ps.sq);
          if (ps.p > 0.02) { ctx.shadowColor = `rgba(52,30,86,${0.22 * Math.min(1, ps.p * 1.5)})`; ctx.shadowBlur = 16 * s * dpr; ctx.shadowOffsetY = 10 * s * dpr; }
          ctx.drawImage(imgs[0], -hw / 2, -hh / 2);
          ctx.restore();
          if (imgs[2]) ctx.drawImage(imgs[2], M.badge[0], M.badge[1]);

          if (t < T_END) raf = requestAnimationFrame(frame); else finish();
        };

        const schedule = (ms) => { clearTimeout(loopTimer); loopTimer = setTimeout(play, ms); };
        const clear = () => { ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, cv.width, cv.height); };

        const finish = () => {
          running = false; parts = [];
          clear(); // the original photo shows again, seamlessly
          schedule(3600);
        };

        const play = () => {
          if (running) return;
          if (document.hidden || !visible || !isActive()) { schedule(1000); return; }
          resize(); if (!G) { schedule(1500); return; }
          const m = G.m;
          load(m).then((ims) => {
            if (running || !isActive()) return;
            imgs = ims; G = geometry() || G;
            interior = buildInterior(G.M); parts = []; emitCarry = 0;
            running = true; t0 = last = performance.now(); cv.dataset.t0 = String(t0);
            raf = requestAnimationFrame(frame);
          }).catch(() => { });
        };

        const halt = () => { cancelAnimationFrame(raf); clearTimeout(loopTimer); running = false; parts = []; clear(); };

        setTimeout(play, 700);
        // restart when the slide comes back into view, stop (after the cross-fade) when the slider moves on
        new MutationObserver(() => {
          if (isActive()) { if (!running) schedule(450); }
          else { clearTimeout(loopTimer); setTimeout(() => { if (!isActive() && running) halt(); }, 750); }
        }).observe(box, { attributes: true, attributeFilter: ['class'] });
        new IntersectionObserver((es) => {
          es.forEach((e) => {
            visible = e.isIntersecting;
            if (!visible && running) halt();
            else if (visible && !running && isActive()) schedule(500);
          });
        }, { threshold: 0.5 }).observe(box);
        new ResizeObserver(() => { if (running) resize(); }).observe(box);
        document.addEventListener('visibilitychange', () => {
          if (document.hidden && running) halt(); else if (!document.hidden && !running) schedule(800);
        });
      };
      const initHeroPowder = () => HEROES.forEach(([sel, modes]) => setupHero(sel, modes));
      initHeroPowder();

      let timer = null;
      new MutationObserver(() => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { initElements(); initCards(); initHeroPowder(); }, 100);
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
