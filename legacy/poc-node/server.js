'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawn } = require('child_process');

const ROOT = __dirname;
const DATA = path.join(ROOT, 'data');
const CACHE_DIR = path.join(ROOT, 'cache');
const PORT = process.env.PORT || 3000;
const BITRATE = process.env.LOCALIFY_BITRATE || '192k';

function ffmpegPath() {
  if (process.env.FFMPEG_PATH) return process.env.FFMPEG_PATH;
  for (const rel of ['bin/ffmpeg.exe', 'bin/ffmpeg']) {
    const p = path.join(ROOT, rel);
    if (fs.existsSync(p)) return p;
  }
  return 'ffmpeg';
}
const FFMPEG = ffmpegPath();

const AUDIO_EXT = new Set(['.mp3', '.wav', '.flac', '.ogg', '.m4a', '.aac']);
const IMG_EXT = new Set(['.jpg', '.jpeg', '.png', '.webp', '.gif']);
const COVER_NAMES = ['folder.jpg', 'folder.png', 'cover.jpg', 'cover.png', 'front.jpg', 'albumart.jpg', 'albumart.png', 'albumartsmall.jpg'];

const MIME = {
  '.mp3': 'audio/mpeg', '.wav': 'audio/wav', '.flac': 'audio/flac',
  '.ogg': 'audio/ogg', '.m4a': 'audio/mp4', '.aac': 'audio/aac',
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png',
  '.webp': 'image/webp', '.gif': 'image/gif',
  '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
};

/* ---------- Durée des pistes (WAV / MP3) ---------- */

function wavInfo(abs) {
  const fd = fs.openSync(abs, 'r');
  try {
    const stat = fs.fstatSync(fd);
    const magic = Buffer.alloc(12);
    if (
      fs.readSync(fd, magic, 0, 12, 0) !== 12 ||
      magic.toString('ascii', 0, 4) !== 'RIFF' ||
      magic.toString('ascii', 8, 12) !== 'WAVE'
    ) return null;

    let off = 12;
    let byteRate = null;
    let dataPos = null;
    const h = Buffer.alloc(8);
    const fmt = Buffer.alloc(40);
    while (off + 8 <= stat.size) {
      fs.readSync(fd, h, 0, 8, off);
      const id = h.toString('ascii', 0, 4);
      const size = h.readUInt32LE(4);
      if (id === 'fmt ') {
        const n = fs.readSync(fd, fmt, 0, 40, off + 8);
        if (n >= 16) byteRate = fmt.readUInt32LE(8);
      }
      if (id === 'data') {
        dataPos = off + 8;
        break;
      }
      off += 8 + size;
    }
    if (byteRate && dataPos != null) {
      return { byteRate, dataStart: dataPos, size: stat.size };
    }
    return null;
  } finally {
    fs.closeSync(fd);
  }
}

function wavDuration(abs) {
  const info = wavInfo(abs);
  return info ? (info.size - info.dataStart) / info.byteRate : null;
}

const MP3_BITRATES = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]; // kbps, layer III
const MP3_SAMPLES = [44100, 48000, 32000];

function mp3Duration(abs) {
  const fd = fs.openSync(abs, 'r');
  try {
    const stat = fs.fstatSync(fd);
    const b = Buffer.alloc(10);
    let off = 0;
    if (fs.readSync(fd, b, 0, 10, 0) === 10 && b.toString('ascii', 0, 3) === 'ID3') {
      const size = ((b[6] & 0x7f) << 21) | ((b[7] & 0x7f) << 14) | ((b[8] & 0x7f) << 7) | (b[9] & 0x7f);
      off = 10 + size + ((b[5] & 0x10) ? 10 : 0);
    }
    const hb = Buffer.alloc(4);
    let foff = off;
    while (foff <= stat.size - 4 && foff - off < 65536) {
      fs.readSync(fd, hb, 0, 4, foff);
      if (hb[0] === 0xff && (hb[1] & 0xe0) === 0xe0 && (hb[1] & 0x06) !== 0) break;
      foff++;
    }
    if (foff > stat.size - 4) return null;
    const bitrateIndex = (hb[2] >> 4) & 0xf;
    const sampleIndex = (hb[2] >> 2) & 3;
    const layer = (hb[1] >> 1) & 3;
    if (bitrateIndex === 0 || bitrateIndex === 15 || sampleIndex === 3 || layer === 0) return null;
    const bitrate = MP3_BITRATES[bitrateIndex] * 1000;
    const sampleRate = MP3_SAMPLES[sampleIndex];
    const framesPerSec = sampleRate / 1152;
    const bytesPerSec = bitrate / 8;
    const dataSize = stat.size - foff;
    return dataSize / bytesPerSec;
  } finally {
    fs.closeSync(fd);
  }
}

function getDuration(abs) {
  switch (path.extname(abs).toLowerCase()) {
    case '.wav': return wavDuration(abs);
    case '.mp3': return mp3Duration(abs);
    default: return null;
  }
}

/* ---------- Index de la bibliothèque ---------- */

const trackPool = [];
const artPool = [];

function ext(name) { return path.extname(name).toLowerCase(); }

