// 3D Hero Jar Showcase (Three.js) for GUT 365 (Slide 1) & STAMIX (Slide 2) on desktop.
//  - lathe/cylinder jar with the packaging artwork wrapped around it, a separate lid, gold collar, open interior with a powder surface
//  - the same lid-lift / powder-dispersion sequence the 2D version plays, now in world space (so it follows the jar's rotation and depth)
//  - cursor tilt + parallax relative to the jar, idle float, soft contact shadow
// Everything is modelled in "banner pixels": the camera is set up so the z = 0 plane maps 1:1 onto the 1448x680 banner art,
// which means the jar sits exactly where the photographed one was at every viewport size.
import * as THREE from './three.module.min.js';

const BW = 1448, BH = 680;            // banner art size
const CAM_Y = -50, CAM_D = 2667;      // camera height (banner y) and distance, fitted to the photo's ellipse ratios
const R_BODY = 204.5, R_LID = 200, R_IN = 194, H_BODY = 280, H_LID = 97, POWDER_Y = -7;

// Slide-specific configurations
const SLIDE_CONFIGS = {
  1: {
    cx: 965.5,
    topY: 257,
    badge: [1114, 28, 195],
    label: 'gut365-label-wrap.jpg',
    badgeImg: 'hero-badge.png',
    plateImg: 'hero-banner-plate.jpg',
    baseColor: 0x551a9a,
  },
  2: {
    cx: 1059,
    topY: 258,
    badge: [1180, 29, 195],
    label: 'stamix-label-wrap.jpg',
    badgeImg: 'stamix-badge.png',
    plateImg: 'hero-banner-stamix-plate.jpg',
    baseColor: 0x3c1864,
  }
};

// lid / powder timeline (s) - identical to the 2D sequence
const T_RISE0 = 0.2, T_RISE = 1.25, T_EMIT0 = 0.85, T_EMIT1 = 2.7, T_RET0 = 3.9, T_RET = 1.3, T_END = 5.35;
const LID = { lift: 60, drift: 115, rot: 2.5 * Math.PI / 180 };

const clamp01 = (v) => Math.min(1, Math.max(0, v));
const easeIO = (v) => { v = clamp01(v); return v < 0.5 ? 4 * v * v * v : 1 - Math.pow(-2 * v + 2, 3) / 2; };
const easeO = (v) => 1 - Math.pow(1 - clamp01(v), 3);
const smooth = (a, b, v) => { v = clamp01((v - a) / (b - a)); return v * v * (3 - 2 * v); };

const loadImage = (url) => new Promise((res, rej) => { const im = new Image(); im.onload = () => res(im); im.onerror = rej; im.src = url; });

function webglOk() {
  try { const c = document.createElement('canvas'); return !!(c.getContext('webgl2') || c.getContext('webgl')); } catch { return false; }
}

