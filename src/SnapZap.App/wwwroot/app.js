'use strict';

// ---- State -------------------------------------------------------------
const state = {
  images: [],                 // all analyzed images
  byId: new Map(),            // id -> image
  dupeOf: new Map(),          // imageId -> {groupId, kind, isKeeper}
  selected: new Set(),        // selected image ids
  visibleIds: [],             // ids currently rendered (filter order) — for range select
  lastClickedId: null,
};

const $ = sel => document.querySelector(sel);
const el = (tag, cls) => { const e = document.createElement(tag); if (cls) e.className = cls; return e; };

// ---- Data loading ------------------------------------------------------
async function loadAll() {
  const [images, groups] = await Promise.all([
    fetch('/api/images').then(r => r.json()),
    fetch('/api/dupe-groups').then(r => r.json()),
  ]);
  state.images = images;
  state.byId = new Map(images.map(i => [i.id, i]));
  state.dupeOf = new Map();
  for (const g of groups)
    for (const m of g.members)
      state.dupeOf.set(m.imageId, { groupId: g.id, kind: g.kind, isKeeper: m.isKeeper });

  populateFacets();
  render();
}

function folderOf(path) {
  const norm = path.replace(/\\/g, '/');
  const i = norm.lastIndexOf('/');
  return i < 0 ? '' : norm.slice(0, i);
}
function yearOf(img) {
  if (!img.exifTaken) return null;
  return new Date(img.exifTaken * 1000).getUTCFullYear();
}

function populateFacets() {
  const folders = [...new Set(state.images.map(i => folderOf(i.path)))].sort();
  const years = [...new Set(state.images.map(yearOf).filter(Boolean))].sort();
  const ff = $('#folderFilter'), yf = $('#yearFilter');
  ff.innerHTML = '<option value="">All folders</option>' +
    folders.map(f => `<option value="${f}">${shortFolder(f)}</option>`).join('');
  yf.innerHTML = '<option value="">All years</option>' +
    years.map(y => `<option value="${y}">${y}</option>`).join('');
}
function shortFolder(f) { const p = f.split('/'); return p.slice(-2).join('/') || f; }

// ---- Filtering ---------------------------------------------------------
function currentFilters() {
  return {
    nsfw: parseFloat($('#nsfwFilter').value),
    blur: parseInt($('#blurFilter').value, 10),
    dupesOnly: $('#dupesOnly').checked,
    folder: $('#folderFilter').value,
    year: $('#yearFilter').value,
  };
}
function matches(img, f) {
  if (f.nsfw > 0 && !(img.nsfwScore != null && img.nsfwScore >= f.nsfw)) return false;
  // Blur filter: show images blurrier than the threshold (lower score = blurrier).
  if (f.blur > 0 && !(img.blurScore != null && img.blurScore <= f.blur)) return false;
  if (f.dupesOnly && !state.dupeOf.has(img.id)) return false;
  if (f.folder && folderOf(img.path) !== f.folder) return false;
  if (f.year && String(yearOf(img)) !== f.year) return false;
  return true;
}
function filteredImages() {
  const f = currentFilters();
  return state.images.filter(i => matches(i, f));
}

// ---- Rendering ---------------------------------------------------------
function render() {
  const grid = $('#grid');
  const imgs = filteredImages();
  state.visibleIds = imgs.map(i => i.id);

  $('#empty').style.display = state.images.length === 0 ? 'grid' : 'none';
  $('#summary').textContent = state.images.length === 0 ? '' :
    `${imgs.length} shown of ${state.images.length} images · ${state.dupeOf.size} in duplicate groups · ${state.selected.size} selected`;

  grid.innerHTML = '';
  const frag = document.createDocumentFragment();
  for (const img of imgs) frag.appendChild(card(img));
  grid.appendChild(frag);
  updateSelCount();
}

function card(img) {
  const c = el('div', 'card');
  c.dataset.id = img.id;
  if (state.selected.has(img.id)) c.classList.add('selected');

  const im = el('img');
  im.loading = 'lazy';
  im.src = img.thumbUrl;
  im.alt = img.path;
  c.appendChild(im);

  const check = el('div', 'check');
  check.textContent = state.selected.has(img.id) ? '✓' : '';
  c.appendChild(check);

  const badges = el('div', 'badges');
  if (img.nsfwScore != null) {
    const b = el('span', 'badge nsfw' + (img.nsfwScore < 0.5 ? ' low' : ''));
    b.textContent = 'NSFW ' + img.nsfwScore.toFixed(2);
    badges.appendChild(b);
  }
  const d = state.dupeOf.get(img.id);
  if (d) {
    const b = el('span', 'badge ' + (d.kind === 'similar' ? 'similar' : 'dupe'));
    b.textContent = (d.kind === 'similar' ? '≈ ' : '⧉ ') + (d.isKeeper ? 'keep' : 'dup');
    badges.appendChild(b);
  }
  if (badges.children.length) c.appendChild(badges);

  c.addEventListener('click', e => onCardClick(e, img.id));
  c.addEventListener('dblclick', () => openModal(img));
  return c;
}

