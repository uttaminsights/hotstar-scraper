// Hotstar dashboard - plain JS, no build step.
const $ = s => document.querySelector(s);
const main = $('#main');
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = n => n == null ? '–' : Number(n).toLocaleString('en-IN');
const api = async (url, opts) => {
  const r = await fetch(url, opts);
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error || r.statusText);
  return body;
};
const utc = d => d ? new Date(String(d).match(/Z|[+-]\d\d:\d\d$/) ? d : d + 'Z') : null;
const fmtDate = d => d ? new Date(d).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' }) : '–';
const fmtDay = d => d ? new Date(d).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' }) : '';
const list = a => (a || []).join(', ');
const rating = r => r == null ? '' : `<span class="imdb">★ ${Number(r).toFixed(1)}</span>`;
const pic = (url, alt = '') => url ? `<img src="${esc(url)}" loading="lazy" alt="${esc(alt)}" onerror="this.remove()">` : '';
const metaLine = parts => `<div class="meta">${parts.filter(Boolean).map(p => `<span>${p}</span>`).join('')}</div>`;
function toast(html, ms = 5000) {
  const t = $('#toast'); t.innerHTML = html; t.hidden = false;
  clearTimeout(toast.timer); toast.timer = setTimeout(() => t.hidden = true, ms);
}

// ---------- Router ----------
let token = 0;
const parts = () => location.hash.slice(1).split('?');
const query = () => new URLSearchParams(parts()[1] || '');
function route() {
  const [path] = parts();
  const [page, id] = (path || 'overview').split('/');
  const menu = { movie: 'movies', show: 'shows' }[page] || page;
  document.querySelectorAll('nav a').forEach(a => a.classList.toggle('active', a.dataset.page === menu));
  token++;
  clearInterval(heroTimer);
  window.scrollTo(0, 0);
  main.innerHTML = `<div class="page"><div class="skeleton" style="height:60vh"></div></div>`;
  const handlers = { overview, movies: () => browse('movies'), shows: () => browse('shows'), home, runs,
                     movie: () => movieDetail(id), show: () => showDetail(id) };
  (handlers[page] || overview)().catch(e => main.innerHTML = `<div class="page"><p class="bad">Could not load: ${esc(e.message)}</p></div>`);
}
window.addEventListener('hashchange', route);
const go = (kind, id) => location.hash = `${kind}/${encodeURIComponent(id)}`;

// ---------- Shared pieces ----------
function tileHtml(r, kind, opts = {}) {
  const sub = [r.release_year, (r.languages || [])[0],
    kind === 'show' && r.season_count ? `${r.season_count} season${r.season_count == 1 ? '' : 's'}` : null,
    kind === 'movie' && r.runtime_minutes ? `${r.runtime_minutes} min` : null].filter(Boolean).join(' · ');
  return `<div class="tile" data-kind="${kind}" data-id="${esc(r.content_id)}">
    <div class="img">${pic(r.image_url, r.title)}
      ${r.imdb_rating != null ? `<span class="badge">★ ${Number(r.imdb_rating).toFixed(1)}</span>` : ''}
      ${opts.rank ? `<span class="rank">${opts.rank}</span>` : ''}</div>
    <div class="cap"><div class="t" title="${esc(r.title)}">${esc(r.title)}</div><div class="s">${esc(sub)}</div></div>
  </div>`;
}
function bindTiles(root = main) {
  root.querySelectorAll('.tile[data-id]:not([data-b])').forEach(el => {
    el.dataset.b = 1;
    el.onclick = () => el.dataset.kind === 'movie' || el.dataset.kind === 'show' ? go(el.dataset.kind, el.dataset.id)
      : el.dataset.url && window.open(el.dataset.url, '_blank', 'noopener');
  });
}
function strip(title, items, kind, link, ranked) {
  if (!items.length) return '';
  return `<section class="row-block"><div class="row-head"><h2>${esc(title)}</h2>${link ? `<a href="${link}">See all ›</a>` : ''}</div>
    <div class="strip">${items.map((r, i) => tileHtml(r, r.kind || kind, { rank: ranked ? i + 1 : null })).join('')}</div></section>`;
}
function bars(rows, { label = r => r.name, value = r => Number(r.n), onClick } = {}) {
  const max = Math.max(1, ...rows.map(value));
  const id = 'b' + Math.random().toString(36).slice(2);
  queueMicrotask(() => onClick && document.querySelectorAll(`#${id} .row`).forEach((el, i) => el.onclick = () => onClick(rows[i])));
  return `<div class="bars" id="${id}">${rows.map(r => `
    <div class="row ${onClick ? 'click' : ''}" title="${esc(label(r))}: ${num(value(r))}">
      <div class="n">${esc(label(r))}</div>
      <div class="track"><div class="fill" style="width:${(value(r) / max * 100).toFixed(1)}%"></div></div>
      <div class="c">${num(value(r))}</div></div>`).join('')}</div>`;
}