function cleanTitle(filename, stripPrefix) {
  let name = filename.replace(/\.[^.]+$/, '');
  if (stripPrefix) {
    const low = name.toLowerCase();
    if (low.startsWith(stripPrefix.toLowerCase())) name = name.slice(stripPrefix.length);
  }
  name = name.replace(/^\s*\d+[.\-_]\s*/, '');
  name = name.replace(/^\s*\d+\s+/, '');
  name = name.replace(/[\u2010\u2011\u2012\u2013\u2014\u2015]/g, '-');
  name = name.replace(/_/g, ' ');
  name = name.replace(/\s+/g, ' ').trim();
  return name;
}

function trackNumber(filename) {
  const m = filename.match(/^(\d+)/);
  return m ? parseInt(m[1], 10) : Infinity;
}

function rankImage(name) {
  const lower = name.toLowerCase();
  if (COVER_NAMES.includes(lower)) return 100;
  if (/^albumart/i.test(lower)) return 90;
  if (/^folder/i.test(lower)) return 90;
  if (/^cover/i.test(lower)) return 85;
  if (/albumart.*large/i.test(lower)) return 88;
  return 60;
}

function findArt(albumPath, artistPath, siblingDirs) {
  const candidates = [];

  function collect(dir, origin) {
    let entries = [];
    try { entries = fs.readdirSync(dir); } catch (_) { return; }
    for (const f of entries) {
      if (!IMG_EXT.has(ext(f))) continue;
      const abs = path.join(dir, f);
      candidates.push({ abs, rank: rankImage(f), origin });
    }
  }

  collect(albumPath, 0);
  collect(artistPath, 1);
  for (const s of siblingDirs) collect(s, 2);

  candidates.sort((a, b) => b.rank - a.rank || a.origin - b.origin);
  return candidates.length ? candidates[0].abs : null;
}