// ---- Selection ---------------------------------------------------------
function onCardClick(e, id) {
  if (e.shiftKey && state.lastClickedId != null) {
    const a = state.visibleIds.indexOf(state.lastClickedId);
    const b = state.visibleIds.indexOf(id);
    if (a >= 0 && b >= 0) {
      const [lo, hi] = a < b ? [a, b] : [b, a];
      for (let i = lo; i <= hi; i++) state.selected.add(state.visibleIds[i]);
    }
  } else {
    toggle(id);
  }
  state.lastClickedId = id;
  render();
}
function toggle(id) { state.selected.has(id) ? state.selected.delete(id) : state.selected.add(id); }

function selectAllVisible() { for (const id of state.visibleIds) state.selected.add(id); render(); }
function selectVisibleFolder() {
  const f = $('#folderFilter').value;
  const target = f || (state.visibleIds.length ? folderOf(state.byId.get(state.visibleIds[0]).path) : '');
  for (const id of state.visibleIds)
    if (folderOf(state.byId.get(id).path) === target) state.selected.add(id);
  render();
}
function selectFiltered() { for (const id of state.visibleIds) state.selected.add(id); render(); }

// Select every non-keeper in duplicate groups (the "delete extras, keep best" one-click).
function selectDupeExtras() {
  for (const [id, d] of state.dupeOf) if (!d.isKeeper) state.selected.add(id);
  render();
}
function clearSelection() { state.selected.clear(); state.lastClickedId = null; render(); }

function updateSelCount() {
  $('#selCount').textContent = `${state.selected.size} selected`;
  $('#exportBtn').disabled = state.selected.size === 0;
  $('#deleteBtn').disabled = state.selected.size === 0;
}

// ---- Delete + Undo -----------------------------------------------------
// Silently re-scan the current folder and reload (used after a restore re-adds files to disk).
function rescanCurrent() {
  const folder = $('#folder').value.trim();
  if (!folder) { loadAll(); return Promise.resolve(); }
  return new Promise(resolve => {
    const es = new EventSource(`/api/scan?folder=${encodeURIComponent(folder)}`);
    es.addEventListener('done', () => { es.close(); loadAll(); resolve(); });
    es.addEventListener('error', () => { es.close(); loadAll(); resolve(); });
  });
}

async function deleteSelected() {
  if (state.selected.size === 0) return;
  const n = state.selected.size;
  if (!confirm(`Move ${n} image${n>1?'s':''} to the Recycle Bin? This is reversible from Undo history.`)) return;
  setStatus(`Recycling ${n}…`);
  // /api/delete returns via Results.Ok → camelCase (unlike the manual-serialized SSE events).
  const r = await fetch('/api/delete', {
    method: 'POST', headers: {'Content-Type':'application/json'},
    body: JSON.stringify({ imageIds: [...state.selected] }),
  }).then(r => r.json());
  setStatus(`Recycled ${r.recycled}${r.failed ? `, ${r.failed} failed` : ''}`);
  state.selected.clear();
  loadAll();
}

async function openUndo() {
  const batches = await fetch('/api/undo/batches').then(r => r.json());
  const list = $('#undoList');
  list.innerHTML = batches.length === 0 ? '<p class="dlg-sub">Nothing deleted yet.</p>' : '';
  for (const b of batches) {
    const row = el('div', 'undo-row' + (b.restored >= b.total ? ' done' : ''));
    const when = new Date(b.ts * 1000).toLocaleString();
    const kind = b.batchId.startsWith('export') ? 'export-recycle' : 'delete';
    row.innerHTML = `<div><b>${kind}</b> · ${b.total} item${b.total>1?'s':''}<div class="meta">${when}${b.restored?` · ${b.restored} restored`:''}</div></div>`;
    const btn = el('button');
    btn.textContent = b.restored >= b.total ? 'Restored' : 'Restore';
    btn.disabled = b.restored >= b.total;
    btn.addEventListener('click', async () => {
      btn.disabled = true; btn.textContent = 'Restoring…';
      const res = await fetch('/api/undo/' + encodeURIComponent(b.batchId), { method: 'POST' }).then(r => r.json());
      setStatus(`Restored ${res.restored}${res.missing?`, ${res.missing} missing`:''}`);
      // Restored files are back on disk but were removed from the catalog on delete —
      // re-scan the folder so they reappear in the grid with fresh signals.
      await rescanCurrent();
      openUndo();
    });
    row.appendChild(btn);
    list.appendChild(row);
  }
  $('#undoDlg').classList.remove('hidden');
}