// ---------- Overview ----------
let heroTimer;
async function overview() {
  const t = token;
  const [d, run] = await Promise.all([api('/api/overview'), api('/api/run/status')]);
  if (t !== token) return;
  const c = d.counts;
  const last = utc(c.last_run_at);
  const age = last ? Math.floor((Date.now() - last) / 86400000) : null;
  const due = age == null || age >= 30;
  const heroes = [...d.topMovies.slice(0, 3).map(x => ({ ...x, kind: 'movie' })), ...d.topShows.slice(0, 3).map(x => ({ ...x, kind: 'show' }))]
    .filter(x => x.image_url);

  main.innerHTML = `
    <section class="hero" id="hero"><div class="bg" id="heroBg"></div><div class="info" id="heroInfo"></div>
      <div class="dots" id="heroDots">${heroes.map((_, i) => `<i data-i="${i}"></i>`).join('')}</div></section>
    <div class="page" style="padding-top:0">
      <div class="banner ${due && !run.running ? 'due' : ''}">
        <div>${run.running ? '<b>A scrape is running right now.</b> <a href="#runs" style="color:var(--accent-2)">See progress ›</a>'
          : `<b>Last scrape:</b> ${fmtDate(last)} <span class="muted">(${age ?? '–'} days ago)</span>${due ? ' — <b class="warn">time for this month\'s run</b>' : ''}`}</div>
        ${run.running ? '' : '<button class="primary" onclick="startRun(false)">▶ Start run</button>'}
      </div>
      <div class="stats">
        ${[['Movies', num(c.movies)], ['TV shows', num(c.shows)], ['Seasons', num(c.seasons)], ['Episodes', num(c.episodes)],
           ['Avg IMDb · movies', c.avg_movie_rating ?? '–'], ['Avg IMDb · shows', c.avg_show_rating ?? '–']]
          .map(([l, v]) => `<div class="stat"><div class="v">${v}</div><div class="l">${l}</div></div>`).join('')}
      </div>
      ${strip('Top 10 movies by IMDb', d.topMovies.slice(0, 10), 'movie', '#movies', true)}
      ${strip('Top 10 shows by IMDb', d.topShows.slice(0, 10), 'show', '#shows', true)}
      ${strip('Latest releases', d.newest, null, null)}
      <section class="row-block"><div class="charts">
        <div class="panel"><h2>Genres <span class="small">· click to browse movies</span></h2>
          ${bars(d.genres, { onClick: r => location.hash = `movies?genre=${encodeURIComponent(r.name)}` })}</div>
        <div class="panel"><h2>Languages</h2>${bars(d.languages.slice(0, 12), { onClick: r => location.hash = `movies?lang=${encodeURIComponent(r.name)}` })}</div>
        <div class="panel"><h2>IMDb ratings <span class="small">· movies + shows</span></h2>
          ${bars(d.ratings.slice().reverse().map(r => ({ name: `${r.bucket}.0 – ${r.bucket}.9`, n: Number(r.movies) + Number(r.shows), min: r.bucket })),
                 { onClick: r => location.hash = `movies?minRating=${r.min}` })}</div>
        <div class="panel"><h2>Most episodes</h2>
          ${bars(d.biggestShows, { label: r => r.title, value: r => Number(r.episodes), onClick: r => go('show', r.content_id) })}</div>
      </div></section>
    </div>`;
  bindTiles();

  let i = 0;
  const show = n => {
    const h = heroes[n]; if (!h) return;
    $('#heroBg').style.backgroundImage = `url("${h.image_url}")`;
    $('#heroInfo').innerHTML = `
      ${h.title_image_url ? `<img class="tlogo" src="${esc(h.title_image_url)}" alt="${esc(h.title)}" onerror="this.outerHTML='<h1>${esc(h.title).replace(/'/g, '')}</h1>'">` : `<h1>${esc(h.title)}</h1>`}
      ${metaLine([rating(h.imdb_rating), h.release_year, h.age_rating, (h.languages || []).slice(0, 2).join(', '), (h.genres || [])[0]])}
      <p>${esc(h.description ?? '')}</p>
      <button class="primary" onclick="go('${h.kind}','${esc(h.content_id)}')">View details</button>`;
    document.querySelectorAll('#heroDots i').forEach((el, k) => el.classList.toggle('on', k === n));
  };
  document.querySelectorAll('#heroDots i').forEach(el => el.onclick = () => { i = +el.dataset.i; show(i); });
  show(0);
  heroTimer = setInterval(() => { i = (i + 1) % heroes.length; show(i); }, 7000);
}

