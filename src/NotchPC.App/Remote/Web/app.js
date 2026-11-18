'use strict';

// NotchPC phone remote. Pairs once with the code from the QR (#p=...), keeps its own token in localStorage and
// talks to the PC over one WebSocket. No external libraries.
(() => {
  const TOKEN_KEY = 'notchpc.token';
  const TAB_KEY = 'notchpc.tab';

  const T = {
    pl: {
      welcomeTitle: 'Połącz z komputerem',
      welcomeText: 'Otwórz NotchPC na komputerze → zakładka Telefon i zeskanuj kod QR aparatem. Telefon musi być w tej samej sieci Wi-Fi.',
      pairFailed: 'Ten kod QR wygasł albo został już użyty. Zeskanuj nowy.',
      padHint: 'Przesuwaj palcem · stuknij = klik · dwa palce: przewijanie, stuknięcie = prawy',
      left: 'Lewy', right: 'Prawy', drag: 'Przytrzymaj',
      typeHere: 'Pisz tutaj…',
      volume: 'Głośność', mute: 'Wycisz', unmute: 'Włącz dźwięk',
      nothingPlaying: 'Nic nie gra', appsEmpty: 'Brak skrótów. Dodaj je w notchu, w zakładce Aplikacje.',
      gameHint: 'Statystyki aplikacji na pierwszym planie komputera. Temperatura CPU wymaga trybu administratora czujników.',
      overlayOn: 'Nakładka na PC: włączona (wyłącz)', overlayOff: 'Nakładka na PC: wyłączona (włącz)',
      slideStart: 'Od początku', slideCurrent: 'Od bieżącego', slideBlack: 'Czarny ekran', slideEnd: 'Zakończ',
      lock: 'Zablokuj', sleep: 'Uśpij', restart: 'Uruchom ponownie', shutdown: 'Wyłącz', cancelPower: 'Anuluj wyłączanie',
      confirmShutdown: 'Wyłączyć komputer?', confirmRestart: 'Uruchomić komputer ponownie?', confirmSleep: 'Uśpić komputer?',
      powerDelay: 'Wyłączenie i restart następują po {0} s – w tym czasie możesz je anulować.',
      forget: 'Rozłącz ten telefon', confirmForget: 'Zapomnieć to parowanie na tym telefonie?',
      unpaired: 'Ten telefon nie jest już sparowany.', connecting: 'Łączenie…', sent: 'Wysłano',
      tabPad: 'Touchpad', tabKeys: 'Klawiatura', tabMusic: 'Muzyka', tabApps: 'Aplikacje', tabGame: 'Gra', tabSlides: 'Slajdy', tabPower: 'Zasilanie',
    },
    en: {
      welcomeTitle: 'Connect to your PC',
      welcomeText: 'Open NotchPC on the PC → Phone tab and scan the QR code with the camera. The phone must be on the same Wi-Fi.',
      pairFailed: 'This QR code expired or was already used. Scan a new one.',
      padHint: 'Move a finger · tap = click · two fingers: scroll, tap = right click',
      left: 'Left', right: 'Right', drag: 'Hold',
      typeHere: 'Type here…',
      volume: 'Volume', mute: 'Mute', unmute: 'Unmute',
      nothingPlaying: 'Nothing playing', appsEmpty: 'No shortcuts. Add them in the notch, Apps tab.',
      gameHint: 'Stats of the app in front on the PC. CPU temperature needs the sensors\' administrator mode.',
      overlayOn: 'PC overlay: on (turn off)', overlayOff: 'PC overlay: off (turn on)',
      slideStart: 'From start', slideCurrent: 'From current', slideBlack: 'Black screen', slideEnd: 'End show',
      lock: 'Lock', sleep: 'Sleep', restart: 'Restart', shutdown: 'Shut down', cancelPower: 'Cancel shutdown',
      confirmShutdown: 'Shut down the PC?', confirmRestart: 'Restart the PC?', confirmSleep: 'Put the PC to sleep?',
      powerDelay: 'Shutdown and restart happen after {0} s – you can cancel them meanwhile.',
      forget: 'Disconnect this phone', confirmForget: 'Forget the pairing on this phone?',
      unpaired: 'This phone is no longer paired.', connecting: 'Connecting…', sent: 'Sent',
      tabPad: 'Touchpad', tabKeys: 'Keys', tabMusic: 'Music', tabApps: 'Apps', tabGame: 'Game', tabSlides: 'Slides', tabPower: 'Power',
    },
  };

  let lang = (navigator.language || 'pl').toLowerCase().startsWith('pl') ? 'pl' : 'en';
  const $ = (id) => document.getElementById(id);
  const t = (key, ...args) => (T[lang][key] || key).replace(/\{(\d)\}/g, (_, i) => args[i]);

  function applyTexts() {
    document.documentElement.lang = lang;
    document.querySelectorAll('[data-t]').forEach((el) => { el.textContent = t(el.dataset.t); });
    document.querySelectorAll('[data-tp]').forEach((el) => { el.placeholder = t(el.dataset.tp); });
  }

  let toastTimer = 0;
  function toast(text) {
    const el = $('toast');
    el.textContent = text;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.hidden = true; }, 2200);
  }

  const buzz = (ms = 8) => { if (navigator.vibrate) navigator.vibrate(ms); };

  // ---------- connection ----------
  let ws = null;
  let ready = false;
  let retry = 0;
  let current = null;
  let state = { overlay: false };

  function setStatus(s) { $('status').dataset.state = s; }

  function send(obj) {
    if (ws && ready && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  function connect() {
    const token = localStorage.getItem(TOKEN_KEY);
    if (!token) { showWelcome(); return; }
    setStatus('wait');
    ws = new WebSocket(`ws://${location.host}/ws`);
    ws.onopen = () => ws.send(JSON.stringify({ t: 'auth', token }));
    ws.onmessage = (e) => onMessage(JSON.parse(e.data));
    ws.onclose = () => {
      ready = false;
      setStatus('off');
      if (!localStorage.getItem(TOKEN_KEY)) return;
      retry = Math.min(retry + 1, 6);
      setTimeout(connect, 500 * retry);
    };
  }

  function onMessage(m) {
    switch (m.t) {
      case 'hello':
        ready = true;
        retry = 0;
        setStatus('on');
        if (m.lang === 'pl' || m.lang === 'en') { lang = m.lang; applyTexts(); }
        $('pc').textContent = m.pc || '';
        renderApps(m.apps || []);
        renderMusic(m.music);
        $('powerHint').textContent = m.powerDelay > 0 ? t('powerDelay', m.powerDelay) : '';
        showTabs();
        if (current === 'game') send({ t: 'stats', on: true });
        break;
      case 'music':
        renderMusic(m.music);
        break;
      case 'stats':
        renderStats(m);
        break;
      case 'unpaired':
        localStorage.removeItem(TOKEN_KEY);
        if (ws) ws.close();
        showWelcome(t('unpaired'));
        break;
    }
  }

  async function pair(code) {
    history.replaceState(null, '', '/'); // the code is one-time: do not keep it in the address bar
    try {
      const r = await fetch('/api/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
      if (!r.ok) throw new Error(String(r.status));
      const j = await r.json();
      localStorage.setItem(TOKEN_KEY, j.token);
      connect();
    } catch {
      showWelcome(t('pairFailed'));
    }
  }

  // ---------- screens ----------
  function showWelcome(error) {
    document.querySelectorAll('.screen').forEach((s) => { s.hidden = s.id !== 'welcome'; });
    $('tabs').hidden = true;
    $('welcomeError').hidden = !error;
    $('welcomeError').textContent = error || '';
  }

  function showTabs() {
    $('tabs').hidden = false;
    let tab = '';
    try { tab = localStorage.getItem(TAB_KEY) || ''; } catch { /* private mode */ }
    select($(tab) && tab !== 'welcome' ? tab : 'touchpad');
  }

  function select(tab) {
    if (current === 'game' && tab !== 'game') send({ t: 'stats', on: false });
    current = tab;
    try { localStorage.setItem(TAB_KEY, tab); } catch { /* ignore */ }
    document.querySelectorAll('.screen').forEach((s) => { s.hidden = s.id !== tab; });
    document.querySelectorAll('nav button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
    if (tab === 'game') send({ t: 'stats', on: true });
  }

  document.querySelectorAll('nav button').forEach((b) => b.addEventListener('click', () => select(b.dataset.tab)));

  // ---------- touchpad ----------
  const pad = $('pad');
  let touches = new Map();
  let gesture = null;
  let pending = { dx: 0, dy: 0, sx: 0, sy: 0 };
  let frame = 0;

  function flush() {
    frame = 0;
    const mx = Math.trunc(pending.dx), my = Math.trunc(pending.dy);
    if (mx || my) { send({ t: 'move', dx: mx, dy: my }); pending.dx -= mx; pending.dy -= my; }
    const sx = Math.trunc(pending.sx), sy = Math.trunc(pending.sy);
    if (sx || sy) { send({ t: 'scroll', dx: sx, dy: sy }); pending.sx -= sx; pending.sy -= sy; }
  }
  const schedule = () => { if (!frame) frame = requestAnimationFrame(flush); };

  pad.addEventListener('touchstart', (e) => {
    e.preventDefault();
    for (const tt of e.changedTouches) touches.set(tt.identifier, { x: tt.clientX, y: tt.clientY });
    if (!gesture) gesture = { start: performance.now(), max: 0, moved: 0 };
    gesture.max = Math.max(gesture.max, touches.size);
  }, { passive: false });

  pad.addEventListener('touchmove', (e) => {
    e.preventDefault();
    let sumX = 0, sumY = 0, n = 0;
    for (const tt of e.changedTouches) {
      const prev = touches.get(tt.identifier);
      if (!prev) continue;
      sumX += tt.clientX - prev.x; sumY += tt.clientY - prev.y; n++;
      touches.set(tt.identifier, { x: tt.clientX, y: tt.clientY });
    }
    if (!n || !gesture) return;
    const dx = sumX / n, dy = sumY / n;
    gesture.moved += Math.abs(dx) + Math.abs(dy);
    if (touches.size >= 2) {
      // Natural scrolling, like a laptop touchpad: fingers down = content down.
      pending.sy += dy * 4; pending.sx -= dx * 4;
    } else {
      const speed = Math.hypot(dx, dy);
      const gain = 1.6 + Math.min(speed, 30) / 12; // faster finger → longer move
      pending.dx += dx * gain; pending.dy += dy * gain;
    }
    schedule();
  }, { passive: false });

  function touchEnd(e) {
    e.preventDefault();
    for (const tt of e.changedTouches) touches.delete(tt.identifier);
    if (touches.size === 0 && gesture) {
      const quick = performance.now() - gesture.start < 250 && gesture.moved < 12;
      if (quick) { send({ t: 'click', b: gesture.max >= 2 ? 'right' : 'left' }); buzz(); }
      gesture = null;
    }
  }
  pad.addEventListener('touchend', touchEnd, { passive: false });
  pad.addEventListener('touchcancel', touchEnd, { passive: false });

  document.querySelectorAll('[data-click]').forEach((b) => b.addEventListener('click', () => { send({ t: 'click', b: b.dataset.click }); buzz(); }));

  let dragging = false;
  $('drag').addEventListener('click', () => {
    dragging = !dragging;
    send({ t: dragging ? 'down' : 'up', b: 'left' });
    $('drag').classList.toggle('on', dragging);
    buzz();
  });

  // ---------- keyboard ----------
  // The field keeps what was typed; each change is sent as "backspaces + new text" so autocorrect and swipe typing work.
  const typing = $('typing');
  let last = '';
  typing.addEventListener('input', () => {
    const now = typing.value;
    let i = 0;
    while (i < last.length && i < now.length && last[i] === now[i]) i++;
    const removed = last.length - i;
    const added = now.slice(i);
    if (removed > 0) send({ t: 'backspace', n: removed });
    if (added) send({ t: 'text', s: added });
    last = now;
    if (now.length > 200) { typing.value = ''; last = ''; }
  });
  typing.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); send({ t: 'key', k: 'enter' }); typing.value = ''; last = ''; }
    else if (e.key === 'Backspace' && typing.value === '') { send({ t: 'key', k: 'backspace' }); }
  });
  document.querySelectorAll('[data-key]').forEach((b) => b.addEventListener('click', () => { send({ t: 'key', k: b.dataset.key }); buzz(); }));

  // ---------- music ----------
  let volumeDragging = false;
  function renderMusic(m) {
    if (!m) return;
    $('trackTitle').textContent = m.has ? (m.title || '—') : t('nothingPlaying');
    $('trackArtist').textContent = m.has ? (m.artist || '') : '';
    $('playBtn').textContent = m.playing ? '⏸' : '▶';
    if (!volumeDragging) $('volume').value = m.volume;
    $('volText').textContent = `${m.volume}%`;
    $('muteBtn').textContent = m.muted ? t('unmute') : t('mute');
    $('muteBtn').classList.toggle('on', !!m.muted);
  }
  document.querySelectorAll('[data-media]').forEach((b) => b.addEventListener('click', () => { send({ t: 'media', a: b.dataset.media }); buzz(); }));
  const volume = $('volume');
  volume.addEventListener('input', () => { volumeDragging = true; $('volText').textContent = `${volume.value}%`; send({ t: 'volume', v: Number(volume.value) }); });
  volume.addEventListener('change', () => { volumeDragging = false; });

  // ---------- apps ----------
  function renderApps(apps) {
    const list = $('appList');
    list.textContent = '';
    apps.forEach((name, i) => {
      const b = document.createElement('button');
      b.textContent = name;
      b.addEventListener('click', () => { send({ t: 'launch', i }); buzz(); toast(t('sent')); });
      list.appendChild(b);
    });
    $('appsEmpty').hidden = apps.length > 0;
  }

  // ---------- game ----------
  const fmt = (load, temp) => load == null ? '—' : `${Math.round(load)}%${temp != null ? ` · ${Math.round(temp)}°` : ''}`;
  function renderStats(s) {
    $('gameApp').textContent = s.app || '—';
    const fps = $('gameFps');
    fps.textContent = s.fps == null || s.fps < 1 ? '—' : String(s.fps);
    fps.className = 'fps ' + (s.fps == null || s.fps < 1 ? '' : s.fps < 30 ? 'low' : s.fps < 60 ? 'fair' : 'good');
    $('gameCpu').textContent = fmt(s.cpu, s.cpuTemp);
    $('gameGpu').textContent = fmt(s.gpu, s.gpuTemp);
    $('gameRam').textContent = fmt(s.ram, null);
    state.overlay = !!s.overlay;
    $('overlayBtn').textContent = state.overlay ? t('overlayOn') : t('overlayOff');
  }
  $('overlayBtn').addEventListener('click', () => { send({ t: 'overlay' }); buzz(); });

  // ---------- power ----------
  const confirmText = { shutdown: 'confirmShutdown', restart: 'confirmRestart', sleep: 'confirmSleep' };
  document.querySelectorAll('[data-power]').forEach((b) => b.addEventListener('click', () => {
    const a = b.dataset.power;
    if (confirmText[a] && !confirm(t(confirmText[a]))) return;
    send({ t: 'power', a });
    buzz(15);
    toast(t('sent'));
  }));
  $('forget').addEventListener('click', () => {
    if (!confirm(t('confirmForget'))) return;
    localStorage.removeItem(TOKEN_KEY);
    if (ws) ws.close();
    showWelcome();
  });

  // ---------- start ----------
  applyTexts();
  const code = new URLSearchParams(location.hash.slice(1)).get('p');
  if (code) pair(code); else connect();
})();