// ---- Modal preview -----------------------------------------------------
function openModal(img) {
  $('#modalImg').src = img.thumbUrl; // full-res endpoint can replace this later
  const d = state.dupeOf.get(img.id);
  const bits = [
    img.path,
    `${img.width}×${img.height} · ${(img.fileSize/1024).toFixed(0)} KB`,
    img.nsfwScore != null ? `NSFW ${img.nsfwScore.toFixed(3)}` : null,
    img.blurScore != null ? `blur ${img.blurScore.toFixed(1)}` : null,
    img.exifTaken ? new Date(img.exifTaken*1000).toISOString().slice(0,10) : null,
    img.exifCamera || null,
    d ? `${d.kind} group #${d.groupId} (${d.isKeeper ? 'keeper' : 'duplicate'})` : null,
  ].filter(Boolean);
  $('#modalInfo').innerHTML = bits.join(' &nbsp;·&nbsp; ');
  $('#modal').classList.remove('hidden');
}

// ---- Scans (SSE) -------------------------------------------------------
function runSse(url, label, onDone) {
  setStatus(`${label}…`);
  const es = new EventSource(url);
  es.addEventListener('progress', e => {
    const p = JSON.parse(e.data);
    if (p.total != null) setStatus(`${label}: ${p.seen ?? p.done}/${p.total}`);
  });
  es.addEventListener('done', e => { es.close(); setStatus(''); onDone(JSON.parse(e.data)); });
  es.addEventListener('error', () => { es.close(); setStatus(`${label} failed`); });
}
function setStatus(t) { $('#status').textContent = t; }

// ---- Export ------------------------------------------------------------
function unselectedIds() {
  return state.images.map(i => i.id).filter(id => !state.selected.has(id));
}
function exportDto() {
  return {
    destination: $('#expDest').value.trim(),
    mode: $('#expMode').value,
    structure: $('#expStructure').value,
    rejectAction: $('#expRecycle').checked ? 'recycle' : 'leave',
    keeperIds: [...state.selected],
    rejectIds: $('#expRecycle').checked ? unselectedIds() : [],
  };
}
function openExport() {
  if (state.selected.size === 0) return;
  $('#expCount').textContent = state.selected.size;
  $('#expRejectCount').textContent = unselectedIds().length;
  $('#preflight').classList.remove('show');
  $('#expResult').textContent = '';
  $('#expRun').disabled = true;
  const saved = localStorage.getItem('pc_dest'); if (saved) $('#expDest').value = saved;
  $('#exportDlg').classList.remove('hidden');
}
function closeExport() { $('#exportDlg').classList.add('hidden'); }

function fmtBytes(b) {
  if (b >= 1e9) return (b/1e9).toFixed(1) + ' GB';
  if (b >= 1e6) return (b/1e6).toFixed(1) + ' MB';
  return (b/1e3).toFixed(0) + ' KB';
}
async function preflight() {
  const dto = exportDto();
  if (!dto.destination) { $('#preflight').textContent = 'Enter a destination.'; $('#preflight').classList.add('show'); return; }
  const p = await fetch('/api/export/preflight', {
    method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(dto),
  }).then(r => r.json());
  $('#expRejectCount').textContent = unselectedIds().length;
  const space = p.enoughSpace
    ? `<span class="ok">enough free space</span>`
    : `<span class="warn">not enough free space!</span>`;
  const link = p.hardlinkPossible ? ' · hardlink available (zero extra space)' : '';
  $('#preflight').innerHTML =
    `${p.keeperCount} images · ${fmtBytes(p.totalBytes)} · destination free ${fmtBytes(p.destinationFreeBytes)} · ${space}${link}`;
  $('#preflight').classList.add('show');
  $('#expRun').disabled = !p.enoughSpace;
}
function runExport() {
  const dto = exportDto();
  localStorage.setItem('pc_dest', dto.destination);
  $('#expRun').disabled = true;
  $('#expResult').textContent = 'Exporting…';
  fetchSse('/api/export/run', dto); // POST SSE via fetch streaming (EventSource is GET-only)
}
// POST-based SSE via fetch streaming (EventSource is GET-only).
async function fetchSse(url, body) {
  const res = await fetch(url, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(body) });
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const chunk = buf.slice(0, idx); buf = buf.slice(idx + 2);
      const ev = /event: (\w+)/.exec(chunk)?.[1];
      const dataLine = /data: (.*)/s.exec(chunk)?.[1];
      if (!dataLine) continue;
      const data = JSON.parse(dataLine);
      if (ev === 'progress') $('#expResult').textContent = `${data.phase}: ${data.done}/${data.total} ${data.current ?? ''}`;
      else if (ev === 'done') onExportDone(data);
      else if (ev === 'error') $('#expResult').innerHTML = `<span class="bad">Error: ${data.message}</span>`;
    }
  }
}
function onExportDone(data) {
  const r = data.result;
  $('#expResult').innerHTML =
    `<span class="good">Exported ${r.exported}</span> · skipped ${r.skippedExisting} · failed ${r.failed}` +
    (r.recycled ? ` · recycled ${r.recycled}` : '') +
    `\nManifest: ${data.manifest.csv}`;
  loadAll();
}