// ---------- Browse movies / shows ----------
async function browse(kind) {
  const t = token;
  const q = query();
  const filters = await api(`/api/filters?kind=${kind}`);
  if (t !== token) return;
  const shows = kind === 'shows';
  main.innerHTML = `<div class="page">
    <h1>${shows ? 'TV Shows' : 'Movies'}</h1><div class="muted" id="count">&nbsp;</div>
    <div class="toolbar">
      <input type="search" id="q" placeholder="Search by title, actor or director" value="${esc(q.get('q') || '')}">
      <select id="genre"><option value="">All genres</option>${filters.genres.map(g => `<option>${esc(g)}</option>`).join('')}</select>
      <select id="lang"><option value="">All languages</option>${filters.languages.map(g => `<option>${esc(g)}</option>`).join('')}</select>
      <select id="minRating"><option value="">Any rating</option>${[9, 8, 7, 6, 5, 4, 3].map(r => `<option value="${r}">IMDb ${r}+</option>`).join('')}</select>
      <select id="sort"><option value="rating">Highest rated</option><option value="year">Newest release</option>
        <option value="title">A – Z</option><option value="new">Recently added</option>
        ${shows ? '<option value="episodes">Most episodes</option>' : ''}</select>
    </div>
    <div class="grid" id="grid"></div>
    <button class="more" id="more" hidden>Load more</button></div>`;
  const keys = ['genre', 'lang', 'minRating', 'sort'];
  for (const k of keys) if (q.get(k)) $('#' + k).value = q.get(k);

  let page = 1;
  const load = async reset => {
    if (reset) page = 1;
    const qs = new URLSearchParams({ kind, page, size: 48 });
    for (const k of ['q', ...keys]) if ($('#' + k).value) qs.set(k, $('#' + k).value);
    const res = await api('/api/titles?' + qs);
    if (t !== token) return;
    const html = res.items.map(r => tileHtml(r, shows ? 'show' : 'movie')).join('');
    if (reset) $('#grid').innerHTML = html || '<div class="empty">Nothing matches these filters.</div>';
    else $('#grid').insertAdjacentHTML('beforeend', html);
    $('#count').textContent = `${num(res.total)} ${shows ? 'shows' : 'movies'}`;
    $('#more').hidden = page * 48 >= res.total;
    bindTiles();
  };
  let timer;
  $('#q').oninput = () => { clearTimeout(timer); timer = setTimeout(() => load(true), 300); };
  for (const k of keys) $('#' + k).onchange = () => load(true);
  $('#more').onclick = () => { page++; load(false); };
  await load(true);
}

