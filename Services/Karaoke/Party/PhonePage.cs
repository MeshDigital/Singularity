namespace Singularity.Services.Karaoke.Party;

/// <summary>
/// The page a guest's phone opens from the QR code: their name (remembered), a search over the songs with covers,
/// "Sing this" to join the queue, and who's up next. One self-contained file; all song text goes in through
/// textContent, never as HTML.
/// </summary>
internal static class PhonePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f1117">
<title>Singularity karaoke</title>
<style>
  :root { --bg:#0f1117; --card:#1a1d27; --line:#2a2f3d; --text:#eef0f6; --dim:#9aa0b0; --accent:#b48cf0; --gold:#ffd23c; --ok:#6c8; --bad:#f77; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:16px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
  header { padding:18px 16px 8px; }
  h1 { margin:0; font-size:24px; }
  h1 span { color:var(--accent); }
  .sub { color:var(--dim); font-size:14px; margin-top:2px; }
  main { padding:0 16px 120px; max-width:640px; margin:0 auto; }
  label { display:block; color:var(--dim); font-size:13px; margin:14px 0 6px; }
  input { width:100%; padding:13px 14px; border-radius:12px; border:1px solid var(--line); background:var(--card); color:var(--text); font-size:17px; }
  input:focus { outline:2px solid var(--accent); border-color:transparent; }
  h2 { font-size:17px; margin:22px 0 8px; }
  .queue li, .song { list-style:none; display:flex; align-items:center; gap:12px; padding:10px 12px; background:var(--card); border-radius:12px; margin-bottom:8px; }
  ol.queue { padding:0; margin:0; }
  .place { color:var(--gold); font-weight:700; min-width:22px; }
  .who { font-weight:600; }
  .what { color:var(--dim); font-size:14px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .song img, .song .noimg { width:52px; height:52px; border-radius:8px; object-fit:cover; background:#262a36; flex:none; }
  .song .text { flex:1; min-width:0; }
  .song .title { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .tags { color:var(--dim); font-size:12px; }
  button { border:0; border-radius:10px; padding:10px 14px; font-size:15px; font-weight:600; background:var(--accent); color:#160d24; flex:none; }
  button:disabled { opacity:.5; }
  .empty { color:var(--dim); font-size:14px; padding:6px 2px; }
  #toast { position:fixed; left:16px; right:16px; bottom:max(16px, env(safe-area-inset-bottom)); padding:14px 16px; border-radius:14px;
           background:#232838; border:1px solid var(--line); font-weight:600; display:none; max-width:608px; margin:0 auto; }
  #toast.ok { border-color:var(--ok); } #toast.bad { border-color:var(--bad); }
</style>
</head>
<body>
<header>
  <h1>Singularity <span>karaoke</span></h1>
  <div class="sub">Pick a song and join the queue.</div>
</header>
<main>
  <label for="name">Your name</label>
  <input id="name" maxlength="24" autocomplete="nickname" placeholder="Name shown on the screen">

  <h2>Up next</h2>
  <ol class="queue" id="queue"></ol>
  <div class="empty" id="queueEmpty">Nobody yet. Be the first!</div>

  <label for="q">Find a song</label>
  <input id="q" type="search" placeholder="Artist or title" autocomplete="off">
  <div id="results"></div>
</main>
<div id="toast"></div>
<script>
(() => {
  const params = new URLSearchParams(location.search);
  if (params.get('k')) { try { localStorage.setItem('key', params.get('k')); } catch (e) {} }
  let key = params.get('k');
  if (!key) { try { key = localStorage.getItem('key'); } catch (e) {} }
  const name = document.getElementById('name');
  try { name.value = localStorage.getItem('name') || ''; } catch (e) {}
  name.addEventListener('input', () => { try { localStorage.setItem('name', name.value); } catch (e) {} });

  const api = (path, options = {}) => fetch(path, { ...options, headers: { 'X-Key': key || '', 'Content-Type': 'application/json' } })
    .then(r => r.json().then(body => ({ ok: r.ok, body })));

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

  let toastTimer;
  const toast = (text, ok) => {
    const t = document.getElementById('toast');
    t.textContent = text; t.className = ok ? 'ok' : 'bad'; t.style.display = 'block';
    clearTimeout(toastTimer); toastTimer = setTimeout(() => t.style.display = 'none', 4000);
  };

  const showQueue = items => {
    const list = document.getElementById('queue');
    list.replaceChildren(...items.map(q => {
      const li = el('li');
      li.append(el('span', 'place', String(q.place)));
      const text = el('div'); text.style.minWidth = '0';
      text.append(el('div', 'who', q.singer), el('div', 'what', q.title + ' · ' + q.artist));
      li.append(text);
      return li;
    }));
    document.getElementById('queueEmpty').style.display = items.length ? 'none' : 'block';
  };
  const loadQueue = () => api('/api/queue').then(r => { if (r.ok) showQueue(r.body); else toast(r.body.error || 'Scan the QR code again.', false); }).catch(() => {});

  const showSongs = songs => {
    const box = document.getElementById('results');
    if (!songs.length) { box.replaceChildren(el('div', 'empty', 'No songs found.')); return; }
    box.replaceChildren(...songs.map(s => {
      const row = el('div', 'song');
      if (s.cover) { const img = el('img'); img.loading = 'lazy'; img.alt = ''; img.src = '/api/cover/' + s.id + '?k=' + encodeURIComponent(key || ''); row.append(img); }
      else row.append(el('div', 'noimg'));
      const text = el('div', 'text');
      const tags = [s.duet ? 'Duet' : '', s.video ? 'Video' : ''].filter(Boolean).join(' · ');
      text.append(el('div', 'title', s.title), el('div', 'what', s.artist));
      if (tags) text.append(el('div', 'tags', tags));
      const button = el('button', null, 'Sing this');
      button.addEventListener('click', () => {
        if (!name.value.trim()) { toast('Type your name first.', false); name.focus(); return; }
        button.disabled = true;
        api('/api/queue', { method: 'POST', body: JSON.stringify({ id: s.id, singer: name.value }) })
          .then(r => { toast(r.body.message || r.body.error, r.body.ok); loadQueue(); })
          .catch(() => toast('No connection to the karaoke computer.', false))
          .finally(() => button.disabled = false);
      });
      row.append(text, button);
      return row;
    }));
  };
  let searchTimer;
  const search = () => api('/api/songs?q=' + encodeURIComponent(document.getElementById('q').value)).then(r => { if (r.ok) showSongs(r.body); }).catch(() => {});
  document.getElementById('q').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(search, 250); });

  if (!key) toast('Scan the QR code on the screen to join.', false);
  search(); loadQueue(); setInterval(loadQueue, 5000);
})();
</script>
</body>
</html>
""";
}
