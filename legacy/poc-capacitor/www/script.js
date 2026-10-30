(() => {
  'use strict';

  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => document.querySelectorAll(sel);

  /* ---------- État ---------- */
  let library = { artists: [] };
  let albums = [];
  const trackMap = new Map();

  let queue = [];
  let playOrder = [];
  let qPos = -1;
  let shuffleOn = false;
  let repeatMode = 0; // 0 off, 1 tout, 2 un

  let currentAlbum = null;
  let favorites = new Set(JSON.parse(localStorage.getItem('loc-favs') || '[]'));

  const audio = $('#audio');
  const homeAlbums = $('#homeAlbums');
  const artistRow = $('#artistRow');
  const albumContent = $('#albumContent');
  const searchResults = $('#searchResults');
  const libList = $('#libList');

  /* ---------- Utilitaires ---------- */
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));

  function fmt(sec) {
    if (!isFinite(sec) || sec == null) return '--:--';
    sec = Math.floor(sec);
    const m = Math.floor(sec / 60);
    return m + ':' + String(sec % 60).padStart(2, '0');
  }

  function colorsFor(str) {
    let h = 0;
    for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
    const h1 = h % 360;
    const h2 = (h1 + 40 + (h >> 3) % 60) % 360;
    return `linear-gradient(135deg, hsl(${h1},55%,40%), hsl(${h2},60%,25%))`;
  }

  /* ---------- Serveur ---------- */
  function serverBase() {
    const saved = localStorage.getItem('loc-server');
    if (saved) return saved.replace(/\/+$/, '');
    if (window.SONNUAGE_SERVER) return window.SONNUAGE_SERVER.replace(/\/+$/, '');
    return window.location.origin;
  }

  function compressEnabled() {
    return localStorage.getItem('loc-compress') === '1';
  }

  function trackSrc(t) {
    let url = t.url;
    if (compressEnabled() && t.format === 'wav') url += (url.includes('?') ? '&' : '?') + 'c=1';
    return url;
  }

  function coverHTML(album, extra) {
    if (album.art) return `<div class="cover ${extra}"><img src="${album.art}" alt=""></div>`;
    const initials = (album.title || '?').trim().slice(0, 2).toUpperCase();
    return `<div class="cover ${extra}" style="background:${colorsFor(album.artist + album.title)}">${esc(initials)}</div>`;
  }

  /* ---------- Navigation ---------- */
  const navButtons = $$('.nav-item, .b-nav-item');
  let viewStack = ['home'];

  function switchView(view) {
    $$('.view').forEach((v) => v.classList.add('hidden'));
    const target = $('#view-' + view);
    if (target) target.classList.remove('hidden');
    navButtons.forEach((b) => b.classList.toggle('active', b.dataset.view === view));
    $('#backBtn').style.opacity = view === 'home' ? '0.4' : '1';
    $('#fwdBtn').style.opacity = '0.4';
    if (view !== 'search') {
      const q = $('#searchInput').value;
      if (q) { $('#searchInput').value = ''; doSearch(''); }
    }
  }

  navButtons.forEach((btn) => {
    btn.addEventListener('click', () => switchView(btn.dataset.view));
  });

  $('#backBtn').addEventListener('click', () => {
    if ($('#view-album').classList.contains('hidden') === false) {
      switchView(viewStack.length > 1 ? viewStack[viewStack.length - 2] : 'home');
    } else {
      switchView('home');
    }
    if (viewStack.length > 1) viewStack.pop();
  });

  /* ---------- Chargement de la bibliothèque ---------- */
  async function loadLibrary() {
    try {
      const base = serverBase();
      const res = await fetch(base + '/api/library');
      if (!res.ok) throw new Error('HTTP ' + res.status);
      library = await res.json();
      library.artists.forEach((a) => {
        a.albums.forEach((al) => {
          if (al.art) al.art = base + al.art;
          al.tracks.forEach((t) => { t.url = base + t.url; });
        });
      });
    } catch (e) {
      homeAlbums.innerHTML = `<p class="result-msg">Impossible de charger la bibliothèque depuis
        <b>${esc(serverBase())}</b>.<br>Vérifie le serveur, puis</p>
        <button class="primary-btn" id="retryBtn" style="margin-top:12px">&#9881; Réglages</button>`;
      $('#retryBtn').addEventListener('click', () => openSettings());
      return;
    }
    albums = [];
    library.artists.forEach((a) => {
      a.albums.forEach((al) => {
        const album = { artist: a.name, title: al.title, art: al.art, tracks: al.tracks };
        albums.push(album);
        al.tracks.forEach((t) => trackMap.set(t.id, { t, album, artist: a.name }));
      });
    });
    renderArtists();
    renderAlbums(albums);
    renderLibrary();
    saveFavorites();
  }

  /* ---------- Accueil ---------- */
  let activeArtistFilter = null;

  function renderArtists() {
    artistRow.innerHTML = '';
    library.artists.forEach((a) => {
      const first = a.albums[0] || {};
      const card = document.createElement('button');
      card.className = 'artist-card';
      card.innerHTML = coverHTML({ ...first, artist: a.name }, 'artist-cover') +
        `<div class="artist-card-body"><p class="card-title">${esc(a.name)}</p>
         <p class="card-sub">${a.albums.length} album(s)</p></div>`;
      card.addEventListener('click', () => filterByArtist(a.name));
      artistRow.appendChild(card);
    });
  }

  function filterByArtist(name) {
    activeArtistFilter = name;
    $('#albumsTitle').textContent = name;
    renderAlbums(albums.filter((al) => al.artist === name));
    $('#clearFilter').classList.remove('hidden');
    $('#clearFilter').scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  $('#clearFilter').addEventListener('click', () => {
    activeArtistFilter = null;
    $('#albumsTitle').textContent = 'Albums';
    $('#clearFilter').classList.add('hidden');
    renderAlbums(albums);
  });

  function renderAlbums(list) {
    homeAlbums.innerHTML = '';
    list.forEach((al) => {
      const card = document.createElement('div');
      card.className = 'card';
      card.innerHTML = coverHTML(al, '') + `
        <div class="card-body">
          <p class="card-title">${esc(al.title)}</p>
          <p class="card-sub">${esc(al.artist)} • ${al.tracks.length} titres</p>
        </div>`;
      card.addEventListener('click', () => openAlbum(al));
      homeAlbums.appendChild(card);
    });
  }

  /* ---------- Vue album ---------- */
  function openAlbum(album) {
    currentAlbum = album;
    viewStack.push('album');
    const total = album.tracks.reduce((s, t) => s + (t.duration || 0), 0);
    albumContent.innerHTML = `
      <div class="album-hero">
        ${coverHTML(album, 'hero-cover')}
        <div class="album-hero-info">
          <p class="kind">ALBUM</p>
          <h1>${esc(album.title)}</h1>
          <p class="sub">${esc(album.artist)} • ${album.tracks.length} titres, ${fmt(total)}</p>
          <div class="hero-actions">
            <button class="primary-btn" id="playAll">&#9654; Lecture</button>
            <button class="ghost-btn big" id="shuffleAll">&#8635; Aléatoire</button>
          </div>
        </div>
      </div>
      <div class="track-list" id="trackList"></div>`;

    $('#playAll').addEventListener('click', () => playFromAlbum(album, 0));
    $('#shuffleAll').addEventListener('click', () => {
      shuffleOn = true;
      $('#shuffleBtn').classList.add('active');
      playFromAlbum(album, Math.floor(Math.random() * album.tracks.length));
    });

    const list = $('#trackList');
    album.tracks.forEach((t, i) => {
      const row = document.createElement('div');
      row.className = 'track';
      row.dataset.id = t.id;
      row.innerHTML = `
        <span class="t-num">${i + 1}</span>
        <span class="t-title">${esc(t.title)}</span>
        <span class="t-dur">${t.duration ? fmt(t.duration) : '--:--'}</span>`;
      row.addEventListener('click', () => playFromAlbum(album, i));
      list.appendChild(row);
    });
    switchView('album');
    refreshActiveTrack();
  }

  /* ---------- File de lecture ---------- */
  function playFromAlbum(album, index) {
    queue = album.tracks;
    currentAlbum = album;
    buildOrder(index);
    loadAt(0);
  }

  function playTracks(tracks) {
    queue = tracks;
    currentAlbum = null;
    buildOrder(0);
    loadAt(0);
  }

  function buildOrder(startIndex) {
    const idxs = queue.map((_, i) => i);
    if (shuffleOn) {
      for (let i = idxs.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [idxs[i], idxs[j]] = [idxs[j], idxs[i]];
      }
      const p = idxs.indexOf(startIndex);
      if (p > 0) { idxs.splice(p, 1); idxs.unshift(startIndex); }
    } else {
      const start = idxs.indexOf(startIndex);
      if (start > 0) idxs.push(...idxs.splice(0, start));
    }
    playOrder = idxs;
    qPos = 0;
  }

  function currentTrack() {
    return queue.length ? queue[playOrder[qPos]] : null;
  }

  function loadAt(pos) {
    qPos = pos;
    const t = currentTrack();
    if (!t) return;
    audio.src = trackSrc(t);
    audio.play().catch((err) => showPlayError('Lecture impossible : ' + err.message));
    updatePlayerUI();
  }

  function showPlayError(msg) {
    let el = $('#playError');
    if (!el) {
      el = document.createElement('div');
      el.id = 'playError';
      el.className = 'play-error';
      document.body.appendChild(el);
    }
    el.textContent = msg;
    el.classList.add('show');
  }

  audio.addEventListener('error', () => {
    const src = audio.currentSrc || '';
    showPlayError("Impossible de charger l'audio : " + (src || '(aucune source)'));
  });
  audio.addEventListener('playing', () => {
    const el = $('#playError');
    if (el) el.classList.remove('show');
  });

  function nextTrack() {
    if (!queue.length) return;
    if (repeatMode === 2) { audio.currentTime = 0; audio.play(); return; }
    loadAt((qPos + 1) % queue.length);
  }

  function prevTrack() {
    if (!queue.length) return;
    if (audio.currentTime > 3) { audio.currentTime = 0; return; }
    loadAt((qPos - 1 + queue.length) % queue.length);
  }

  /* ---------- Player UI ---------- */
  function currentTrackId() {
    const t = currentTrack();
    return t ? t.id : null;
  }

  function updatePlayerUI() {
    const t = currentTrack();
    if (!t) return;
    $('#playerTitle').textContent = t.title;
    const meta = trackMap.get(t.id) || {};
    $('#playerArtist').textContent = meta.artist || '';
    const album = meta.album || { title: t.title, artist: '', art: null };
    $('#playerArt').innerHTML = album.art
      ? `<img src="${album.art}" alt="">`
      : `<span style="font-size:16px">${esc(album.title.slice(0, 2).toUpperCase())}</span>`;
    $('#playerArt').style.background = album.art ? 'none' : colorsFor(album.artist + album.title);
    $('#favBtn').classList.toggle('liked', favorites.has(t.id));
    refreshActiveTrack();
  }

  function refreshActiveTrack() {
    const id = currentTrackId();
    $$('#trackList .track').forEach((r) => {
      r.classList.toggle('active', r.dataset.id === String(id));
    });
  }

  /* ---------- Contrôles ---------- */
  $('#playBtn').addEventListener('click', () => {
    if (!queue.length) { if (albums.length) openAlbum(albums[0]); return; }
    if (audio.paused) audio.play(); else audio.pause();
  });

  audio.addEventListener('play', () => { $('#playBtn').innerHTML = '&#10074;&#10074;'; });
  audio.addEventListener('pause', () => { $('#playBtn').innerHTML = '&#9654;'; });

  audio.addEventListener('loadedmetadata', () => {
    $('#durTime').textContent = fmt(audio.duration);
    renderProgress();
  });

  audio.addEventListener('timeupdate', renderProgress);
  audio.addEventListener('ended', nextTrack);

  function renderProgress() {
    const dur = audio.duration || 0;
    const cur = audio.currentTime || 0;
    $('#curTime').textContent = fmt(cur);
    $('#durTime').textContent = fmt(dur);
    const pct = dur ? (cur / dur) * 100 : 0;
    $('#progressFill').style.width = pct + '%';
    $('#progressThumb').style.left = pct + '%';
  }

  $('#nextBtn').addEventListener('click', nextTrack);
  $('#prevBtn').addEventListener('click', prevTrack);

  $('#shuffleBtn').addEventListener('click', () => {
    shuffleOn = !shuffleOn;
    $('#shuffleBtn').classList.toggle('active', shuffleOn);
    if (queue.length) buildOrder(playOrder[qPos] != null ? playOrder[qPos] : 0);
  });

  $('#repeatBtn').addEventListener('click', (e) => {
    repeatMode = (repeatMode + 1) % 3;
    e.currentTarget.classList.toggle('active', repeatMode !== 0);
    e.currentTarget.textContent = repeatMode === 2 ? '&#9881;' : '&#10227;';
  });

  /* Barre de progression (click + drag) */
  const progressBar = $('#progressBar');
  let seeking = false;
  progressBar.addEventListener('pointerdown', (e) => {
    seeking = true;
    progressBar.setPointerCapture(e.pointerId);
    seekFromEvent(e);
  });
  progressBar.addEventListener('pointermove', (e) => { if (seeking) seekFromEvent(e); });
  progressBar.addEventListener('pointerup', (e) => { seeking = false; progressBar.releasePointerCapture(e.pointerId); });

  function seekFromEvent(e) {
    const rect = progressBar.getBoundingClientRect();
    const ratio = Math.min(Math.max((e.clientX - rect.left) / rect.width, 0), 1);
    if (audio.duration) {
      audio.currentTime = ratio * audio.duration;
      renderProgress();
    }
  }

  /* Volume */
  const volumeBar = $('#volumeBar');
  let volume = 80;
  let muted = false;
  let lastVolume = volume;

  function renderVolume() {
    const shown = muted ? 0 : volume;
    $('#volumeFill').style.width = shown + '%';
    $('#volumeThumb').style.left = shown + '%';
    $('#muteIcon').textContent = shown === 0 ? '&#10005;' : '&#9834;';
    audio.volume = volume / 100;
    audio.muted = muted;
  }

  let volSeeking = false;
  volumeBar.addEventListener('pointerdown', (e) => {
    volSeeking = true;
    volumeBar.setPointerCapture(e.pointerId);
    volumeFromEvent(e);
  });
  volumeBar.addEventListener('pointermove', (e) => { if (volSeeking) volumeFromEvent(e); });
  volumeBar.addEventListener('pointerup', (e) => { volSeeking = false; volumeBar.releasePointerCapture(e.pointerId); });

  function volumeFromEvent(e) {
    const rect = volumeBar.getBoundingClientRect();
    volume = Math.min(Math.max(((e.clientX - rect.left) / rect.width) * 100, 0), 100);
    muted = volume === 0;
    lastVolume = volume;
    renderVolume();
  }

  $('#muteIcon').addEventListener('click', () => {
    muted = !muted;
    if (!muted && volume === 0) volume = lastVolume || 80;
    renderVolume();
  });
  renderVolume();

  /* Favoris */
  $('#favBtn').addEventListener('click', () => {
    const id = currentTrackId();
    if (id == null) return;
    if (favorites.has(id)) favorites.delete(id);
    else favorites.add(id);
    saveFavorites();
    $('#favBtn').classList.toggle('liked', favorites.has(id));
  });

  function saveFavorites() {
    localStorage.setItem('loc-favs', JSON.stringify([...favorites]));
  }

  /* ---------- Playlists ---------- */
  $$('.playlist-item').forEach((item) => {
    item.addEventListener('click', () => {
      let tracks;
      if (item.dataset.playlist === 'favorites') {
        tracks = [...favorites].map((id) => trackMap.get(id)?.t).filter(Boolean);
        if (!tracks.length) {
          $('#playerTitle').textContent = 'Aucun favori';
          $('#playerArtist').textContent = "Clique sur le coeur d'un titre";
          return;
        }
      } else {
        tracks = [...trackMap.values()].map((v) => v.t);
      }
      if (!shuffleOn) {
        shuffleOn = true;
        $('#shuffleBtn').classList.add('active');
      }
      playTracks(tracks);
    });
  });

  /* ---------- Recherche ---------- */
  const searchInput = $('#searchInput');
  searchInput.addEventListener('input', () => doSearch(searchInput.value.trim()));

  function doSearch(q) {
    if (!q) {
      searchResults.innerHTML = '<p class="result-msg">Tape pour rechercher des titres, albums ou artistes.</p>';
      return;
    }
    const lq = q.toLowerCase();
    const hitAlbums = albums.filter((al) =>
      al.title.toLowerCase().includes(lq) || al.artist.toLowerCase().includes(lq));
    const hitTracks = [];
    trackMap.forEach(({ t, album, artist }) => {
      if (t.title.toLowerCase().includes(lq) || artist.toLowerCase().includes(lq)) {
        hitTracks.push({ t, album, artist });
      }
    });

    searchResults.innerHTML = '';

    if (!hitAlbums.length && !hitTracks.length) {
      searchResults.innerHTML = `<p class="result-msg">Aucun résultat pour « ${esc(q)} ».</p>`;
      return;
    }

    if (hitAlbums.length) {
      const h = document.createElement('h2');
      h.textContent = 'Albums';
      searchResults.appendChild(h);
      const grid = document.createElement('div');
      grid.className = 'card-grid';
      hitAlbums.forEach((al) => {
        const card = document.createElement('div');
        card.className = 'card';
        card.innerHTML = coverHTML(al, '') +
          `<div class="card-body"><p class="card-title">${esc(al.title)}</p>
           <p class="card-sub">${esc(al.artist)}</p></div>`;
        card.addEventListener('click', () => openAlbum(al));
        grid.appendChild(card);
      });
      searchResults.appendChild(grid);
    }

    if (hitTracks.length) {
      const h = document.createElement('h2');
      h.textContent = 'Titres';
      searchResults.appendChild(h);
      const list = document.createElement('div');
      list.className = 'track-list';
      hitTracks.forEach(({ t, album, artist }) => {
        const row = document.createElement('div');
        row.className = 'track';
        row.innerHTML = `<span class="t-num">&#9835;</span>
          <span class="t-title">${esc(t.title)} <span class="t-artist">— ${esc(artist)}</span></span>
          <span class="t-dur">${t.duration ? fmt(t.duration) : '--:--'}</span>`;
        row.addEventListener('click', () => {
          const start = album.tracks.findIndex((x) => x.id === t.id);
          playFromAlbum(album, start < 0 ? 0 : start);
        });
        list.appendChild(row);
      });
      searchResults.appendChild(list);
    }
  }

  /* ---------- Bibliothèque ---------- */
  function renderLibrary() {
    libList.innerHTML = '';
    library.artists.forEach((a) => {
      const section = document.createElement('div');
      section.className = 'lib-section';
      section.innerHTML = `<h2>${esc(a.name)}</h2>`;
      const rows = document.createElement('div');
      rows.className = 'lib-list';
      a.albums.forEach((al) => {
        const album = albums.find((x) => x.title === al.title && x.artist === a.name);
        if (!album) return;
        const row = document.createElement('div');
        row.className = 'lib-row';
        row.innerHTML = coverHTML(album, 'lib-cover') +
          `<p>${esc(al.title)}</p><span>Album • ${al.tracks.length} titres</span>`;
        row.addEventListener('click', () => openAlbum(album));
        rows.appendChild(row);
      });
      section.appendChild(rows);
      libList.appendChild(section);
    });
  }

  /* ---------- Réglages ---------- */
  const settingsOverlay = $('#settingsOverlay');

  function openSettings() {
    const base = serverBase();
    const origin = window.location.origin;
    $('#serverInput').value = base === origin ? '' : base;
    $('#srvHint').textContent = window.SONNUAGE_SERVER || 'http://localhost:3000';
    $('#compressToggle').checked = compressEnabled();
    $('#connResult').innerHTML = '';
    settingsOverlay.classList.remove('hidden');
    if (!$('#serverInput').value) $('#serverInput').focus();
  }

  function closeSettings() {
    settingsOverlay.classList.add('hidden');
  }

  $('#settingsBtn').addEventListener('click', openSettings);
  $('#closeSettings').addEventListener('click', closeSettings);
  settingsOverlay.addEventListener('click', (e) => { if (e.target === settingsOverlay) closeSettings(); });

  $('#connectBtn').addEventListener('click', async () => {
    const val = $('#serverInput').value.trim().replace(/\/+$/, '');
    const base = val || window.location.origin;
    const el = $('#connResult');
    el.innerHTML = 'Connexion en cours...';
    try {
      const res = await fetch(base + '/api/info', { signal: AbortSignal.timeout(5000) });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const info = await res.json();
      if (val) localStorage.setItem('loc-server', base);
      else localStorage.removeItem('loc-server');
      el.innerHTML = `<span class="ok">&#10003; Connecté à ${esc(base)}</span>
        <p>${esc(info.name)} v${esc(info.version)} — IPs serveur : ${(info.ips || []).map(esc).join(', ') || 'aucune'}</p>`;
      closeSettings();
      loadLibrary();
    } catch (e) {
      el.innerHTML = `<span class="err">&#10005; Échec : ${esc(String(e.message || e))}</span>`;
    }
  });

  $('#compressToggle').addEventListener('change', (e) => {
    localStorage.setItem('loc-compress', e.target.checked ? '1' : '0');
  });

  /* ---------- Init ---------- */
  switchView('home');
  loadLibrary();
})();