// ---------- Detail pages: image left, everything right ----------
function detailHtml(x, kind, extraFacts) {
  const genres = x.genres || [];
  return `<div class="detail">
    <div class="backdrop" style="background-image:url('${esc(x.image_url ?? '')}')"></div>
    <div class="wrap">
      <a class="back" onclick="history.length > 1 ? history.back() : location.hash='${kind}s'">‹ Back</a>
      <div class="split">
        <div class="pic">${pic(x.image_url, x.title)}</div>
        <div class="facts">
          ${x.title_image_url ? `<img class="tlogo" src="${esc(x.title_image_url)}" alt="" onerror="this.remove()">` : ''}
          <h1>${esc(x.title)}</h1>
          ${metaLine([x.release_year, x.age_rating, ...extraFacts, (x.languages || []).slice(0, 3).join(', ')])}
          ${x.imdb_rating != null ? `<div class="score"><span class="big">★ ${Number(x.imdb_rating).toFixed(1)}</span>
            <span class="lbl">IMDb rating<br>out of 10</span></div>` : ''}
          <div class="chips">${genres.map((g, i) => `<span class="chip ${i < 2 ? 'main' : ''}">${esc(g)}</span>`).join('')}</div>
          <p class="desc">${esc(x.description ?? '')}</p>
          <dl class="kv">
            ${x.cast_members?.length ? `<dt>Cast</dt><dd>${esc(list(x.cast_members))}</dd>` : ''}
            ${x.directors?.length ? `<dt>Director</dt><dd>${esc(list(x.directors))}</dd>` : ''}
            ${x.creators?.length ? `<dt>Creator</dt><dd>${esc(list(x.creators))}</dd>` : ''}
            ${x.producers?.length ? `<dt>Producer</dt><dd>${esc(list(x.producers))}</dd>` : ''}
            <dt>Languages</dt><dd>${esc(list(x.languages)) || '–'}</dd>
            <dt>On Hotstar since</dt><dd>${fmtDay(utc(x.first_seen_at))} <span class="small">(first seen by the scraper)</span></dd>
            <dt>Last updated</dt><dd>${fmtDate(utc(x.last_scraped_at))}</dd>
          </dl>
          <div class="actions"><a class="btn primary" href="${esc(x.url)}" target="_blank" rel="noopener">Open on Hotstar ↗</a></div>
        </div>
      </div>
      <div id="below"></div>
    </div></div>`;
}
function promoTable(p) {
  if (!p.length) return '';
  return `<section class="row-block"><h2>Where Hotstar promoted it</h2><div class="panel" style="padding:6px 10px">
    <table><tr><th>Run</th><th>Row on the home page</th><th class="num">Position</th></tr>
    ${p.map(r => `<tr><td>${fmtDate(utc(r.scraped_at))}</td><td>${esc(r.section_title)} ${r.on_home ? '' : '<span class="tag">View All</span>'}</td>
      <td class="num">#${r.position}</td></tr>`).join('')}</table></div></section>`;
}

async function movieDetail(id) {
  const t = token;
  const d = await api(`/api/movies/${encodeURIComponent(id)}`);
  if (t !== token) return;
  const m = d.movie;
  main.innerHTML = detailHtml(m, 'movie', [m.runtime_minutes ? `${Math.floor(m.runtime_minutes / 60)}h ${m.runtime_minutes % 60}m` : null]);
  $('#below').innerHTML = promoTable(d.promotions);
}