// ---------- procedural pieces ----------
function powderTextures() {
  const N = 1024, mk = () => { const c = document.createElement('canvas'); c.width = c.height = N; return c; };
  const col = mk(), g = col.getContext('2d'), bump = mk(), b = bump.getContext('2d');
  let seed = 11; const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  const gr = g.createRadialGradient(N * 0.42, N * 0.4, 10, N / 2, N / 2, N / 2);
  gr.addColorStop(0, '#dccaa0'); gr.addColorStop(0.55, '#c2ac80'); gr.addColorStop(1, '#a08d64');
  g.fillStyle = gr; g.fillRect(0, 0, N, N);
  b.fillStyle = '#808080'; b.fillRect(0, 0, N, N);
  for (let i = 0; i < 220; i++) { // clumps: lit crest + shadow side, mirrored in the bump map
    const x = rnd() * N, y = rnd() * N, w = 10 + rnd() * 46, h = w * (0.4 + rnd() * 0.4);
    for (const [ox, oy, c, bc] of [[-w * 0.2, -h * 0.25, 'rgba(244,230,194,0.34)', 'rgba(210,210,210,0.55)'], [w * 0.25, h * 0.3, 'rgba(88,68,44,0.24)', 'rgba(40,40,40,0.5)']]) {
      for (const [ctx, color] of [[g, c], [b, bc]]) {
        const rg = ctx.createRadialGradient(x + ox, y + oy, 0, x + ox, y + oy, w);
        rg.addColorStop(0, color); rg.addColorStop(1, 'rgba(128,128,128,0)');
        ctx.save(); ctx.translate(x + ox, y + oy); ctx.scale(1, h / w); ctx.translate(-x - ox, -y - oy);
        ctx.fillStyle = rg; ctx.beginPath(); ctx.arc(x + ox, y + oy, w, 0, 6.283); ctx.fill(); ctx.restore();
      }
    }
  }
  for (let i = 0; i < 26000; i++) { // fine grain
    const x = rnd() * N, y = rnd() * N, sz = 0.8 + rnd() * 1.8, u = rnd();
    g.fillStyle = u < 0.4 ? `rgba(246,234,198,${0.25 + rnd() * 0.4})` : u < 0.7 ? `rgba(86,66,40,${0.22 + rnd() * 0.33})` : u < 0.88 ? `rgba(196,164,96,${0.25 + rnd() * 0.3})` : `rgba(96,110,54,${0.25 + rnd() * 0.25})`;
    g.fillRect(x, y, sz, sz * 0.8);
    b.fillStyle = `rgba(${rnd() < 0.5 ? 255 : 0},${rnd() < 0.5 ? 255 : 0},0,0)`; b.fillStyle = rnd() < 0.5 ? 'rgba(190,190,190,0.5)' : 'rgba(70,70,70,0.5)'; b.fillRect(x, y, sz, sz);
  }
  const ao = g.createRadialGradient(N / 2, N / 2, N * 0.34, N / 2, N / 2, N * 0.5);
  ao.addColorStop(0, 'rgba(34,24,28,0)'); ao.addColorStop(1, 'rgba(34,24,28,0.4)'); g.fillStyle = ao; g.fillRect(0, 0, N, N);
  const t1 = new THREE.CanvasTexture(col), t2 = new THREE.CanvasTexture(bump);
  t1.colorSpace = THREE.SRGBColorSpace; t1.anisotropy = 4; t2.anisotropy = 4;
  return { map: t1, bump: t2 };
}

function radialTexture(stops) {
  const c = document.createElement('canvas'); c.width = c.height = 256; const g = c.getContext('2d');
  const gr = g.createRadialGradient(128, 128, 0, 128, 128, 128); stops.forEach(([o, col]) => gr.addColorStop(o, col));
  g.fillStyle = gr; g.fillRect(0, 0, 256, 256); const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}

function studioEnv(renderer) {
  const sc = new THREE.Scene();
  sc.add(new THREE.Mesh(new THREE.BoxGeometry(20, 20, 20), new THREE.MeshBasicMaterial({ side: THREE.BackSide, color: 0xe6def3 })));
  const panel = (w, h, pos, c) => { const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ color: c, side: THREE.DoubleSide })); m.position.set(...pos); m.lookAt(0, 0, 0); sc.add(m); };
  panel(8, 6, [-7, 8, 7], new THREE.Color(7, 7, 6.6));
  panel(5, 8, [9, 3, 4], new THREE.Color(1.6, 1.4, 2.0));
  panel(10, 4, [0, 9.8, 0], new THREE.Color(3, 3, 3));
  const pm = new THREE.PMREMGenerator(renderer), rt = pm.fromScene(sc, 0.04); pm.dispose();
  return rt.texture;
}