function scanLibrary() {
  trackPool.length = 0;
  artPool.length = 0;
  const artists = [];

  const artistDirs = fs.readdirSync(DATA, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .sort((a, b) => a.name.localeCompare(b.name, 'fr'));

  for (const ad of artistDirs) {
    const artistPath = path.join(DATA, ad.name);
    const allDirs = fs.readdirSync(artistPath, { withFileTypes: true })
      .filter((d) => d.isDirectory())
      .map((d) => path.join(artistPath, d.name));

    const albums = [];
    for (const albumPath of allDirs) {
      let files = [];
      try { files = fs.readdirSync(albumPath); } catch (_) { continue; }

      const audioFiles = files.filter((f) => AUDIO_EXT.has(ext(f)));
      if (audioFiles.length === 0) continue;

      const seen = new Set();
      const artistName = ad.name === 'AC_DC' ? 'AC/DC' : ad.name;
      const albumTitle = cleanTitle(path.basename(albumPath));
      const titlePrefix = artistName + ' - ' + albumTitle + ' - ';
      const tracks = [];
      for (const f of audioFiles) {
        const m = f.match(/^(.*)\s\(1\)(\.[^.]+)$/);
        if (m) {
          const base = m[1] + m[2];
          if (fs.existsSync(path.join(albumPath, base))) continue;
        }
        const id = trackPool.length;
        trackPool.push({ id, abs: path.join(albumPath, f), type: MIME[ext(f)] || 'audio/mpeg', format: ext(f).slice(1) });
        seen.add(f);
        tracks.push({
          id,
          title: cleanTitle(f, titlePrefix),
          track: trackNumber(f),
          duration: getDuration(path.join(albumPath, f)),
          format: ext(f).slice(1),
          url: '/stream/' + id,
        });
      }
      if (tracks.length === 0) continue;
      tracks.sort((a, b) => a.track - b.track || a.title.localeCompare(b.title, 'fr'));

      const art = findArt(albumPath, artistPath, allDirs.filter((d) => d !== albumPath));
      let artUrl = null;
      if (art) {
        artPool.push({ abs: art, type: MIME[ext(art)] || 'image/jpeg' });
        artUrl = '/art/' + (artPool.length - 1);
      }

      albums.push({
        title: cleanTitle(path.basename(albumPath)),
        dir: path.basename(albumPath),
        art: artUrl,
        tracks,
      });
    }

    if (albums.length === 0) continue;
    artists.push({
      name: ad.name === 'AC_DC' ? 'AC/DC' : ad.name,
      dir: ad.name,
      albums,
    });
  }

  return { artists };
}

/* ---------- Streaming avec Range ---------- */

function sendRange(file, type, req, res) {
  fs.stat(file, (err, stat) => {
    if (err || !stat.isFile()) return send404(res);
    const size = stat.size;
    const total = size;
    const range = req.headers.range;

    if (range) {
      const m = /bytes=(\d*)-(\d*)/.exec(range);
      let start = m && m[1] ? parseInt(m[1], 10) : null;
      let end = m && m[2] ? parseInt(m[2], 10) : null;
      if (start === null) {
        start = total - (end || 0);
        end = total - 1;
      } else if (end === null || end >= total) {
        end = total - 1;
      }
      if (start < 0 || start >= total || start > end) {
        res.writeHead(416, { 'Content-Range': 'bytes */' + total });
        return res.end();
      }
      res.writeHead(206, {
        'Content-Type': type,
        'Content-Range': 'bytes ' + start + '-' + end + '/' + total,
        'Accept-Ranges': 'bytes',
        'Content-Length': end - start + 1,
      });
      fs.createReadStream(file, { start, end }).pipe(res);
    } else {
      res.writeHead(200, {
        'Content-Type': type,
        'Content-Length': total,
        'Accept-Ranges': 'bytes',
      });
      fs.createReadStream(file).pipe(res);
    }
  });
}

function send404(res) {
  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end('404 Not Found');
}

function sendFile(absPath, type, req, res) {
  fs.stat(absPath, (err, stat) => {
    if (err || !stat.isFile()) return send404(res);
    if (req.method === 'HEAD') {
      res.writeHead(200, { 'Content-Type': type, 'Content-Length': stat.size });
      return res.end();
    }
    sendRange(absPath, type, req, res);
  });
}

/* ---------- Transcodage (WAV -> MP3, mis en cache) ---------- */

const transcodePromises = new Map();

function transcodeCacheFile(track) {
  const cacheFile = path.join(CACHE_DIR, track.id + '.mp3');
  if (fs.existsSync(cacheFile) && fs.statSync(cacheFile).size > 0) return Promise.resolve(cacheFile);
  if (transcodePromises.has(track.id)) return transcodePromises.get(track.id);

  const p = new Promise((resolve) => {
    if (!FFMPEG) return resolve(null);
    fs.mkdirSync(CACHE_DIR, { recursive: true });
    const proc = spawn(FFMPEG, ['-i', track.abs, '-vn', '-acodec', 'libmp3lame', '-b:a', BITRATE, '-f', 'mp3', cacheFile]);
    proc.on('close', (code) => {
      transcodePromises.delete(track.id);
      resolve(code === 0 && fs.existsSync(cacheFile) && fs.statSync(cacheFile).size > 0 ? cacheFile : null);
    });
    proc.on('error', () => {
      transcodePromises.delete(track.id);
      resolve(null);
    });
  });
  transcodePromises.set(track.id, p);
  return p;
}

/* ---------- Utilitaires réseau ---------- */

function lanIPs() {
  const ips = [];
  if (process.env.LOCALIFY_PUBLIC_IP) ips.push(process.env.LOCALIFY_PUBLIC_IP);
  for (const name of Object.keys(os.networkInterfaces())) {
    for (const ni of os.networkInterfaces()[name]) {
      if (ni.family === 'IPv4' && !ni.internal) ips.push(ni.address);
    }
  }
  return [...new Set(ips)];
}

/* ---------- Router ---------- */

function enableCORS(res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Range, Content-Type');
}

scanLibrary();

const server = http.createServer((req, res) => {
  const [pathname, queryString] = req.url.split('?');
  const url = decodeURIComponent(pathname);
  const query = new URLSearchParams(queryString || '');

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
      'Access-Control-Allow-Headers': 'Range, Content-Type',
    });
    return res.end();
  }
  enableCORS(res);

  if (url === '/api/library') {
    const lib = scanLibrary();
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    return res.end(JSON.stringify(lib));
  }

  if (url === '/api/info') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    return res.end(JSON.stringify({ name: 'Localify', version: '1.0', ips: lanIPs() }));
  }

  const stream = /^\/stream\/(\d+)$/.exec(url);
  if (stream) {
    const t = trackPool[parseInt(stream[1], 10)];
    if (!t) return send404(res);
    const compressed = query.get('c') === '1' || query.get('c') === 'true';
    if (compressed && t.format === 'wav') {
      return transcodeCacheFile(t).then((cacheFile) => {
        if (cacheFile) return sendRange(cacheFile, 'audio/mpeg', req, res);
        return sendRange(t.abs, t.type, req, res);
      });
    }
    return sendRange(t.abs, t.type, req, res);
  }

  const art = /^\/art\/(\d+)$/.exec(url);
  if (art) {
    const a = artPool[parseInt(art[1], 10)];
    if (!a) return send404(res);
    return sendFile(a.abs, a.type, req, res);
  }

  const staticPath = url === '/' ? 'index.html' : url.replace(/^\/+/, '');
  if (!staticPath || staticPath.includes('..')) return send404(res);
  const absPath = path.join(ROOT, staticPath);
  const type = MIME[ext(absPath)] || 'application/octet-stream';
  sendFile(absPath, type, req, res);
});

server.listen(PORT, () => {
  console.log('Localify serveur démarré :');
  console.log('   PC      -> http://localhost:' + PORT);
  const ips = lanIPs();
  for (const ip of ips) console.log('   Téléphone -> http://' + ip + ':' + PORT);
  if (!ips.length) console.log('   (aucune IP LAN détectée)');
  if (FFMPEG) console.log('Réencodage WAV->MP3 : actif (' + FFMPEG + ')');
  else console.log('Réencodage WAV->MP3 : indisponible (ffmpeg introuvable)');
});