async function showDetail(id) {
  const t = token;
  const d = await api(`/api/shows/${encodeURIComponent(id)}`);
  if (t !== token) return;
  const s = d.show;
  const total = d.seasons.reduce((a, x) => a + Number(x.stored), 0);
  main.innerHTML = detailHtml(s, 'show', [`${d.seasons.length} season${d.seasons.length == 1 ? '' : 's'}`, `${num(total)} episodes`]);
  if (!d.seasons.length) { $('#below').innerHTML = promoTable(d.promotions); return; }
  $('#below').innerHTML = `
    <div class="tabs" id="tabs">${d.seasons.map((x, i) => `<button data-i="${i}">${esc(x.title)}
      <span class="small">${num(x.stored)}${x.announced != null && x.stored < x.announced ? ` / ${num(x.announced)}` : ''}</span></button>`).join('')}</div>
    <div class="eps" id="eps"></div><button class="more" id="moreEps" hidden>Load more episodes</button>
    ${promoTable(d.promotions)}`;
  let season, page;
  const load = async reset => {
    if (reset) page = 1;
    const r = await api(`/api/seasons/${encodeURIComponent(season.season_id)}/episodes?page=${page}`);
    if (t !== token) return;
    const html = r.items.map(e => `<a class="ep" href="${esc(e.url ?? s.url)}" target="_blank" rel="noopener">
      <div class="img">${pic(e.image_url)}</div>
      <div><div class="t">${e.episode_number != null ? `E${e.episode_number} · ` : ''}${esc(e.title)}</div>
        ${metaLine([e.air_date ? fmtDay(e.air_date) : null, e.runtime_minutes ? `${e.runtime_minutes} min` : null])}
        <div class="d">${esc(e.description ?? '')}</div></div></a>`).join('');
    if (reset) $('#eps').innerHTML = html || '<div class="empty">No episodes stored for this season.</div>';
    else $('#eps').insertAdjacentHTML('beforeend', html);
    $('#moreEps').hidden = r.items.length < 100;
  };
  const pick = i => {
    season = d.seasons[i];
    document.querySelectorAll('#tabs button').forEach((b, k) => b.classList.toggle('on', k === i));
    load(true);
  };
  document.querySelectorAll('#tabs button').forEach(b => b.onclick = () => pick(+b.dataset.i));
  $('#moreEps').onclick = () => { page++; load(false); };
  pick(d.seasons.length - 1); // newest season first
}

// ---------- Home page (what Hotstar promoted) ----------
async function home() {
  const t = token;
  const run = query().get('run');
  const d = await api('/api/promotions' + (run ? `?run=${encodeURIComponent(run)}` : ''));
  if (t !== token) return;
  main.innerHTML = `<div class="page">
    <div style="display:flex;justify-content:space-between;align-items:flex-end;gap:14px;flex-wrap:wrap">
      <div><h1>Home Page</h1><div class="muted">Hotstar's home page as the scraper saw it, row by row</div></div>
      <select id="run">${d.runs.map(r => `<option value="${esc(r.run_id)}" ${r.run_id === d.run ? 'selected' : ''}>
        ${fmtDate(utc(r.scraped_at))} · ${r.cards} titles</option>`).join('')}</select></div>
    ${d.sections.map(s => `<section class="row-block"><div class="row-head"><h2>${esc(s.title)}</h2>
      <span class="small">${s.cards.length} titles</span></div>
      <div class="strip">${s.cards.map(c => {
        const kind = c.movie_id ? 'movie' : c.show_id ? 'show' : 'link';
        const id = c.movie_id || c.show_id || c.content_id || '';
        return `<div class="tile" data-kind="${kind}" data-id="${esc(id)}" data-url="${esc(c.url ?? '')}">
          <div class="img">${pic(c.image_url, c.title)}${c.imdb_rating != null ? `<span class="badge">★ ${Number(c.imdb_rating).toFixed(1)}</span>` : ''}</div>
          <div class="cap"><div class="t" title="${esc(c.title)}">${esc(c.title)}</div>
            <div class="s">#${c.position} · ${esc(c.content_type)}${c.on_home ? '' : ' · View All'}</div></div></div>`;
      }).join('')}</div></section>`).join('')}</div>`;
  $('#run').onchange = e => location.hash = `home?run=${encodeURIComponent(e.target.value)}`;
  bindTiles();
}