// ---------- scene ----------
function buildJar(THREE_, labelTex, renderer, cfg) {
  const jar = new THREE.Group();            // origin = centre of the gold collar, +y up (units = banner px)
  const aniso = Math.min(8, renderer.capabilities.getMaxAnisotropy());
  labelTex.colorSpace = THREE.SRGBColorSpace; labelTex.anisotropy = aniso; labelTex.generateMipmaps = true; labelTex.minFilter = THREE.LinearMipmapLinearFilter;

  const bodyMat = new THREE.MeshStandardMaterial({ map: labelTex, roughness: 0.46, metalness: 0 });
  const body = new THREE.Mesh(new THREE.CylinderGeometry(R_BODY, R_BODY, H_BODY, 192, 1, true, Math.PI), bodyMat);
  body.position.y = -H_BODY / 2; body.castShadow = true; body.receiveShadow = true; body.material.shadowSide = THREE.DoubleSide;
  jar.add(body);

  const baseColor = cfg && cfg.baseColor ? cfg.baseColor : 0x551a9a;
  const baseMat = new THREE.MeshStandardMaterial({ color: baseColor, roughness: 0.42 });
  const prof = []; const fr = 9;                                   // rounded base: quarter circle then flat bottom
  for (let i = 0; i <= 8; i++) { const a = (i / 8) * Math.PI / 2; prof.push(new THREE.Vector2(R_BODY - fr + Math.cos(a) * fr, -H_BODY - fr + fr + Math.sin(a) * 0 - Math.sin(a) * fr)); }
  prof.push(new THREE.Vector2(0, -H_BODY - fr));
  const base = new THREE.Mesh(new THREE.LatheGeometry(prof.reverse(), 192), baseMat); base.material.side = THREE.DoubleSide; base.receiveShadow = true; jar.add(base);

  const gold = new THREE.MeshStandardMaterial({ color: 0xf0c050, metalness: 1, roughness: 0.2 });
  const collar = new THREE.Mesh(new THREE.CylinderGeometry(R_BODY + 1, R_BODY + 1, 7, 192, 1, true), gold); collar.position.y = -0.5; jar.add(collar);

  const rimMat = new THREE.MeshStandardMaterial({ color: 0xf6f1e8, roughness: 0.5 });
  const rim = new THREE.Mesh(new THREE.RingGeometry(R_IN, R_BODY + 1, 192), rimMat); rim.rotation.x = -Math.PI / 2; rim.position.y = 3; rim.receiveShadow = true; jar.add(rim);
  const bead = new THREE.Mesh(new THREE.TorusGeometry(R_BODY + 0.5, 1.4, 8, 192), gold); bead.rotation.x = Math.PI / 2; bead.position.y = 3; jar.add(bead);

  const wall = new THREE.Mesh(new THREE.CylinderGeometry(R_IN, R_IN, 80, 128, 1, true), new THREE.MeshStandardMaterial({ color: 0xa79fb8, roughness: 0.85, side: THREE.BackSide }));
  wall.position.y = 3 - 40; wall.receiveShadow = true; jar.add(wall);

  const pt = powderTextures();
  const powder = new THREE.Mesh(new THREE.CircleGeometry(R_IN - 0.6, 96), new THREE.MeshStandardMaterial({ map: pt.map, bumpMap: pt.bump, bumpScale: 2.4, roughness: 1, metalness: 0 }));
  powder.rotation.x = -Math.PI / 2; powder.position.y = POWDER_Y; powder.receiveShadow = true; jar.add(powder);

  // lid: its own pivot at the lid's centre so it can tilt while it hovers
  const lidPivot = new THREE.Group(); lidPivot.position.y = H_LID / 2; jar.add(lidPivot);
  const r = 11, lp = [new THREE.Vector2(0, H_LID)];
  lp.push(new THREE.Vector2(R_LID - r, H_LID));
  for (let i = 1; i <= 8; i++) { const a = (i / 8) * Math.PI / 2; lp.push(new THREE.Vector2(R_LID - r + Math.sin(a) * r, H_LID - r + Math.cos(a) * r)); }
  lp.push(new THREE.Vector2(R_LID, 2), new THREE.Vector2(R_LID - 2, 0), new THREE.Vector2(R_LID - 10, 0), new THREE.Vector2(R_LID - 10, 5), new THREE.Vector2(0, 5));
  const lidMat = new THREE.MeshStandardMaterial({ color: 0xffffff, emissive: 0x38363e, roughness: 0.55, metalness: 0, side: THREE.DoubleSide });
  const lid = new THREE.Mesh(new THREE.LatheGeometry(lp, 192), lidMat); lid.position.y = -H_LID / 2; lid.castShadow = true; lid.receiveShadow = true;
  lidPivot.add(lid);
  return { jar, lidPivot };
}

