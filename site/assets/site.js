// MTRTracer site: navigation, reveals, the live app replica on the home page, docs search and scrollspy.
(() => {
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ---------- mobile menu ----------
  const menuBtn = document.querySelector('.menu-btn');
  const nav = document.querySelector('.nav');
  menuBtn?.addEventListener('click', () => {
    const open = nav.classList.toggle('open');
    menuBtn.setAttribute('aria-expanded', String(open));
  });
  nav?.addEventListener('click', (e) => {
    if (e.target.closest('a')) nav.classList.remove('open');
  });

  // ---------- reveals ----------
  const reveals = document.querySelectorAll('.reveal');
  if (reduce || !('IntersectionObserver' in window)) reveals.forEach((el) => el.classList.add('in'));
  else {
    const io = new IntersectionObserver(
      (entries) =>
        entries.forEach((e) => {
          if (e.isIntersecting) {
            e.target.classList.add('in');
            io.unobserve(e.target);
          }
        }),
      { rootMargin: '0px 0px -8% 0px' },
    );
    reveals.forEach((el, i) => {
      if (el.closest('.hero')) setTimeout(() => el.classList.add('in'), 80 + i * 90);
      else io.observe(el);
    });
  }

  // ---------- install tabs ----------
  document.querySelectorAll('.tabs').forEach((tabs) => {
    const buttons = tabs.querySelectorAll('[role="tab"]');
    buttons.forEach((b) =>
      b.addEventListener('click', () => {
        buttons.forEach((x) => {
          const on = x === b;
          x.setAttribute('aria-selected', String(on));
          document.getElementById(x.getAttribute('aria-controls')).hidden = !on;
        });
      }),
    );
  });

  // Links to #install-linux open the Linux tab of the install section.
  const openLinux = () => {
    const tab = document.getElementById('t-linux');
    if (!tab) return;
    tab.click();
    document.getElementById('install')?.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth' });
  };
  document.querySelectorAll('a[href="#install-linux"]').forEach((a) =>
    a.addEventListener('click', (e) => {
      e.preventDefault();
      openLinux();
      history.replaceState(null, '', '#install-linux');
    }),
  );
  if (location.hash === '#install-linux') setTimeout(openLinux, 50);

  // ---------- live replica ----------
  const tbody = document.getElementById('aw-rows');
  if (tbody) initReplica(tbody);

  function latColor(v) {
    if (v == null) return '#5b5e6b';
    if (v < 30) return '#57d29a';
    if (v < 60) return '#b4d860';
    if (v < 100) return '#ecbf4f';
    if (v < 180) return '#ee8c4b';
    return '#ec5d55';
  }
  function fmt(v) {
    return v == null ? '—' : v < 10 ? v.toFixed(2) : v < 100 ? v.toFixed(1) : String(Math.round(v));
  }

  function initReplica(tbody) {
    const hops = [
      { name: 'router.lan', ip: '10.0.0.1', base: 0.9, spread: 0.4, loss: 0 },
      { name: 'gw1.waw.example-isp.net', ip: '198.51.100.10', base: 3.2, spread: 1.6, loss: 0 },
      { name: '', ip: '100.64.12.1', base: 4.0, spread: 1.2, loss: 0 },
      { name: 'ae7.waw-bb1', ip: '198.51.100.20', base: 5.1, spread: 1.2, loss: 0.3 },
      { name: '', ip: '* * *', base: 0, spread: 0, loss: 1, silent: true },
      { name: 'ber-ix', ip: '198.51.100.30', base: 9.8, spread: 1.4, loss: 0 },
      { name: 'ber-b2-link', ip: '203.0.113.11', base: 10.6, spread: 1.2, loss: 0 },
      { name: 'ham-b1-link', ip: '203.0.113.12', base: 13.5, spread: 1.4, loss: 0 },
      { name: 'ams-bb2-link', ip: '203.0.113.13', base: 19.9, spread: 1.6, loss: 0.08 },
      { name: 'ffm-bb1-link', ip: '203.0.113.14', base: 24.6, spread: 1.4, loss: 0 },
      { name: 'fra16s56-in-f14', ip: '142.250.185.78', base: 25.6, spread: 1, loss: 0, dest: true },
    ];
    const st = hops.map(() => ({ sent: 0, recv: 0, last: null, best: null, worst: null, sum: 0, hist: [] }));
    const rows = hops.map((h, i) => {
      const tr = document.createElement('tr');
      if (h.dest) tr.className = 'dest';
      tr.innerHTML = `<td>${h.dest ? `<span>${i + 1}</span>` : i + 1}</td>
        <td class="host">${h.silent ? '<span class="muted" style="margin:0">Нет ответа</span>' : (h.name || '') + `<span${h.name ? '' : ' style="margin:0;color:var(--text)"'}>${h.ip}</span>`}</td>
        <td data-k="loss">0</td><td data-k="sent">0</td><td data-k="last">—</td><td data-k="avg">—</td><td data-k="best">—</td><td data-k="worst">—</td>
        <td><svg class="spark" width="96" height="16" aria-hidden="true"></svg></td>`;
      tbody.appendChild(tr);
      return tr;
    });

    const tick = () => {
      hops.forEach((h, i) => {
        const s = st[i];
        s.sent++;
        const lost = h.silent || Math.random() < h.loss;
        if (lost) s.hist.push(null);
        else {
          const spike = Math.random() < 0.04 ? Math.random() * 30 : 0;
          const v = Math.max(0.3, h.base + (Math.random() - 0.3) * h.spread + spike);
          s.recv++;
          s.last = v;
          s.sum += v;
          s.best = s.best == null ? v : Math.min(s.best, v);
          s.worst = s.worst == null ? v : Math.max(s.worst, v);
          s.hist.push(v);
        }
        if (s.hist.length > 40) s.hist.shift();
        const r = rows[i];
        const loss = s.sent ? ((s.sent - s.recv) * 100) / s.sent : 0;
        const lc = r.querySelector('[data-k="loss"]');
        lc.textContent = loss === 0 ? '0' : loss < 10 ? loss.toFixed(1) : String(Math.round(loss));
        lc.className = loss >= 5 ? 'loss-bad' : loss > 0 ? 'loss-warn' : 'muted';
        r.querySelector('[data-k="sent"]').textContent = s.sent;
        const avg = s.recv ? s.sum / s.recv : null;
        r.querySelector('[data-k="last"]').textContent = fmt(s.last);
        r.querySelector('[data-k="avg"]').innerHTML = avg == null ? '—' : `<i class="dotc" style="background:${latColor(avg)}"></i>${fmt(avg)}`;
        r.querySelector('[data-k="best"]').textContent = fmt(s.best);
        r.querySelector('[data-k="worst"]').textContent = fmt(s.worst);
        const svg = r.querySelector('.spark');
        const max = 60;
        svg.innerHTML = s.hist
          .map((v, k) => {
            const x = k * 2.4;
            if (v == null) return `<rect x="${x}" y="0" width="1.6" height="16" fill="rgba(236,93,85,.55)"/>`;
            const hh = Math.max(1.5, Math.min(1, v / max) * 15);
            return `<rect x="${x}" y="${16 - hh}" width="1.6" height="${hh}" fill="${latColor(v)}"/>`;
          })
          .join('');
      });
      const round = st[0].sent;
      const r = document.getElementById('aw-round');
      if (r) r.textContent = `раунд ${round}`;
      const destAvg = st[st.length - 1].sum / Math.max(1, st[st.length - 1].recv);
      const d = document.getElementById('aw-dest');
      if (d) d.textContent = `цель: 0% потерь, ${destAvg.toFixed(1)} мс`;
      launchPacket();
    };

    // ----- map -----
    const map = document.getElementById('aw-svg');
    const E = window.EUROPE;
    let pathEl = null;
    let pts = [];
    if (map && E) {
      map.setAttribute('viewBox', `120 40 ${E.w - 200} ${E.h - 120}`);
      map.setAttribute('preserveAspectRatio', 'xMidYMid slice');
      const P = E.pts;
      const order = [P.warsaw, P.berlin, P.hamburg, P.amsterdam, P.frankfurt];
      const colors = ['#9b9cf0', '#57d29a', '#57d29a', '#57d29a', '#57d29a'];
      // Gentle arcs between nodes: quadratic curves bowed north.
      let d = `M${order[0][0]} ${order[0][1]}`;
      const segs = [];
      for (let i = 1; i < order.length; i++) {
        const [x1, y1] = order[i - 1];
        const [x2, y2] = order[i];
        const cx = (x1 + x2) / 2;
        const cy = (y1 + y2) / 2 - Math.hypot(x2 - x1, y2 - y1) * 0.12;
        d += ` Q${cx} ${cy} ${x2} ${y2}`;
        segs.push(`<path d="M${x1} ${y1} Q${cx} ${cy} ${x2} ${y2}" fill="none" stroke="${colors[i]}" stroke-width="2.2" stroke-linecap="round" opacity=".9"/>`);
      }
      const labels = [
        [P.warsaw, 'Вы', 'Варшава'],
        [P.frankfurt, 'fra16s56-in-f14.1e100.net', 'Франкфурт · 25.6 мс'],
      ];
      map.innerHTML = `
        <rect x="0" y="0" width="${E.w}" height="${E.h}" fill="#111217"/>
        <path d="${E.d}" fill="#1d1f26" stroke="#2d303a" stroke-width=".7" stroke-linejoin="round"/>
        ${segs.join('')}
        <path id="aw-route" d="${d}" fill="none" stroke="none"/>
        ${order
          .map((p, i) => `<circle cx="${p[0]}" cy="${p[1]}" r="${i === 0 || i === order.length - 1 ? 6 : 4.5}" fill="${colors[i]}" stroke="#111217" stroke-width="2.5"/>`)
          .join('')}
        <circle class="pulse" cx="${P.frankfurt[0]}" cy="${P.frankfurt[1]}" r="8" fill="none" stroke="#b4d860" stroke-width="1.5"/>
        ${labels
          .map(
            ([p, a, b]) => `<g transform="translate(${p[0] + 12} ${p[1] - 14})"><rect width="${Math.max(a.length, b.length) * 6.6 + 16}" height="34" rx="5" fill="rgba(28,29,34,.92)" stroke="#2d303a"/>
            <text x="8" y="14" fill="#e8e8ee" font-size="11.5" font-family="Geist, sans-serif">${a}</text><text x="8" y="27" fill="#8a8d9a" font-size="10.5" font-family="Geist, sans-serif">${b}</text></g>`,
          )
          .join('')}
        <g id="aw-packets"></g>`;
      pathEl = map.querySelector('#aw-route');
      pts = [];
      const len = pathEl.getTotalLength();
      for (let i = 0; i <= 200; i++) {
        const p = pathEl.getPointAtLength((len * i) / 200);
        pts.push([p.x, p.y]);
      }
    }
    const packets = [];
    function launchPacket() {
      if (!pts.length || reduce || document.hidden) return;
      packets.push({ t0: performance.now(), dur: 1600 });
      if (packets.length === 1) requestAnimationFrame(frame);
    }
    function frame(now) {
      const g = document.getElementById('aw-packets');
      if (!g) return;
      let html = '';
      for (let i = packets.length - 1; i >= 0; i--) {
        const p = packets[i];
        const f = (now - p.t0) / p.dur;
        if (f > 1.25) {
          packets.splice(i, 1);
          continue;
        }
        const head = Math.min(1, f);
        const fade = f > 1 ? 1 - (f - 1) / 0.25 : 1;
        const hi = Math.floor(head * 200);
        const lo = Math.max(0, hi - 22);
        const trail = pts.slice(lo, hi + 1).map((q) => q.join(',')).join(' ');
        html += `<polyline points="${trail}" fill="none" stroke="rgba(214,215,255,${0.75 * fade})" stroke-width="2.6" stroke-linecap="round"/>`;
        const h = pts[hi];
        html += `<circle cx="${h[0]}" cy="${h[1]}" r="7" fill="rgba(155,156,240,${0.25 * fade})"/><circle cx="${h[0]}" cy="${h[1]}" r="3.4" fill="rgba(240,240,255,${fade})"/>`;
      }
      g.innerHTML = html;
      if (packets.length) requestAnimationFrame(frame);
    }

    for (let i = 0; i < 12; i++) tick();
    setInterval(tick, reduce ? 3000 : 1000);
  }

  // ---------- docs ----------
  const toc = document.querySelector('.toc');
  if (toc) {
    const links = [...toc.querySelectorAll('a[href^="#"]')];
    const targets = links.map((a) => document.getElementById(a.getAttribute('href').slice(1))).filter(Boolean);
    const setActive = () => {
      const y = window.scrollY + 120;
      let current = targets[0];
      for (const t of targets) if (t.offsetTop <= y && !t.closest('.hidden-by-search')) current = t;
      links.forEach((a) => a.classList.toggle('active', current && a.getAttribute('href') === '#' + current.id));
    };
    window.addEventListener('scroll', setActive, { passive: true });
    setActive();

    const input = toc.querySelector('input');
    const sections = [...document.querySelectorAll('.doc > section')];
    const empty = document.querySelector('.search-empty');
    input?.addEventListener('input', () => {
      const q = input.value.trim().toLowerCase();
      let shown = 0;
      sections.forEach((s) => {
        const hit = !q || s.textContent.toLowerCase().includes(q);
        s.classList.toggle('hidden-by-search', !hit);
        if (hit) shown++;
        const link = toc.querySelector(`a[href="#${s.id}"]`);
        if (link) link.parentElement.hidden = !hit;
      });
      if (empty) empty.style.display = shown ? 'none' : 'block';
    });
    input?.addEventListener('focus', () => toc.classList.add('open'));
    toc.querySelector('.toc-toggle')?.addEventListener('click', () => toc.classList.toggle('open'));
  }

  // Copy buttons on code blocks.
  document.querySelectorAll('pre[data-copy]').forEach((pre) => {
    const b = document.createElement('button');
    b.className = 'btn sm copy';
    b.type = 'button';
    b.textContent = 'Копировать';
    b.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(pre.innerText.trim());
        b.textContent = 'Скопировано';
      } catch {
        b.textContent = 'Не вышло';
      }
      setTimeout(() => (b.textContent = 'Копировать'), 1600);
    });
    const wrap = document.createElement('div');
    wrap.className = 'pre-wrap';
    pre.parentNode.insertBefore(wrap, pre);
    wrap.append(pre, b);
  });
})();