// ---- Wiring ------------------------------------------------------------
function init() {
  $('#scanBtn').addEventListener('click', () => {
    const folder = $('#folder').value.trim();
    if (!folder) return;
    localStorage.setItem('pc_folder', folder);
    runSse(`/api/scan?folder=${encodeURIComponent(folder)}`, 'Scanning',
      r => { setStatus(`Scanned ${r.analyzed} new, ${r.cached} cached, ${r.failed} failed`); loadAll(); });
  });
  $('#dedupBtn').addEventListener('click', async () => {
    const folder = $('#folder').value.trim(); if (!folder) return;
    setStatus('Finding duplicates…');
    const r = await fetch(`/api/dedup?folder=${encodeURIComponent(folder)}`, { method: 'POST' }).then(r => r.json());
    setStatus(`${r.exactGroups} exact${r.czkawkaAvailable ? `, ${r.similarGroups} similar` : ' (czkawka absent)'}`);
    loadAll();
  });
  $('#nsfwBtn').addEventListener('click', () =>
    runSse('/api/nsfw-scan', 'Scoring NSFW', r => {
      setStatus(r.modelAvailable ? `Scored ${r.scored}, ${r.failed} failed` : 'NSFW model not installed');
      loadAll();
    }));

  $('#nsfwFilter').addEventListener('input', e => { $('#nsfwOut').textContent = (+e.target.value).toFixed(2); render(); });
  $('#blurFilter').addEventListener('input', e => { $('#blurOut').textContent = e.target.value === '0' ? 'off' : e.target.value; render(); });
  for (const id of ['#dupesOnly', '#folderFilter', '#yearFilter']) $(id).addEventListener('change', render);
  $('#clearFilters').addEventListener('click', () => {
    $('#nsfwFilter').value = 0; $('#nsfwOut').textContent = '0.00';
    $('#blurFilter').value = 0; $('#blurOut').textContent = 'off';
    $('#dupesOnly').checked = false; $('#folderFilter').value = ''; $('#yearFilter').value = '';
    render();
  });

  $('#selAll').addEventListener('click', selectAllVisible);
  $('#selFolder').addEventListener('click', selectVisibleFolder);
  $('#selFiltered').addEventListener('click', selectFiltered);
  $('#selDupeExtras').addEventListener('click', selectDupeExtras);
  $('#selNone').addEventListener('click', clearSelection);

  $('#exportBtn').addEventListener('click', openExport);
  $('#deleteBtn').addEventListener('click', deleteSelected);
  $('#undoBtn').addEventListener('click', openUndo);
  $('#undoClose').addEventListener('click', () => $('#undoDlg').classList.add('hidden'));
  $('#expCancel').addEventListener('click', closeExport);
  $('#expPreflight').addEventListener('click', preflight);
  $('#expRun').addEventListener('click', runExport);
  $('#expRecycle').addEventListener('change', () => { $('#expRejectCount').textContent = unselectedIds().length; });

  $('#modalClose').addEventListener('click', () => $('#modal').classList.add('hidden'));
  $('#modal').addEventListener('click', e => { if (e.target.id === 'modal') $('#modal').classList.add('hidden'); });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') $('#modal').classList.add('hidden');
    if (e.key === 'a' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); selectAllVisible(); }
  });

  const saved = localStorage.getItem('pc_folder'); if (saved) $('#folder').value = saved;
  loadAll();
}
document.addEventListener('DOMContentLoaded', init);