// ---------- entry ----------
export function mountHero3D(box, opts) {
  const canvas = box.querySelector('.nh-hero-3d');
  if (!canvas || canvas.dataset.h3d) return;
  canvas.dataset.h3d = '1';
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
  const desktop = window.matchMedia('(min-width: 901px)');
  if (reduced.matches || !webglOk()) return;

  const slideId = box.classList.contains('nh-hero-slide-2') ? 2 : 1;
  const cfg = SLIDE_CONFIGS[slideId] || SLIDE_CONFIGS[1];

  const base = opts.base, urlOf = (f) => new URL(base + f, document.baseURI).href;
  let S = null, ready = null, running = false, raf = 0, visible = true, shown = false;
  const lowPower = (navigator.hardwareConcurrency || 4) <= 4;
  const isActive = () => box.classList.contains('active');
  const want = () => desktop.matches && !reduced.matches && visible && !document.hidden && isActive();

  const init = async () => {
    const [label, badge, plate] = await Promise.all([
      new THREE.TextureLoader().loadAsync(urlOf(cfg.label)),
      new THREE.TextureLoader().loadAsync(urlOf(cfg.badgeImg)),
      loadImage(urlOf(cfg.plateImg)),
    ]);
    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' });
    renderer.setClearColor(0x000000, 0);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.shadowMap.enabled = true; renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    const scene = new THREE.Scene();
    scene.environment = studioEnv(renderer); scene.environmentIntensity = 0.38;
    const cam = new THREE.PerspectiveCamera(); cam.position.set(0, 0, CAM_D);

    scene.add(new THREE.HemisphereLight(0xffffff, 0xe3d9f5, 1.25));
    const key = new THREE.DirectionalLight(0xfff8ee, 2.4); key.position.set(-420, CAM_Y - cfg.topY - H_BODY / 2 + 640, 520); key.target.position.set(0, CAM_Y - cfg.topY - H_BODY / 2, 0);
    key.castShadow = true; key.shadow.mapSize.set(lowPower ? 1024 : 2048, lowPower ? 1024 : 2048);
    Object.assign(key.shadow.camera, { left: -330, right: 330, top: 330, bottom: -330, near: 100, far: 1800 });
    key.shadow.bias = -0.0004; key.shadow.normalBias = 1.2; key.shadow.radius = 5; key.shadow.blurSamples = 14;
    scene.add(key, key.target);

    const root = new THREE.Group(); scene.add(root);                        // world position of the jar's centre
    const bob = new THREE.Group(); root.add(bob);                           // float / tilt / parallax live here
    const { jar, lidPivot } = buildJar(THREE, label, renderer, cfg);
    jar.position.y = H_BODY / 2; bob.add(jar);                              // jar origin is its collar; pivot is mid-body
    const centreY = CAM_Y - (cfg.topY + H_BODY / 2);                          // world y of the pivot
    root.position.set(0, centreY, 0);
    const collarWorldY = centreY + H_BODY / 2;

    // soft contact shadow on the surface under the jar
    const shTex = radialTexture([[0, 'rgba(48,30,84,0.55)'], [0.55, 'rgba(48,30,84,0.28)'], [1, 'rgba(48,30,84,0)']]);
    const shadow = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshBasicMaterial({ map: shTex, transparent: true, depthWrite: false }));
    shadow.rotation.x = -Math.PI / 2; shadow.position.set(0, collarWorldY - H_BODY - 14, 0); scene.add(shadow);

    // the sticker stays in front of everything, like in the 2D version
    badge.colorSpace = THREE.SRGBColorSpace;
    const zb = 600, kb = (CAM_D - zb) / CAM_D;
    const badgeW = cfg.badge[2];
    const badgeMesh = new THREE.Mesh(new THREE.PlaneGeometry(badgeW * kb, badgeW * kb), new THREE.MeshBasicMaterial({ map: badge, transparent: true, depthTest: false, depthWrite: false }));
    badgeMesh.position.set((cfg.badge[0] + badgeW / 2 - cfg.cx) * kb, (CAM_Y - (cfg.badge[1] + badgeW / 2)) * kb, zb); badgeMesh.renderOrder = 20;
    scene.add(badgeMesh);

    // powder particles
    const MAXP = lowPower ? 2600 : 5200;
    const pos = new Float32Array(MAXP * 3), siz = new Float32Array(MAXP), alp = new Float32Array(MAXP), tone = new Float32Array(MAXP * 3), soft = new Float32Array(MAXP);
    const pg = new THREE.BufferGeometry();
    const attrs = { position: new THREE.BufferAttribute(pos, 3), aSize: new THREE.BufferAttribute(siz, 1), aAlpha: new THREE.BufferAttribute(alp, 1), aTone: new THREE.BufferAttribute(tone, 3), aSoft: new THREE.BufferAttribute(soft, 1) };
    Object.entries(attrs).forEach(([k, a]) => { a.setUsage(THREE.DynamicDrawUsage); pg.setAttribute(k, a); });
    pg.setDrawRange(0, 0);
    const pmat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, depthTest: true,
      uniforms: { uF: { value: 1 } },
      vertexShader: 'attribute float aSize;attribute float aAlpha;attribute vec3 aTone;attribute float aSoft;uniform float uF;varying float vA;varying vec3 vC;varying float vS;' +
        'void main(){vec4 mv=modelViewMatrix*vec4(position,1.);gl_Position=projectionMatrix*mv;gl_PointSize=max(1.4,aSize*uF/(-mv.z));' +
        // depth + height shading: nearer / higher dust catches more light, far dust sinks into the scene
        'float lf=clamp(.5+position.z/520.,0.,1.);vC=aTone*(.66+.4*lf);vA=aAlpha*(.7+.3*lf);vS=aSoft;}',
      fragmentShader: 'varying float vA;varying vec3 vC;varying float vS;void main(){float d=length(gl_PointCoord-.5)*2.;if(d>1.)discard;' +
        'float hard=smoothstep(1.,.45,d)*.92;float sft=pow(1.-d,1.6)*.75;gl_FragColor=vec4(vC,mix(hard,sft,vS)*vA);}',
    });
    const points = new THREE.Points(pg, pmat); points.frustumCulled = false; points.renderOrder = 5; scene.add(points);

    const TONES = [[104, 120, 58], [132, 128, 64], [176, 150, 84], [140, 104, 62], [92, 108, 62], [226, 208, 158]].map((c) => c.map((v) => v / 255));
    const WEIGHTS = [0.24, 0.2, 0.2, 0.12, 0.12, 0.12];
    const pickTone = () => { let r = Math.random(); for (let i = 0; i < WEIGHTS.length; i++) { if ((r -= WEIGHTS[i]) < 0) return i; } return 0; };

    S = { renderer, scene, cam, root, bob, jar, lidPivot, shadow, pmat, pg, attrs, pos, siz, alp, tone, soft, parts: [], MAXP, TONES, pickTone, key, collarWorldY, centreY, plate, w: 0, h: 0, sc: 1, ox: 0, oy: 0, cfg };
    return S;
  };

  const layout = () => {
    if (!S) return;
    const W = box.clientWidth, H = box.clientHeight; if (!W || !H) return;
    const dpr = Math.min(window.devicePixelRatio || 1, lowPower ? 1.5 : 2);
    const s = Math.max(W / BW, H / BH), ox = W - BW * s, oy = (H - BH * s) / 2;
    S.renderer.setPixelRatio(dpr); S.renderer.setSize(W, H, false);
    const near = 200, far = 6000, f = CAM_D * s, px = ox + cfg.cx * s, py = oy + CAM_Y * s;
    S.cam.projectionMatrix.makePerspective(-px * near / f, (W - px) * near / f, py * near / f, -(H - py) * near / f, near, far);
    S.cam.projectionMatrixInverse.copy(S.cam.projectionMatrix).invert();
    S.pmat.uniforms.uF.value = f * dpr;
    Object.assign(S, { w: W, h: H, sc: s, ox, oy });
  };

  // ---------- pointer ----------
  const ptr = { x: 0, y: 0, inside: false };
  const target = { ry: 0, rx: 0, px: 0, py: 0, pz: 0 }, cur = { ry: 0, rx: 0, px: 0, py: 0, pz: 0 };
  const onMove = (e) => {
    if (!S || !S.w) return;
    const r = box.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
    const cx = S.ox + cfg.cx * S.sc, cy = S.oy + (cfg.topY + H_BODY / 2 - 10) * S.sc;
    const hw = R_BODY * S.sc * 1.55, hh = (H_BODY / 2 + 70) * S.sc * 1.45;
    const nx = (x - cx) / hw, ny = (y - cy) / hh, d = Math.max(Math.abs(nx), Math.abs(ny));
    if (d >= 1) { target.ry = target.rx = target.px = target.py = target.pz = 0; return; }
    const f = 1 - smooth(0.75, 1, d);                // eases off at the edge of the product area
    target.ry = nx * 0.3 * f; target.rx = ny * 0.17 * f;
    target.px = nx * 12 * f; target.py = -ny * 8 * f; target.pz = 30 * f;
  };
  const onLeave = () => { target.ry = target.rx = target.px = target.py = target.pz = 0; };
  box.addEventListener('pointermove', onMove, { passive: true });
  box.addEventListener('pointerleave', onLeave, { passive: true });
  box.addEventListener('pointerup', (e) => { if (e.pointerType === 'touch') onLeave(); }, { passive: true });
  box.addEventListener('pointercancel', onLeave, { passive: true });

  // ---------- sequence state ----------
  let seqT0 = 0, seqOn = false, nextStart = 0, last = 0, emitCarry = 0, clock = 0;
  const _v = new THREE.Vector3(), _q = new THREE.Quaternion(), _jet = new THREE.Vector3();

  const resetLid = () => { S.lidPivot.position.set(0, H_LID / 2, 0); S.lidPivot.rotation.set(0, 0, 0); S.lidPivot.scale.setScalar(1); };

  const spawn = (n) => {
    S.jar.updateWorldMatrix(true, false);
    S.jar.getWorldQuaternion(_q);
    for (let i = 0; i < n && S.parts.length < S.MAXP; i++) {
      const ang = Math.random() * 6.283, rr = Math.sqrt(Math.random()), z = Math.random(), cloud = Math.random() < 0.06;
      const size = cloud ? 8 + Math.random() * 9 : (0.8 + Math.random() * 1.7) * (0.65 + 0.75 * z) * (Math.random() < 0.05 ? 1.7 : 1);
      const lx = Math.cos(ang) * rr * R_IN * 0.84, lz = Math.sin(ang) * rr * R_IN * 0.84;
      _v.set(lx, POWDER_Y + 1, lz); S.jar.localToWorld(_v);
      const jet = (110 + Math.random() * 330) * (0.55 + 0.45 * (1 - rr * 0.6));
      _jet.set((lx / R_IN) * 70 + (Math.random() - 0.5) * 80, jet, (lz / R_IN) * 70 + (Math.random() - 0.5) * 80).applyQuaternion(_q);
      S.parts.push({ x: _v.x, y: _v.y, z: _v.z, vx: _jet.x, vy: _jet.y, vz: _jet.z, size, tone: cloud ? (Math.random() < 0.5 ? 5 : 1) : S.pickTone(), soft: cloud || Math.random() < 0.18, seed: Math.random() * 6.283, age: 0, zf: z,
        life: 2.2 + Math.random() * 1.2 + (1 - z) * 0.3, base: cloud ? 0.05 + Math.random() * 0.06 : 0.5 + Math.random() * 0.45, mass: cloud ? 0.5 : 0.7 + size * 0.25 });
    }
  };

  const stepParticles = (dt, t) => {
    const P = S.parts, jx = S.root.position.x;
    let n = 0;
    for (let i = P.length - 1; i >= 0; i--) {
      const p = P[i]; p.age += dt;
      if (p.age >= p.life) { P[i] = P[P.length - 1]; P.pop(); continue; }
      const a = p.x * 0.0105 + t * 0.55 + p.seed * 0.3, b = p.y * 0.0126 - t * 0.4, c = p.z * 0.0098 + t * 0.3;
      const core = Math.exp(-(Math.pow((p.x - jx) / 230, 2) + Math.pow(p.z / 230, 2))) * Math.exp(-p.age * 0.55);
      const wx = Math.sin(a) * Math.cos(b) * 36 + 7, wy = Math.cos(a) * Math.sin(b) * 28 + 52 * core, wz = Math.sin(c + a) * Math.cos(b) * 26;
      const k = 0.85 + p.mass * 0.45;
      p.vx += (wx - p.vx) * k * dt; p.vy += (wy - p.vy) * k * dt - 20 * p.mass * dt; p.vz += (wz - p.vz) * k * dt;
      p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
    }
    for (let i = 0; i < P.length; i++) {
      const p = P[i], u = p.age / p.life;
      const al = p.base * (0.55 + 0.45 * p.zf) * Math.min(1, p.age / 0.12) * (1 - smooth(0.5, 1, u));
      if (al < 0.01) continue;
      const o = n * 3; S.pos[o] = p.x; S.pos[o + 1] = p.y; S.pos[o + 2] = p.z;
      const tn = S.TONES[p.tone]; S.tone[o] = tn[0]; S.tone[o + 1] = tn[1]; S.tone[o + 2] = tn[2];
      S.siz[n] = Math.max(p.size * (1 + u * 0.35), 0.7 / S.sc) * 1.15 * 2; S.alp[n] = al; S.soft[n] = p.soft ? 1 : 0; n++;
    }
    S.pg.setDrawRange(0, n);
    for (const a of Object.values(S.attrs)) a.needsUpdate = true;
  };

  const lidPose = (t) => {
    const rise = easeIO((t - T_RISE0) / T_RISE), ret = easeIO((t - T_RET0) / T_RET), p = rise * (1 - ret);
    const A = easeO(p / 0.5), B = easeIO((p - 0.35) / 0.65);
    const wob = Math.sin(t * 1.9) * 1.6 * B * (1 - ret);
    let dy = LID.lift * A + 8 * B + wob;
    const st = t - (T_RET0 + T_RET);
    if (st > 0 && st < 0.9) dy -= 1.6 * Math.exp(-st * 8) * Math.sin(st * 24);   // soft click as it settles
    return { p, dx: LID.drift * B, dy, dz: -46 * B, rz: LID.rot * B, rx: -0.1 * B, sc: 1 - 0.04 * B };
  };

  const frame = (now) => {
    raf = 0; if (!running) return;
    const dt = Math.min((now - last) / 1000, 0.05); last = now; clock += dt;
    // headroom for the lifted lid (keeps it inside the hero on short viewports)
    const maxLift = Math.max(8, Math.min(LID.lift, ((S.oy + (cfg.topY - H_LID) * S.sc - 6) / S.sc) - 22 - 8));
    // cursor easing (time-corrected), then idle float on top
    const k = 1 - Math.exp(-dt * 6.5);
    for (const key of Object.keys(cur)) cur[key] += (target[key] - cur[key]) * k;
    const fl = Math.sin(clock * 1.15) * 5, sw = Math.sin(clock * 0.7) * 0.03, sp = Math.sin(clock * 0.9 + 1) * 0.014;
    S.bob.rotation.set(cur.rx + sp, cur.ry + sw, 0);
    S.bob.position.set(cur.px, cur.py + fl, cur.pz);
    const lift = (fl + 5) / 10;                                        // 0..1 float phase drives the shadow
    S.shadow.scale.set(470 * (1 - 0.05 * lift), 470 * 0.9 * (1 - 0.05 * lift), 1);
    S.shadow.material.opacity = 0.55 - 0.15 * lift - Math.min(0.15, Math.abs(cur.ry) * 0.3);
    S.shadow.position.x = -cur.ry * 40 + cur.px * 0.4;

    if (!seqOn && now >= nextStart && S.ready) { seqOn = true; seqT0 = now; S.parts.length = 0; emitCarry = 0; }
    if (seqOn) {
      const t = (now - seqT0) / 1000, ps = lidPose(t);
      const lift2 = ps.dy * (maxLift / LID.lift);
      S.lidPivot.position.set(ps.dx, H_LID / 2 + lift2, ps.dz); S.lidPivot.rotation.set(ps.rx, 0, ps.rz); S.lidPivot.scale.setScalar(ps.sc);
      if (t > T_EMIT0 && t < T_EMIT1) {
        const N = lowPower ? 1200 : 2600, e = Math.exp(-(t - T_EMIT0) * 1.15) / 0.82;
        emitCarry += N * e * dt; const n = Math.floor(emitCarry); emitCarry -= n; if (n) { S.bob.updateMatrixWorld(true); spawn(n); }
      }
      stepParticles(dt, t);
      if (t >= T_END) { seqOn = false; resetLid(); S.parts.length = 0; S.pg.setDrawRange(0, 0); nextStart = now + 3600; }
    }
    S.renderer.render(S.scene, S.cam);
    if (!shown) { shown = true; box.dataset.h3d = 'on'; }
    raf = requestAnimationFrame(frame);
  };

  const start = () => {
    if (running || !S) return;
    running = true; layout(); last = performance.now();
    if (!seqOn) nextStart = last + 450;
    S.ready = true; raf = requestAnimationFrame(frame);
  };
  const stop = (hide) => {
    running = false; cancelAnimationFrame(raf); raf = 0;
    if (S) { seqOn = false; resetLid(); S.parts.length = 0; S.pg.setDrawRange(0, 0); }
    if (hide) { shown = false; delete box.dataset.h3d; }
  };

  const sync = async () => {
    if (!desktop.matches || reduced.matches) { stop(true); return; }
    if (want()) {
      if (!S) { if (!ready) ready = init().catch((e) => { console.warn('hero3d unavailable, using the 2D animation', e); ready = null; opts.failed = true; }); await ready; if (!S) return; }
      if (want()) start();
    } else if (running) {
      // leave the 3D scene on screen while the slider cross-fades away, then park the loop
      setTimeout(() => { if (!want()) stop(false); }, 750);
    }
  };

  new MutationObserver(sync).observe(box, { attributes: true, attributeFilter: ['class'] });
  new IntersectionObserver((es) => es.forEach((e) => { visible = e.isIntersecting; sync(); }), { threshold: 0.3 }).observe(box);
  new ResizeObserver(() => layout()).observe(box);
  document.addEventListener('visibilitychange', sync);
  desktop.addEventListener?.('change', sync); reduced.addEventListener?.('change', sync);
  sync();
}