// ---------- Runs ----------
async function runs() {
  const t = token;
  const history = await api('/api/runs');
  if (t !== token) return;
  main.innerHTML = `<div class="page">
    <h1>Runs</h1>
    <div class="muted">The scraper runs on this laptop. Keep it on and online until the run finishes.</div>
    <div class="panel" style="margin-top:22px" id="runPanel"><div class="skeleton" style="height:80px"></div></div>
    <section class="row-block"><h2>Past runs</h2>
      ${history.length ? history.map((r, i) => `<details class="run" ${i === 0 ? 'open' : ''}>
        <summary><b>${esc(runName(r.folder))}</b>${qualitySummary(r.quality)}</summary><pre class="log">${esc(r.quality)}</pre></details>`).join('')
        : '<div class="empty">No finished runs yet.</div>'}
    </section></div>`;
  renderRunPanel(status);
}
const runName = f => { const m = f.match(/^(\d{4})(\d\d)(\d\d)-(\d\d)(\d\d)/); return m ? fmtDate(new Date(+m[1], m[2] - 1, +m[3], +m[4], +m[5])) : f; };
function qualitySummary(q) {
  const failed = Number((q.match(/Titles that failed: (\d+)/) || [])[1] || 0);
  const counts = (q.match(/Database now holds: (.*)/) || [])[1];
  return `<span class="small">${failed ? `<span class="warn">${failed} failed</span>` : '<span class="ok">no failures</span>'}${counts ? ' · ' + esc(counts) : ''}</span>`;
}
function renderRunPanel(s) {
  const el = $('#runPanel');
  if (!el || !s) return;
  const pct = s.total ? Math.round(s.done / s.total * 100) : 0;
  const state = s.running ? `<h2 style="margin:0">Scraping… <span class="small">${s.full ? 'full run' : 'normal run'} · started ${fmtDate(s.startedAt)}</span></h2>`
    : s.endedAt ? `<h2 style="margin:0">${s.exitCode === 0 ? '<span class="ok">✓ Finished — data updated</span>' : `<span class="bad">Stopped (exit code ${s.exitCode})</span>`}
        <span class="small">${fmtDate(s.endedAt)}</span></h2>`
    : `<h2 style="margin:0">Ready</h2>`;
  el.innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap">${state}
      <div class="actions" style="margin:0">${s.running ? '<button class="danger" onclick="stopRun()">■ Stop</button>'
        : `<button class="primary" onclick="startRun(false)">▶ Start run</button>
           <button onclick="startRun(true)" title="Re-scrape every title and every episode">Full run</button>`}</div></div>
    ${s.running ? `<div class="progress"><div style="width:${s.total ? pct : 3}%"></div></div>
      <div class="small">${s.total ? `Title ${num(s.done)} of ${num(s.total)} · ${pct}%` : 'Reading the home page and every View All row (about 6 minutes)…'}</div>` : ''}
    ${s.log?.length ? `<pre class="log">${esc(s.log.join('\n'))}</pre>` : ''}
    ${s.running ? '' : `<p class="small" style="margin:14px 0 0"><b>Start run</b> — fresh home page, new episodes of every show, and movies not updated for 7 days.
      <b>Full run</b> — everything again (about 3 hours).</p>`}`;
  const log = el.querySelector('pre.log');
  if (log) log.scrollTop = log.scrollHeight;
}

// ---------- Run status: rail card + polling ----------
let status = null, poll, wasRunning = false;
async function refreshStatus() {
  try { status = await api('/api/run/status'); } catch { poll = setTimeout(refreshStatus, 10000); return; }
  const s = status;
  $('#runDot').hidden = !s.running;
  $('#railRun').innerHTML = s.running
    ? `<b>Scrape running</b>${s.total ? `Title ${num(s.done)} of ${num(s.total)}` : 'Reading home page…'}<button onclick="location.hash='runs'">View progress</button>`
    : `<b>Scraper ready</b>Run it about once a month<button class="primary" onclick="startRun(false)">▶ Start run</button>`;
  renderRunPanel(s);
  if (wasRunning && !s.running) {
    toast(s.exitCode === 0 ? '<b>Scrape finished.</b> The dashboard now shows the new data.' : `<b>Scrape stopped</b> (exit code ${s.exitCode}). See Runs for the log.`, 9000);
    if (!location.hash.startsWith('#runs')) route(); else runs();
  }
  wasRunning = s.running;
  clearTimeout(poll);
  poll = setTimeout(refreshStatus, s.running ? 3000 : 20000);
}
async function startRun(full) {
  if (full && !confirm('A full run re-scrapes every title and every episode. It takes about 3 hours. Start it?')) return;
  try {
    await api(`/api/run/start?full=${full}`, { method: 'POST' });
    toast('<b>Scraping started.</b> You can keep using the dashboard; it refreshes when the run finishes.');
    location.hash = 'runs';
    refreshStatus();
  } catch (e) { toast(`<b>Could not start:</b> ${esc(e.message)}`); }
}
async function stopRun() {
  if (!confirm('Stop the scrape? Everything finished so far stays saved.')) return;
  await api('/api/run/stop', { method: 'POST' });
  refreshStatus();
}

route();
refreshStatus();
