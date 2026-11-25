// MeraPrint Host — Built for Pankaj Jaiswal (MeraCall parivaar)
//
// Is PC ko "Host" banata hai:
//   🖨️ Print  — phone / doosre PC se file aaye, yahan ke printer par chhape
//   📠 Scan   — yahan ke scanner se scan karke phone / PC tak bheje
//   🖥️ Remote — phone / doosre PC se is PC ki screen dekho aur chalao
//
// Har client (phone/PC) ke liye aap Host par chunte ho: ✅ seedha / ❓ har baar poochho / ⛔ mana
//
// Chalana: START-HOST.bat  (ya: node host.js)   — Node.js 18+, koi npm package nahi
// Admin panel (sirf isi PC par): http://localhost:8791

'use strict';
const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');
const crypto = require('crypto');
const { spawn } = require('child_process');
const { EventEmitter } = require('events');

const ROOT = __dirname;
const TOP = path.join(ROOT, '..');
const DATA = path.join(ROOT, 'data');
const JOBS = path.join(DATA, 'jobs');
const TOOLS = path.join(ROOT, 'tools');
for (const d of [DATA, JOBS, TOOLS]) fs.mkdirSync(d, { recursive: true });

// ---------------- Config ----------------
const CFG_PATH = path.join(TOP, 'host-config.json');
const CFG_DEFAULT = {
  hostName: os.hostname(),
  port: 8790,
  adminPort: 8791,
  internet: true,
  meracallConfig: 'D:\\call apk\\meracall-config.json',
  maxUploadMB: 150,
  shareFolder: path.join(__dirname, '..', 'MeraPrint-Share')   // Download isi folder se; aayi files isme "Aayi hui files" me
};
if (!fs.existsSync(CFG_PATH)) fs.writeFileSync(CFG_PATH, JSON.stringify(CFG_DEFAULT, null, 2));
const cfg = Object.assign({}, CFG_DEFAULT, readJson(CFG_PATH) || {});
const mc = readJson(cfg.meracallConfig) || {};
// public.json: sirf khule (non-secret) pate — har Host ke saath jata hai. Secret (token) sirf maalik ke PC par (meracall-config).
const PUB = readJson(path.join(TOP, 'public.json')) || {};
const FB = (mc.firebaseUrl || PUB.firebaseUrl) ? String(mc.firebaseUrl || PUB.firebaseUrl).replace(/\/?$/, '/') : '';
const FB_SECRET = mc.firebaseSecret || '';
// GitHub (MeraCall wala repo): Host ka naya internet pata yahan likha jata hai -> link badle to bhi client khud jud jaate hain
const GH_REPO = mc.githubUser && mc.githubRepo ? mc.githubUser + '/' + mc.githubRepo : (PUB.ghRepo || '');
const GH_TOKEN = mc.githubToken || '';
const OWNER = !!GH_TOKEN;   // maalik ka Host (update yahin se nikalta hai, khud update nahi leta)
const GO_LINK = GH_REPO ? 'https://' + GH_REPO.split('/')[0].toLowerCase() + '.github.io/' + GH_REPO.split('/')[1] + '/meraprint/#' : '';
const APP_VER = (readJson(path.join(TOP, 'app-version.json')) || {}).version || 0;
const SHARE = path.resolve(cfg.shareFolder);
const RECV = path.join(SHARE, 'Aayi hui files');
fs.mkdirSync(RECV, { recursive: true });
// share folder ke andar ka asli raasta (bahar nikalne ki koshish = null)
function inShare(rel) {
  const p = path.resolve(SHARE, String(rel || '').replace(/^[\\/]+/, ''));
  if (p !== SHARE && !p.startsWith(SHARE + path.sep)) return null;
  return p;
}
const MIME = { '.pdf': 'application/pdf', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png', '.gif': 'image/gif', '.mp4': 'video/mp4', '.mp3': 'audio/mpeg', '.txt': 'text/plain; charset=utf-8', '.zip': 'application/zip', '.apk': 'application/vnd.android.package-archive', '.docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', '.xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' };
const tickets = new Map();   // download ticket -> {file, name, until}

function readJson(f) { try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch (e) { return null; } }

// ---------------- Log ----------------
const logs = [];
function log(...a) {
  const line = new Date().toLocaleTimeString('en-IN') + '  ' + a.join(' ');
  console.log(line);
  logs.push(line); if (logs.length > 150) logs.shift();
}

// ---------------- State (devices, PIN) ----------------
const STATE_PATH = path.join(DATA, 'state.json');
const KINDS = ['print', 'scan', 'remote', 'send', 'download'];
const PERMS = ['allow', 'ask', 'deny'];
const ALPH = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
function randCode(n) { const b = crypto.randomBytes(n); let s = ''; for (let i = 0; i < n; i++) s += ALPH[b[i] % ALPH.length]; return s; }
function newPin() { return String(crypto.randomInt(0, 1000000)).padStart(6, '0'); }
const sha = s => crypto.createHash('sha256').update(String(s)).digest('hex');

const state = readJson(STATE_PATH) || {};
if (!state.hostId) state.hostId = randCode(4) + '-' + randCode(4) + '-' + randCode(4);
if (!state.pin) state.pin = newPin();
if (!state.devices) state.devices = {};
if (!state.defaults) state.defaults = {};
if (!state.fbKey) state.fbKey = crypto.randomBytes(24).toString('hex');   // is Host ki apni chabi (Firebase me sirf yahi apna pata badal sake)
for (const k of KINDS) if (!state.defaults[k]) state.defaults[k] = 'ask';
let saveTimer = null;
function save(now) {
  clearTimeout(saveTimer);
  const w = () => { try { fs.writeFileSync(STATE_PATH + '.tmp', JSON.stringify(state, null, 1)); fs.renameSync(STATE_PATH + '.tmp', STATE_PATH); } catch (e) { log('✖ state save:', e.message); } };
  if (now) w(); else saveTimer = setTimeout(w, 2000);
}
save(true);

function deviceByToken(tok) {
  if (!tok || String(tok).length < 30) return null;
  const h = sha(tok);
  for (const id in state.devices) { const d = state.devices[id]; if (d.tokenHash === h || (d.tokenHashes && d.tokenHashes.includes(h))) return Object.assign({ id }, d); }
  return null;
}
function touch(dev) { const d = state.devices[dev.id]; if (d) { d.lastSeen = Date.now(); save(); } }

// ---------------- Login ID + Password (ijazat ID par — kisi bhi device / link se login karo, wahi ijazat) ----------------
if (!state.users) state.users = {};
const normUser = u => String(u || '').trim().toLowerCase().replace(/[^a-z0-9._-]/g, '').slice(0, 30);
function passHash(pass, salt) { return crypto.scryptSync(String(pass), salt, 32).toString('hex'); }
function checkPass(u, pass) {
  const x = state.users[u]; if (!x) { passHash(pass, 'dummy-salt-xyz'); return false; }   // samay barabar rakho
  const h = passHash(pass, x.salt);
  return crypto.timingSafeEqual(Buffer.from(h, 'hex'), Buffer.from(x.passHash, 'hex'));
}
function permsOf(devId) {
  const d = state.devices[devId] || {};
  if (d.user && state.users[d.user]) return Object.assign({}, state.defaults, state.users[d.user].perms);
  return Object.assign({}, state.defaults, d.perms);
}
const loginFails = new Map();   // user -> {n, until}

// Pairing par galat PIN se bachav
let pinFails = 0, pinLockUntil = 0;

// ---------------- Approvals (❓ har baar poochho) ----------------
const pending = new Map();     // id -> {id, deviceId, deviceName, kind, text, ts, done}
let pendSeq = 1;
const KIND_TXT = { print: 'print karna', scan: 'scan karna', remote: 'ye PC door se chalana (Remote)', send: 'is PC par file bhejna', download: 'is PC se file download karna' };
function approve(dev, kind, detail) {
  const perm = permsOf(dev.id)[kind] || 'ask';
  if (perm === 'allow') return Promise.resolve(true);
  if (perm === 'deny') return Promise.resolve(false);
  return new Promise(resolve => {
    const id = String(pendSeq++);
    const text = '"' + dev.name + '" ' + KIND_TXT[kind] + ' chahta hai.' + (detail ? '\n' + detail : '') + '\n\nAllow karein?';
    const p = { id, deviceId: dev.id, deviceName: dev.name, kind, text, ts: Date.now() };
    p.finish = ok => { if (p.done) return; p.done = true; pending.delete(id); clearTimeout(p.timer); log((ok ? '✅ Allow: ' : '⛔ Mana: ') + dev.name + ' — ' + kind); resolve(ok); };
    p.timer = setTimeout(() => p.finish(false), 95000);
    pending.set(id, p);
    helper.ask(id, text);
    log('❓ Ijazat maangi: ' + dev.name + ' — ' + kind);
  });
}

// ---------------- Helper (C#) ----------------
const helper = new (class extends EventEmitter {
  constructor() { super(); this.proc = null; this.buf = Buffer.alloc(0); this.capQ = []; }
  exe() { return path.join(TOOLS, 'helper.exe'); }
  compile() {
    const src = path.join(ROOT, 'helper.cs'), exe = this.exe();
    try { if (fs.existsSync(exe) && fs.statSync(exe).mtimeMs >= fs.statSync(src).mtimeMs) return true; } catch (e) {}
    const win = process.env.WINDIR || 'C:\\Windows';
    const csc = [win + '\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe', win + '\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe'].find(f => fs.existsSync(f));
    if (!csc) { log('✖ csc.exe (.NET Framework 4) nahi mila — Remote Desktop aur popup nahi chalenge'); return false; }
    log('🔧 helper.exe ban raha hai (ek baar)...');
    const r = require('child_process').spawnSync(csc, ['/nologo', '/codepage:65001', '/target:exe', '/optimize+', '/out:' + exe,
      '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', src], { encoding: 'utf8', windowsHide: true });
    if (r.status !== 0) { log('✖ helper.exe nahi bana:\n' + (r.stdout || '') + (r.stderr || '')); return false; }
    log('✅ helper.exe taiyaar');
    return true;
  }
  start() {
    if (process.platform !== 'win32') { log('(Windows nahi hai — helper band)'); return; }
    if (!this.compile()) return;
    const p = spawn(this.exe(), [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.proc = p; this.buf = Buffer.alloc(0);
    p.stdout.on('data', d => this.onData(d));
    p.stderr.on('data', d => log('helper:', String(d).trim()));
    p.stdin.on('error', () => {});
    p.on('exit', code => {
      this.proc = null;
      for (const q of this.capQ.splice(0)) q.reject(new Error('helper band'));
      log('✖ helper band hua (' + code + '), 3 sec me dobara');
      setTimeout(() => this.start(), 3000);
    });
  }
  onData(d) {
    this.buf = Buffer.concat([this.buf, d]);
    while (this.buf.length >= 5) {
      const type = String.fromCharCode(this.buf[0]), n = this.buf.readUInt32LE(1);
      if (this.buf.length < 5 + n) break;
      const pl = this.buf.subarray(5, 5 + n); this.buf = this.buf.subarray(5 + n);
      if (type === 'F') { const q = this.capQ.shift(); if (q) q.resolve(Buffer.from(pl)); }
      else if (type === 'E') { const q = this.capQ.shift(); if (q) q.reject(new Error(String(pl))); }
      else if (type === 'A') { const [id, ok] = String(pl).split(' '); const p = pending.get(id); if (p) p.finish(ok === '1'); }
      else if (type === 'X') { log('✖ PC par "Band karo" dabaya — Remote band'); kickRemote(); }
      else if (type === 'L') log('helper:', String(pl));
    }
  }
  cmd(s) { if (this.proc) try { this.proc.stdin.write(s + '\n'); return true; } catch (e) {} return false; }
  capture(scale, quality) {
    return new Promise((resolve, reject) => {
      if (!this.cmd('CAP ' + scale + ' ' + quality)) return reject(new Error('helper nahi chal raha'));
      this.capQ.push({ resolve, reject });
    });
  }
  ask(id, text) { if (!this.cmd('ASK ' + id + ' ' + Buffer.from(text).toString('base64'))) log('(popup nahi dikha — admin panel me Allow karein)'); }
  indicator(text) { this.cmd('IND ' + (text ? Buffer.from(text).toString('base64') : '-')); }
})();

// ---------------- PowerShell ----------------
function ps(script, env, timeoutMs) {
  return new Promise(resolve => {
    const p = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command',
      '[Console]::OutputEncoding=[Text.Encoding]::UTF8; ' + script],
      { windowsHide: true, env: Object.assign({}, process.env, env || {}) });
    let out = '', err = '';
    p.stdout.on('data', d => out += d); p.stderr.on('data', d => err += d);
    const t = setTimeout(() => { try { p.kill(); } catch (e) {} }, timeoutMs || 60000);
    p.on('exit', code => { clearTimeout(t); resolve({ code, out: out.trim(), err: err.trim() }); });
    p.on('error', e => { clearTimeout(t); resolve({ code: -1, out: '', err: e.message }); });
  });
}
function runFile(file, args, env) {
  return new Promise(resolve => {
    const p = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', file].concat(args || []),
      { windowsHide: true, env: Object.assign({}, process.env, env || {}) });
    let out = '';
    p.stdout.on('data', d => out += d); p.stderr.on('data', d => out += d);
    const t = setTimeout(() => { try { p.kill(); } catch (e) {} }, 300000);
    p.on('exit', code => { clearTimeout(t); resolve({ code, out: out.trim() }); });
    p.on('error', e => { clearTimeout(t); resolve({ code: -1, out: e.message }); });
  });
}
function runExe(exe, args, timeoutMs) {
  return new Promise(resolve => {
    const p = spawn(exe, args, { windowsHide: true });
    let out = '';
    p.stdout.on('data', d => out += d); p.stderr.on('data', d => out += d);
    const t = setTimeout(() => { try { p.kill(); } catch (e) {} }, timeoutMs || 300000);
    p.on('exit', code => { clearTimeout(t); resolve({ code, out: out.trim() }); });
    p.on('error', e => { clearTimeout(t); resolve({ code: -1, out: e.message }); });
  });
}

// ---------------- Printers ----------------
let printerCache = null, printerAt = 0;
async function listPrinters() {
  if (printerCache && Date.now() - printerAt < 20000) return printerCache;
  const r = await ps('@(Get-CimInstance Win32_Printer | Select-Object Name,Default,WorkOffline,PrinterStatus) | ConvertTo-Json -Compress', null, 30000);
  let arr = [];
  try { arr = JSON.parse(r.out || '[]'); if (!Array.isArray(arr)) arr = [arr]; } catch (e) {}
  printerCache = arr.filter(p => p && p.Name).map(p => ({ name: p.Name, isDefault: !!p.Default, offline: !!p.WorkOffline }));
  printerAt = Date.now();
  return printerCache;
}
function sumatra() {
  return [path.join(TOOLS, 'SumatraPDF.exe'), 'C:\\Program Files\\SumatraPDF\\SumatraPDF.exe',
    path.join(process.env.LOCALAPPDATA || '', 'SumatraPDF', 'SumatraPDF.exe')].find(f => f && fs.existsSync(f));
}
function soffice() {
  return ['C:\\Program Files\\LibreOffice\\program\\soffice.exe', 'C:\\Program Files (x86)\\LibreOffice\\program\\soffice.exe'].find(f => fs.existsSync(f));
}
const EXT = {
  pdf: ['.pdf', '.xps', '.oxps', '.epub', '.djvu', '.cbz'],
  img: ['.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff', '.webp', '.heic'],
  word: ['.doc', '.docx', '.docm', '.rtf', '.odt', '.txt', '.htm', '.html', '.xml', '.wps'],
  excel: ['.xls', '.xlsx', '.xlsm', '.csv', '.ods'],
  ppt: ['.ppt', '.pptx', '.pps', '.ppsx', '.odp']
};
function kindOf(ext) { for (const k in EXT) if (EXT[k].includes(ext)) return k; return 'other'; }

async function toPdf(job, kind) {
  const out = job.file.replace(/\.[^.]+$/, '') + '-conv.pdf';
  job.msg = 'PDF me badal rahe hain...';
  const r = await runFile(path.join(ROOT, 'office2pdf.ps1'), [], { MP_IN: job.file, MP_OUT: out, MP_KIND: kind });
  if (r.code === 0 && fs.existsSync(out)) return out;
  log('Office se PDF nahi bana (' + (r.out || r.code) + '), LibreOffice try...');
  const so = soffice();
  if (so) {
    const r2 = await runExe(so, ['--headless', '--convert-to', 'pdf', '--outdir', path.dirname(job.file), job.file], 180000);
    const p2 = job.file.replace(/\.[^.]+$/, '') + '.pdf';
    if (fs.existsSync(p2)) return p2;
    log('LibreOffice bhi nahi: ' + r2.out);
  }
  return null;
}

function sumatraSettings(o, kind) {
  const s = [];
  if (o.pages && /^[0-9,\- ]{1,60}$/.test(o.pages)) s.push(o.pages.replace(/\s+/g, ''));
  s.push((Math.min(99, Math.max(1, parseInt(o.copies, 10) || 1))) + 'x');
  s.push(o.color === 'bw' ? 'monochrome' : 'color');
  if (o.duplex === 'long') s.push('duplexlong'); else if (o.duplex === 'short') s.push('duplexshort'); else s.push('simplex');
  if (['A3', 'A4', 'A5', 'letter', 'legal'].includes(o.paper)) s.push('paper=' + o.paper);
  if (o.orient === 'portrait' || o.orient === 'landscape') s.push(o.orient);
  s.push(o.fit === 'none' ? 'noscale' : (o.fit === 'shrink' ? 'shrink' : (kind === 'img' ? 'fit' : 'shrink')));
  return s.join(',');
}

async function runPrint(job) {
  const ext = path.extname(job.file).toLowerCase();
  let kind = kindOf(ext), target = job.file;
  if (kind === 'word' || kind === 'excel' || kind === 'ppt') {
    const pdf = await toPdf(job, kind);
    if (pdf) { target = pdf; kind = 'pdf'; job.extra.push(pdf); }
  }
  job.state = 'printing'; job.msg = 'Printer ko bhej rahe hain...';
  const sp = sumatra();
  if ((kind === 'pdf' || kind === 'img') && sp) {
    const r = await runExe(sp, ['-print-to', job.printer, '-print-settings', sumatraSettings(job.opts, kind), '-silent', '-exit-when-done', target], 300000);
    if (r.code === 0) return done(job, '✅ Printer ko bhej diya');
    log('Sumatra print error: ' + r.code + ' ' + r.out);
  }
  // Baaki sab: Windows ka apna "PrintTo" (jis app se wo file khulti hai)
  if (ext === '.txt' || ext === '.log') {
    const r = await runExe('notepad.exe', ['/pt', target, job.printer], 120000);
    if (r.code === 0) return done(job, '✅ Printer ko bhej diya (Notepad)');
  }
  const r = await ps('Start-Process -FilePath $env:MP_FILE -Verb PrintTo -ArgumentList (\'"\' + $env:MP_PRINTER + \'"\') -WindowStyle Hidden', { MP_FILE: target, MP_PRINTER: job.printer }, 300000);
  if (r.code === 0 && !r.err) return done(job, '✅ Printer ko bhej diya');
  if (!sp && (kind === 'pdf' || kind === 'img')) return fail(job, 'SumatraPDF nahi mila. START-HOST.bat dobara chalaiye (wo khud download karega).');
  fail(job, 'Is tarah ki file (' + ext + ') is PC par print nahi ho payi. PDF bana kar bhejiye.');
}

// ---------------- Scanner ----------------
let scanCache = null, scanAt = 0;
async function listScanners() {
  if (scanCache && Date.now() - scanAt < 20000) return scanCache;
  const r = await runFile(path.join(ROOT, 'scan.ps1'), [], { MP_LIST: '1' });
  let arr = [];
  try { arr = JSON.parse(r.out || '[]'); if (!Array.isArray(arr)) arr = [arr]; } catch (e) { log('scanner list:', r.out); }
  scanCache = arr.filter(s => s && s.id); scanAt = Date.now();
  return scanCache;
}
async function runScan(job) {
  job.state = 'scanning'; job.msg = 'Scan ho raha hai... (scanner par kagaz rakha hai na?)';
  const out = path.join(JOBS, job.id + '.jpg');
  const r = await runFile(path.join(ROOT, 'scan.ps1'), [], { MP_SCANNER: job.scanner || '', MP_DPI: String(job.dpi), MP_MODE: job.mode, MP_OUT: out });
  if (r.code === 0 && fs.existsSync(out)) { job.result = out; job.size = fs.statSync(out).size; return done(job, '✅ Scan taiyaar'); }
  fail(job, 'Scan nahi hua: ' + (r.out || 'scanner check karein').slice(0, 200));
}

// ---------------- Jobs ----------------
const jobs = new Map();
function newJob(dev, kind) {
  const id = crypto.randomBytes(9).toString('hex');
  const j = { id, kind, deviceId: dev.id, deviceName: dev.name, state: 'approval', msg: '', ts: Date.now(), extra: [] };
  jobs.set(id, j);
  return j;
}
function done(j, msg) { j.state = 'done'; j.msg = msg; log(msg + ' — ' + j.deviceName + (j.name ? ' — ' + j.name : '')); cleanJob(j, false); }
function fail(j, msg) { j.state = 'error'; j.msg = msg; log('✖ ' + msg + ' — ' + j.deviceName); cleanJob(j, false); }
function cleanJob(j, all) {
  // print ki file 10 minute baad mitao (kuch apps file ko der se padhte hain)
  if (j.kind === 'print') { const fl = [j.file].concat(j.extra).filter(Boolean); setTimeout(() => { for (const f of fl) fs.rm(f, { force: true }, () => {}); }, all ? 0 : 600000); }
  if (all && j.result && j.kind === 'scan') fs.rm(j.result, { force: true }, () => {});
}
setInterval(() => {
  for (const [id, j] of jobs) if (Date.now() - j.ts > 3600000) { cleanJob(j, true); jobs.delete(id); }
  for (const [t, v] of tickets) if (v.until < Date.now()) tickets.delete(t);
  for (const f of fs.readdirSync(JOBS)) { try { const st = fs.statSync(path.join(JOBS, f)); if (Date.now() - st.mtimeMs > 7200000) fs.rmSync(path.join(JOBS, f), { force: true }); } catch (e) {} }
}, 600000);

// ---------------- Remote Desktop ----------------
const sessions = new Set();
function updateIndicator() {
  const names = [...sessions].filter(s => s.ok).map(s => s.dev.name);
  helper.indicator(names.length ? '● Remote chal raha hai: ' + names.join(', ').slice(0, 40) : null);
}
function kickRemote(deviceId) {
  for (const s of [...sessions]) if (!deviceId || s.dev && s.dev.id === deviceId) { try { s.ws.sendJson({ t: 'err', msg: 'Host PC ne Remote band kar diya' }); } catch (e) {} s.ws.close(); }
}
const VK_OK = n => Number.isInteger(n) && n > 0 && n < 256;
function remoteSession(ws, req) {
  const s = { ws, ok: false, dev: null, want: false, busy: false, last: null, scale: 0.75, q: 55 };
  sessions.add(s);
  const held = { keys: new Set(), btn: new Set() };
  const authTimer = setTimeout(() => { if (!s.dev) ws.close(); }, 15000);
  ws.on('close', () => {
    for (const vk of held.keys) helper.cmd('K ' + vk + ' 0');
    for (const b of held.btn) helper.cmd('B ' + b + ' 0');
    sessions.delete(s); clearTimeout(authTimer);
    if (s.ok) log('🖥️ Remote band: ' + s.dev.name);
    updateIndicator();
  });
  const pump = () => {
    if (!s.ok || !s.want || s.busy) return;
    s.busy = true;
    helper.capture(s.scale, s.q).then(buf => {
      s.busy = false;
      if (s.last && buf.length === s.last.length && buf.equals(s.last)) { setTimeout(pump, 120); return; }
      s.last = buf; s.want = false;
      ws.send(buf);
    }).catch(e => {
      s.busy = false;
      ws.sendJson({ t: 'err', msg: 'Screen nahi mil rahi (' + e.message + '). PC lock ho to unlock karein.', soft: true });
      setTimeout(pump, 1500);
    });
  };
  ws.on('message', async (m, isText) => {
    if (!isText) return;
    let o; try { o = JSON.parse(m); } catch (e) { return; }
    if (!s.dev) {
      if (o.t !== 'auth') return ws.close();
      const dev = deviceByToken(o.token);
      if (!dev) { ws.sendJson({ t: 'err', msg: 'Ye device ab is PC se juda nahi hai. Dobara jodiye.', unpair: true }); return ws.close(); }
      s.dev = dev; touch(dev); clearTimeout(authTimer);
      ws.sendJson({ t: 'wait', msg: 'Host PC se ijazat...' });
      const ok = await approve(dev, 'remote', '');
      if (!ok) { ws.sendJson({ t: 'err', msg: 'Host PC ne Remote ki ijazat nahi di.' }); return ws.close(); }
      if (!sessions.has(s)) return;
      if (!helper.proc) { ws.sendJson({ t: 'err', msg: 'Host par helper nahi chal raha (log dekhein).' }); return ws.close(); }
      s.ok = true; log('🖥️ Remote shuru: ' + dev.name);
      updateIndicator();
      ws.sendJson({ t: 'ok' });
      return;
    }
    if (!s.ok) return;
    switch (o.t) {
      case 'next': s.want = true; pump(); break;
      case 'q': s.scale = Math.min(1, Math.max(0.25, +o.scale || 0.75)); s.q = Math.min(90, Math.max(15, parseInt(o.q, 10) || 55)); s.last = null; break;
      case 'm': if (isFinite(o.x) && isFinite(o.y)) helper.cmd('M ' + (+o.x).toFixed(5) + ' ' + (+o.y).toFixed(5)); break;
      case 'b': if (['L', 'R', 'M'].includes(o.b)) { helper.cmd('B ' + o.b + ' ' + (o.d ? 1 : 0)); if (o.d) held.btn.add(o.b); else held.btn.delete(o.b); } break;
      case 'w': helper.cmd('W ' + (parseInt(o.dy, 10) || 0) + ' ' + (parseInt(o.dx, 10) || 0)); break;
      case 'k': if (VK_OK(o.vk)) { helper.cmd('K ' + o.vk + ' ' + (o.d ? 1 : 0)); if (o.d) held.keys.add(o.vk); else held.keys.delete(o.vk); } break;
      case 'combo':
        if (Array.isArray(o.keys) && o.keys.length <= 4 && o.keys.every(VK_OK)) {
          for (const k of o.keys) helper.cmd('K ' + k + ' 1');
          for (const k of [...o.keys].reverse()) helper.cmd('K ' + k + ' 0');
        }
        break;
      case 'txt': if (typeof o.s === 'string' && o.s.length <= 2000) helper.cmd('T ' + Buffer.from(o.s).toString('base64')); break;
    }
  });
}

// ---------------- WebSocket (bina npm) ----------------
class WS extends EventEmitter {
  constructor(socket) {
    super(); this.s = socket; this.buf = Buffer.alloc(0); this.frag = null; this.fragOp = 0; this.closed = false;
    socket.setNoDelay(true);
    socket.on('data', d => { this.buf = Buffer.concat([this.buf, d]); this.parse(); });
    socket.on('close', () => { if (!this.closed) { this.closed = true; this.emit('close'); } });
    socket.on('error', () => {});
    this.ping = setInterval(() => this.raw(9, Buffer.alloc(0)), 25000);
    this.on('close', () => clearInterval(this.ping));
  }
  parse() {
    while (this.buf.length >= 2) {
      const b0 = this.buf[0], b1 = this.buf[1], fin = b0 & 0x80, op = b0 & 0x0f, masked = b1 & 0x80;
      let len = b1 & 0x7f, off = 2;
      if (len === 126) { if (this.buf.length < 4) return; len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) return; len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      if (len > 4 * 1024 * 1024) return this.close();
      let mask = null;
      if (masked) { if (this.buf.length < off + 4) return; mask = this.buf.subarray(off, off + 4); off += 4; }
      if (this.buf.length < off + len) return;
      const p = Buffer.from(this.buf.subarray(off, off + len));
      if (mask) for (let i = 0; i < p.length; i++) p[i] ^= mask[i & 3];
      this.buf = this.buf.subarray(off + len);
      if (op === 8) return this.close();
      if (op === 9) { this.raw(10, p); continue; }
      if (op === 10) continue;
      if (op === 0) {
        if (!this.frag) continue;
        this.frag = Buffer.concat([this.frag, p]);
        if (fin) { const t = this.fragOp === 1; this.emit('message', t ? this.frag.toString('utf8') : this.frag, t); this.frag = null; }
        continue;
      }
      if (!fin) { this.frag = p; this.fragOp = op; continue; }
      this.emit('message', op === 1 ? p.toString('utf8') : p, op === 1);
    }
  }
  raw(op, payload) {
    if (this.closed) return;
    const n = payload.length;
    let h;
    if (n < 126) { h = Buffer.alloc(2); h[1] = n; }
    else if (n < 65536) { h = Buffer.alloc(4); h[1] = 126; h.writeUInt16BE(n, 2); }
    else { h = Buffer.alloc(10); h[1] = 127; h.writeBigUInt64BE(BigInt(n), 2); }
    h[0] = 0x80 | op;
    try { this.s.write(Buffer.concat([h, payload])); } catch (e) {}
  }
  send(data) { if (typeof data === 'string') this.raw(1, Buffer.from(data)); else this.raw(2, data); }
  sendJson(o) { this.send(JSON.stringify(o)); }
  close() { if (this.closed) return; this.raw(8, Buffer.alloc(0)); this.closed = true; try { this.s.end(); } catch (e) {} setTimeout(() => { try { this.s.destroy(); } catch (e) {} }, 1000); this.emit('close'); }
}

// ---------------- HTTP helpers ----------------
function send(res, code, body, type, extra) {
  res.writeHead(code, Object.assign({
    'Content-Type': type || 'application/json; charset=utf-8',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'x-token, x-name, x-opts, content-type',
    'Access-Control-Allow-Methods': 'GET, PUT, POST, DELETE, OPTIONS',
    'Cache-Control': 'no-store'
  }, extra || {}));
  res.end(type ? body : JSON.stringify(body));
}
function readBody(req, max) {
  return new Promise((resolve, reject) => {
    const parts = []; let n = 0;
    req.on('data', d => { n += d.length; if (n > max) { reject(new Error('bada')); req.destroy(); } else parts.push(d); });
    req.on('end', () => resolve(Buffer.concat(parts)));
    req.on('error', reject);
  });
}
function saveBody(req, file, max) {
  return new Promise((resolve, reject) => {
    const w = fs.createWriteStream(file); let n = 0, bad = false;
    req.on('data', d => { n += d.length; if (n > max && !bad) { bad = true; req.destroy(); w.destroy(); fs.rm(file, { force: true }, () => {}); reject(new Error('bada')); } });
    req.pipe(w);
    w.on('finish', () => { if (!bad) resolve(n); });
    w.on('error', e => { if (!bad) reject(e); });
    req.on('error', e => { if (!bad) { bad = true; reject(e); } });
  });
}
function lanIps() {
  const out = [];
  for (const [name, list] of Object.entries(os.networkInterfaces())) {
    if (/vEthernet|VirtualBox|VMware|Loopback|WSL|Hyper-V/i.test(name)) continue;
    for (const a of list || []) if (a.family === 'IPv4' && !a.internal && !a.address.startsWith('169.254.')) out.push(a.address);
  }
  out.sort((a, b) => (/^192\.168\./.test(b) ? 1 : 0) - (/^192\.168\./.test(a) ? 1 : 0));
  return out;
}
const lanUrls = () => lanIps().map(ip => 'http://' + ip + ':' + cfg.port);
function webPage() {
  let html = fs.readFileSync(path.join(ROOT, '..', 'web', 'index.html'), 'utf8');
  return html.replace("'__FB_URL__'", JSON.stringify(FB)).replace("'__GH__'", JSON.stringify(GH_REPO));
}

// ---------------- Main server (LAN + internet) ----------------
async function handle(req, res) {
  if (req.method === 'OPTIONS') return send(res, 204, '', 'text/plain');
  const u = new URL(req.url, 'http://x'), p = u.pathname;
  if (req.method === 'GET' && (p === '/' || p === '/index.html')) return send(res, 200, webPage(), 'text/html; charset=utf-8');
  if (p === '/api/ping') return send(res, 200, { ok: true, app: 'meraprint', hostId: state.hostId, name: cfg.hostName });

  if (req.method === 'POST' && p === '/api/login') {
    let b; try { b = JSON.parse((await readBody(req, 4096)).toString('utf8')); } catch (e) { return send(res, 400, { err: 'galat request' }); }
    const u = normUser(b.user), lf = loginFails.get(u) || { n: 0, until: 0 };
    if (Date.now() < lf.until) return send(res, 429, { err: 'Bahut galat password. ' + Math.ceil((lf.until - Date.now()) / 60000) + ' minute baad try karein.' });
    if (!u || !checkPass(u, b.pass || '')) {
      lf.n++; if (lf.n >= 5) { lf.n = 0; lf.until = Date.now() + 10 * 60000; log('🔒 ' + u + ': 5 galat password — 10 minute band'); }
      loginFails.set(u, lf);
      log('⚠️ Galat login: ' + (u || '(khaali)'));
      return send(res, 403, { err: 'Login ID ya password galat hai' });
    }
    loginFails.delete(u);
    const name = String(b.name || 'Device').replace(/[\r\n"<>]/g, '').slice(0, 40) || 'Device';
    // isi ID + isi device naam ka purana entry ho to wahi dobara (nayi line nahi banegi)
    let id = Object.keys(state.devices).find(k => state.devices[k].user === u && state.devices[k].name === name + ' [' + u + ']');
    const token = crypto.randomBytes(32).toString('hex');
    if (!id) id = crypto.randomBytes(6).toString('hex');
    const old = state.devices[id] || { created: Date.now() };
    const hashes = (old.tokenHashes || (old.tokenHash ? [old.tokenHash] : [])).concat(sha(token)).slice(-5);   // ek device ke 5 login (alag browser / link) saath chalein
    state.devices[id] = Object.assign(old, { name: name + ' [' + u + ']', user: u, tokenHash: undefined, tokenHashes: hashes, lastSeen: Date.now() });
    delete state.devices[id].tokenHash;
    save(true);
    log('🔑 Login: ' + u + ' (' + name + ')');
    return send(res, 200, { ok: true, token, deviceId: id, hostId: state.hostId, name: cfg.hostName, lan: lanUrls(), url: publicUrl, gh: GH_REPO, fb: FB, user: u });
  }

  if (req.method === 'POST' && p === '/api/pair') {
    if (Date.now() < pinLockUntil) return send(res, 429, { err: 'Bahut galat PIN. ' + Math.ceil((pinLockUntil - Date.now()) / 60000) + ' minute baad try karein.' });
    let b; try { b = JSON.parse((await readBody(req, 4096)).toString('utf8')); } catch (e) { return send(res, 400, { err: 'galat request' }); }
    if (String(b.pin || '') !== state.pin) {
      pinFails++;
      log('⚠️ Galat PIN (' + pinFails + '/5) — ' + String(b.name || '').slice(0, 30));
      if (pinFails >= 5) { pinFails = 0; pinLockUntil = Date.now() + 10 * 60000; state.pin = newPin(); save(true); log('🔒 5 galat PIN — 10 minute pairing band, naya PIN bana'); }
      return send(res, 403, { err: 'PIN galat hai' });
    }
    pinFails = 0;
    const id = crypto.randomBytes(6).toString('hex'), token = crypto.randomBytes(32).toString('hex');
    const name = String(b.name || 'Naya device').replace(/[\r\n"<>]/g, '').slice(0, 40) || 'Naya device';
    state.devices[id] = { name, tokenHash: sha(token), perms: Object.assign({}, state.defaults), created: Date.now(), lastSeen: Date.now() };
    state.pin = newPin(); save(true);
    log('🔗 Naya device juda: ' + name + ' (Print/Scan/Remote: ' + KINDS.map(k => state.defaults[k]).join('/') + ')');
    return send(res, 200, { ok: true, token, deviceId: id, hostId: state.hostId, name: cfg.hostName, lan: lanUrls(), url: publicUrl, gh: GH_REPO, fb: FB });
  }

  // PC client setup (is PC ke har software me Host ke printer) — Host ke pate pehle se bhare hue
  if (req.method === 'GET' && p === '/MeraPrint-Client-Setup.bat') {
    try {
      const head = fs.readFileSync(path.join(TOP, 'client-pc', 'setup-header.bat'), 'utf8');
      const cs = fs.readFileSync(path.join(TOP, 'client-pc', 'MeraPrintClient.cs'), 'utf8');
      const addrs = lanUrls().concat(publicUrl ? [publicUrl] : []).join(',');
      const bat = (head.replace('__HOSTS__', state.hostId + '|' + addrs + '|' + (FB || '') + '|' + GH_REPO) + cs).replace(/\r?\n/g, '\r\n');
      res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename="MeraPrint-Client-Setup.bat"', 'Cache-Control': 'no-store' });
      return res.end(bat);
    } catch (e) { return send(res, 404, { err: 'client-pc folder nahi mila' }); }
  }
  const tm = /^\/dl\/([a-f0-9]{48})$/.exec(p);
  if (tm && req.method === 'GET') {
    const t = tickets.get(tm[1]);
    if (!t || t.until < Date.now() || !fs.existsSync(t.file)) return send(res, 404, { err: 'Download ka samay khatam — dobara dabaiye' });
    const st = fs.statSync(t.file);
    res.writeHead(200, { 'Content-Type': MIME[path.extname(t.file).toLowerCase()] || 'application/octet-stream', 'Content-Length': st.size,
      'Content-Disposition': "attachment; filename*=UTF-8''" + encodeURIComponent(t.name), 'Access-Control-Allow-Origin': '*', 'Cache-Control': 'no-store' });
    fs.createReadStream(t.file).pipe(res);
    return;
  }
  if (!p.startsWith('/api/')) return send(res, 404, { err: 'kya?' });
  const dev = deviceByToken(req.headers['x-token']);
  if (!dev) return send(res, 401, { err: 'Ye device is PC se juda nahi hai (ya hata diya gaya). Dobara jodiye.', unpair: true });
  touch(dev);
  const myPerms = permsOf(dev.id);

  if (req.method === 'GET' && p === '/api/me') return send(res, 200, { name: dev.name, host: cfg.hostName, hostId: state.hostId, perms: myPerms, lan: lanUrls(), url: publicUrl, gh: GH_REPO, fb: FB });
  if (req.method === 'GET' && p === '/api/client') return send(res, 200, { ver: clientVer() });
  if (req.method === 'POST' && p === '/api/unpair') { delete state.devices[dev.id]; save(true); kickRemote(dev.id); log('✖ ' + dev.name + ' ne khud ko hataya'); return send(res, 200, { ok: true }); }

  // MeraPrint APK ka apna update (MeraCall se bilkul alag): BUILD-APK.bat ki banayi out\MeraPrint.apk
  if (req.method === 'GET' && p === '/api/app') {
    const info = readJson(path.join(TOP, 'out', 'MeraPrint.json')), apk = path.join(TOP, 'out', 'MeraPrint.apk');
    if (!info || !fs.existsSync(apk)) return send(res, 200, { versionCode: 0 });
    const t = crypto.randomBytes(24).toString('hex');
    tickets.set(t, { file: apk, name: 'MeraPrint.apk', until: Date.now() + 30 * 60000 });
    return send(res, 200, { versionCode: info.versionCode, versionName: info.versionName, note: info.note || '', size: fs.statSync(apk).size, ticket: t });
  }
  if (req.method === 'GET' && p === '/api/printers') {
    if (myPerms.print === 'deny') return send(res, 403, { err: 'Is device ko print ki ijazat nahi hai' });
    return send(res, 200, await listPrinters());
  }
  if (req.method === 'PUT' && p === '/api/print') {
    if (myPerms.print === 'deny') return send(res, 403, { err: 'Is device ko print ki ijazat nahi hai' });
    let name = 'file', opts = {};
    try { name = decodeURIComponent(String(req.headers['x-name'] || 'file')).replace(/[\\/:*?"<>|\r\n]/g, '_').slice(0, 120); } catch (e) {}
    try { opts = JSON.parse(decodeURIComponent(String(req.headers['x-opts'] || '{}'))) || {}; } catch (e) {}
    const printers = await listPrinters();
    const pr = printers.find(x => x.name === opts.printer) || printers.find(x => x.isDefault) || printers[0];
    if (!pr) return send(res, 400, { err: 'Is PC par koi printer nahi mila' });
    let ext = path.extname(name).toLowerCase(); if (!/^\.[a-z0-9]{1,6}$/.test(ext)) ext = '.bin';
    const j = newJob(dev, 'print');
    j.name = name; j.printer = pr.name; j.opts = opts; j.file = path.join(JOBS, j.id + ext);
    try { j.size = await saveBody(req, j.file, cfg.maxUploadMB * 1048576); }
    catch (e) { jobs.delete(j.id); return send(res, 413, { err: 'File bahut badi hai (' + cfg.maxUploadMB + ' MB tak)' }); }
    log('📥 Print aaya: ' + name + ' (' + (j.size / 1048576).toFixed(1) + ' MB) — ' + dev.name);
    j.msg = myPerms.print === 'ask' ? 'Host PC par "Allow" ka intezaar...' : '';
    send(res, 200, { ok: true, job: j.id });
    const copies = Math.max(1, parseInt(opts.copies, 10) || 1);
    const ok = await approve(dev, 'print', 'File: ' + name + '\nPrinter: ' + pr.name + ' | ' + copies + ' copy' + (opts.pages ? ' | Pages: ' + String(opts.pages).slice(0, 30) : ''));
    if (!ok) { j.state = 'denied'; j.msg = '⛔ Host PC ne print ki ijazat nahi di'; cleanJob(j); return; }
    j.state = 'printing';
    runPrint(j).catch(e => fail(j, e.message));
    return;
  }

  if (req.method === 'GET' && p === '/api/scanners') {
    if (myPerms.scan === 'deny') return send(res, 403, { err: 'Is device ko scan ki ijazat nahi hai' });
    return send(res, 200, await listScanners());
  }
  if (req.method === 'POST' && p === '/api/scan') {
    if (myPerms.scan === 'deny') return send(res, 403, { err: 'Is device ko scan ki ijazat nahi hai' });
    let b; try { b = JSON.parse((await readBody(req, 4096)).toString('utf8')); } catch (e) { return send(res, 400, { err: 'galat request' }); }
    const list = await listScanners();
    const sc = list.find(x => x.id === b.scanner) || list[0];
    if (!sc) return send(res, 400, { err: 'Is PC par koi scanner nahi mila' });
    const j = newJob(dev, 'scan');
    j.scanner = sc.id; j.name = 'Scan_' + new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14) + '.jpg';
    j.dpi = [100, 150, 200, 300, 600].includes(+b.dpi) ? +b.dpi : 200;
    j.mode = ['color', 'gray', 'bw'].includes(b.mode) ? b.mode : 'color';
    j.msg = myPerms.scan === 'ask' ? 'Host PC par "Allow" ka intezaar...' : '';
    send(res, 200, { ok: true, job: j.id });
    const ok = await approve(dev, 'scan', 'Scanner: ' + sc.name + ' | ' + j.dpi + ' DPI');
    if (!ok) { j.state = 'denied'; j.msg = '⛔ Host PC ne scan ki ijazat nahi di'; return; }
    runScan(j).catch(e => fail(j, e.message));
    return;
  }

  let m = /^\/api\/job\/([a-f0-9]{18})(\/file)?$/.exec(p);
  if (m && req.method === 'GET') {
    const j = jobs.get(m[1]);
    if (!j || j.deviceId !== dev.id) return send(res, 404, { err: 'nahi mila' });
    if (!m[2]) return send(res, 200, { state: j.state, msg: j.msg, name: j.name, size: j.size, ticket: j.ticket });
    if (!j.result || !fs.existsSync(j.result)) return send(res, 404, { err: 'file nahi hai' });
    return send(res, 200, fs.readFileSync(j.result), 'image/jpeg');
  }

  // ---------- 📤 File bhejna (client -> is PC) ----------
  if (req.method === 'PUT' && p === '/api/send') {
    if (myPerms.send === 'deny') return send(res, 403, { err: 'Is device ko file bhejne ki ijazat nahi hai' });
    let name = 'file';
    try { name = decodeURIComponent(String(req.headers['x-name'] || 'file')).replace(/[\\/:*?"<>|\r\n]/g, '_').replace(/^\.+/, '').slice(0, 150) || 'file'; } catch (e) {}
    const j = newJob(dev, 'send');
    j.name = name; j.file = path.join(JOBS, j.id + '.part');
    try { j.size = await saveBody(req, j.file, cfg.maxUploadMB * 1048576 * 4); }
    catch (e) { jobs.delete(j.id); return send(res, 413, { err: 'File bahut badi hai (' + cfg.maxUploadMB * 4 + ' MB tak)' }); }
    j.msg = myPerms.send === 'ask' ? 'Host PC par "Allow" ka intezaar...' : '';
    send(res, 200, { ok: true, job: j.id });
    const ok = await approve(dev, 'send', 'File: ' + name + ' (' + (j.size / 1048576).toFixed(1) + ' MB)');
    if (!ok) { fs.rm(j.file, { force: true }, () => {}); j.state = 'denied'; j.msg = '⛔ Host PC ne file lene ki ijazat nahi di'; return; }
    const dir = path.join(RECV, dev.name.replace(/[\\/:*?"<>|]/g, '_'));
    fs.mkdirSync(dir, { recursive: true });
    let dest = path.join(dir, name), n = 1;
    const ext = path.extname(name), base = name.slice(0, name.length - ext.length);
    while (fs.existsSync(dest)) dest = path.join(dir, base + ' (' + (n++) + ')' + ext);
    try { fs.renameSync(j.file, dest); } catch (e) { fs.copyFileSync(j.file, dest); fs.rmSync(j.file, { force: true }); }
    j.file = null;
    j.state = 'done'; j.msg = '✅ PC par pahunch gayi: ' + path.relative(SHARE, dest);
    log('📤 File aayi: ' + name + ' (' + (j.size / 1048576).toFixed(1) + ' MB) — ' + dev.name + ' -> ' + dest);
    return;
  }

  // ---------- 📥 Download (is PC ke share folder se) ----------
  if (req.method === 'GET' && p === '/api/files') {
    if (myPerms.download === 'deny') return send(res, 403, { err: 'Is device ko download ki ijazat nahi hai' });
    const dir = inShare(u.searchParams.get('path'));
    if (!dir || !fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) return send(res, 404, { err: 'Folder nahi mila' });
    const items = [];
    for (const n of fs.readdirSync(dir)) {
      try { const st = fs.statSync(path.join(dir, n)); items.push({ name: n, dir: st.isDirectory(), size: st.isDirectory() ? 0 : st.size, mtime: st.mtimeMs }); } catch (e) {}
    }
    items.sort((a, b) => (b.dir - a.dir) || a.name.localeCompare(b.name));
    return send(res, 200, { path: path.relative(SHARE, dir).split(path.sep).join('/'), items, share: SHARE });
  }
  if (req.method === 'POST' && p === '/api/download') {
    if (myPerms.download === 'deny') return send(res, 403, { err: 'Is device ko download ki ijazat nahi hai' });
    let b; try { b = JSON.parse((await readBody(req, 4096)).toString('utf8')); } catch (e) { return send(res, 400, { err: 'galat request' }); }
    const f = inShare(b.path);
    if (!f || !fs.existsSync(f) || !fs.statSync(f).isFile()) return send(res, 404, { err: 'File nahi mili' });
    const j = newJob(dev, 'download');
    j.name = path.basename(f); j.size = fs.statSync(f).size;
    j.msg = myPerms.download === 'ask' ? 'Host PC par "Allow" ka intezaar...' : '';
    send(res, 200, { ok: true, job: j.id });
    const ok = await approve(dev, 'download', 'File: ' + j.name + ' (' + (j.size / 1048576).toFixed(1) + ' MB)');
    if (!ok) { j.state = 'denied'; j.msg = '⛔ Host PC ne download ki ijazat nahi di'; return; }
    const t = crypto.randomBytes(24).toString('hex');
    tickets.set(t, { file: f, name: j.name, until: Date.now() + 30 * 60000 });
    j.ticket = t; j.state = 'done'; j.msg = '✅ Download taiyaar';
    log('📥 Download diya: ' + j.name + ' — ' + dev.name);
    return;
  }
  return send(res, 404, { err: 'kya?' });
}

const server = http.createServer((req, res) => {
  handle(req, res).catch(e => { log('✖', e.message); try { send(res, 500, { err: 'Host me gadbad: ' + e.message }); } catch (x) {} });
});
server.on('upgrade', (req, socket) => {
  const key = req.headers['sec-websocket-key'];
  if (new URL(req.url, 'http://x').pathname !== '/ws' || !key) return socket.destroy();
  const acc = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + acc + '\r\n\r\n');
  remoteSession(new WS(socket), req);
});

// ---------------- Admin panel (sirf isi PC par: 127.0.0.1) ----------------
function sendA(res, code, body, type) {   // admin: koi CORS nahi — doosri website ise padh na sake
  res.writeHead(code, { 'Content-Type': type || 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Frame-Options': 'DENY' });
  res.end(type ? body : JSON.stringify(body));
}
async function admin(req, res) {
  const send = sendA;
  const u = new URL(req.url, 'http://x'), p = u.pathname;
  const origin = req.headers.origin;
  if (origin && !/^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin)) return send(res, 403, { err: 'mana' });
  // dusri website is panel ko na chhed sake
  const host = String(req.headers.host || '');
  if (!/^(localhost|127\.0\.0\.1)(:\d+)?$/.test(host)) return send(res, 403, { err: 'mana' });
  if (req.method === 'GET' && (p === '/' || p === '/index.html')) return send(res, 200, fs.readFileSync(path.join(ROOT, 'admin.html')), 'text/html; charset=utf-8');
  if (req.method === 'GET' && p === '/a/state') {
    return send(res, 200, {
      hostId: state.hostId, name: cfg.hostName, pin: state.pin, pinLocked: Date.now() < pinLockUntil,
      lan: lanUrls(), url: publicUrl, firebase: !!(FB && FB_SECRET), helper: !!helper.proc, share: SHARE,
      github: GH_REPO && GH_TOKEN ? (ghOk ? 'ok' : 'wait') : '', goLink: GO_LINK ? GO_LINK + state.hostId : '',
      fbOk, owner: OWNER, ver: APP_VER, update: updState,
      defaults: state.defaults,
      devices: Object.entries(state.devices).map(([id, d]) => ({ id, name: d.name, user: d.user || '', perms: permsOf(id), created: d.created, lastSeen: d.lastSeen, remote: [...sessions].some(s => s.ok && s.dev.id === id) })),
      users: Object.entries(state.users).map(([u, x]) => ({ user: u, perms: Object.assign({}, state.defaults, x.perms), devices: Object.values(state.devices).filter(d => d.user === u).length,
        lastSeen: Math.max(0, ...Object.values(state.devices).filter(d => d.user === u).map(d => d.lastSeen || 0)) })),
      pending: [...pending.values()].map(x => ({ id: x.id, text: x.text, kind: x.kind, deviceName: x.deviceName, ts: x.ts })),
      jobs: [...jobs.values()].slice(-15).reverse().map(j => ({ kind: j.kind, name: j.name, device: j.deviceName, state: j.state, msg: j.msg, ts: j.ts })),
      log: logs.slice(-60)
    });
  }
  if (req.method !== 'POST') return send(res, 404, { err: 'kya?' });
  if (!/^application\/json/.test(String(req.headers['content-type'] || ''))) return send(res, 415, { err: 'json' });
  let b; try { b = JSON.parse((await readBody(req, 8192)).toString('utf8')); } catch (e) { return send(res, 400, { err: 'json' }); }
  const d = b.id && state.devices[b.id];
  switch (p) {
    case '/a/perm':
      if (!KINDS.includes(b.kind) || !PERMS.includes(b.value)) return send(res, 400, { err: 'galat' });
      if (b.id === 'defaults') state.defaults[b.kind] = b.value;
      else if (String(b.id).indexOf('user:') === 0) {
        const u = state.users[String(b.id).slice(5)]; if (!u) return send(res, 404, { err: 'user nahi' });
        u.perms = Object.assign({}, state.defaults, u.perms); u.perms[b.kind] = b.value;
        if (b.kind === 'remote' && b.value === 'deny') for (const k in state.devices) if (state.devices[k].user === String(b.id).slice(5)) kickRemote(k);
        save(true); log('⚙️ Login ' + String(b.id).slice(5) + ' — ' + b.kind + ' = ' + b.value);
        return send(res, 200, { ok: true });
      }
      else if (d) { d.perms = Object.assign({}, state.defaults, d.perms); d.perms[b.kind] = b.value; if (b.kind === 'remote' && b.value === 'deny') kickRemote(b.id); }
      else return send(res, 404, { err: 'device nahi' });
      save(true); log('⚙️ ' + (d ? d.name : 'Naye device') + ' — ' + b.kind + ' = ' + b.value);
      return send(res, 200, { ok: true });
    case '/a/user-add': case '/a/user-pass': {
      const u = normUser(b.user);
      if (!u || u.length < 3) return send(res, 400, { err: 'Login ID kam se kam 3 akshar (a-z, 0-9)' });
      if (String(b.pass || '').length < 6) return send(res, 400, { err: 'Password kam se kam 6 akshar' });
      if (p === '/a/user-add' && state.users[u]) return send(res, 400, { err: 'Ye Login ID pehle se hai' });
      if (p === '/a/user-pass' && !state.users[u]) return send(res, 404, { err: 'user nahi' });
      const salt = crypto.randomBytes(16).toString('hex');
      state.users[u] = Object.assign(state.users[u] || { perms: Object.assign({}, state.defaults), created: Date.now() }, { salt, passHash: passHash(b.pass, salt) });
      if (p === '/a/user-pass') for (const k in state.devices) if (state.devices[k].user === u) { kickRemote(k); delete state.devices[k]; }   // naya password = purane login band
      save(true); log((p === '/a/user-add' ? '👤 Naya login: ' : '🔁 Password badla: ') + u);
      return send(res, 200, { ok: true, user: u });
    }
    case '/a/user-del': {
      const u = normUser(b.user); if (!state.users[u]) return send(res, 404, { err: 'user nahi' });
      for (const k in state.devices) if (state.devices[k].user === u) { kickRemote(k); delete state.devices[k]; }
      delete state.users[u]; save(true); log('🗑️ Login hataya: ' + u);
      return send(res, 200, { ok: true });
    }
    case '/a/rename':
      if (!d) return send(res, 404, { err: 'device nahi' });
      d.name = String(b.name || '').replace(/[\r\n"<>]/g, '').slice(0, 40) || d.name; save(true);
      return send(res, 200, { ok: true });
    case '/a/remove':
      if (!d) return send(res, 404, { err: 'device nahi' });
      log('🗑️ Device hataya: ' + d.name); kickRemote(b.id); delete state.devices[b.id]; save(true);
      return send(res, 200, { ok: true });
    case '/a/answer': { const x = pending.get(String(b.id)); if (x) x.finish(!!b.ok); return send(res, 200, { ok: true }); }
    case '/a/newpin': state.pin = newPin(); pinLockUntil = 0; pinFails = 0; save(true); return send(res, 200, { ok: true });
    case '/a/kick': kickRemote(b.id || undefined); return send(res, 200, { ok: true });
    case '/a/hostname':
      cfg.hostName = String(b.name || '').replace(/[\r\n"<>]/g, '').slice(0, 40) || cfg.hostName;
      try { const c = readJson(CFG_PATH) || {}; c.hostName = cfg.hostName; fs.writeFileSync(CFG_PATH, JSON.stringify(c, null, 2)); } catch (e) {}
      announce(); return send(res, 200, { ok: true });
  }
  return send(res, 404, { err: 'kya?' });
}
const adminServer = http.createServer((req, res) => { admin(req, res).catch(e => { try { sendA(res, 500, { err: e.message }); } catch (x) {} }); });

// ---------------- Internet: Cloudflare tunnel + Firebase me pata ----------------
let publicUrl = '';
let ghLast = '', ghOk = false;
const ghApi = p => 'https://api.github.com/repos/' + GH_REPO + '/contents/' + p;
const ghHead = () => ({ Authorization: 'Bearer ' + GH_TOKEN, 'User-Agent': 'MeraPrint-Host', Accept: 'application/vnd.github+json' });
async function ghPut(p, text, msg) {
  let sha;
  const g = await fetch(ghApi(p), { headers: ghHead() });
  if (g.status === 200) { const j = await g.json(); sha = j.sha; if (j.content && Buffer.from(j.content, 'base64').toString('utf8') === text) return true; }
  const r = await fetch(ghApi(p), { method: 'PUT', headers: Object.assign({ 'Content-Type': 'application/json' }, ghHead()),
    body: JSON.stringify({ message: msg, content: Buffer.from(text).toString('base64'), sha }) });
  if (!r.ok) throw new Error('GitHub ' + r.status + ' ' + (await r.text()).slice(0, 120));
  return true;
}
async function announceGh() {
  if (!GH_REPO || !GH_TOKEN || !publicUrl) return;
  const key = publicUrl + '|' + lanUrls().join(',');
  if (key === ghLast) return;
  try {
    await ghPut('meraprint/' + state.hostId + '.json', JSON.stringify({ name: cfg.hostName, url: publicUrl, lan: lanUrls(), ts: Date.now() }), 'MeraPrint: ' + cfg.hostName + ' ka naya pata');
    ghLast = key; ghOk = true;
    log('🔗 Naya pata GitHub par likha — client apne aap judenge. Pakka link: ' + GO_LINK + state.hostId);
  } catch (e) { ghOk = false; log('✖ GitHub par pata nahi likh paye: ' + e.message); }
}
async function ghGoPage() {
  if (!GH_REPO || !GH_TOKEN) return;
  try { await ghPut('meraprint/index.html', fs.readFileSync(path.join(TOP, 'web', 'go.html'), 'utf8').replace("'__FB_URL__'", JSON.stringify(FB)), 'MeraPrint: pakka link page'); }
  catch (e) { log('✖ GitHub go-page:', e.message); }
}
let fbFailAt = 0, fbOk = false;
async function announce() {
  announceGh();
  if (!FB) return;
  const pub = { name: cfg.hostName.slice(0, 60), url: publicUrl, lan: lanUrls(), ts: Date.now() };
  try {
    // maalik: secret se.  Baaki Host: bina secret, apni chabi (fbKey) ke saath — rules sirf usi chabi wale ko badalne dete hain
    const r = FB_SECRET
      ? await fetch(FB + 'printHosts/' + state.hostId + '.json?auth=' + encodeURIComponent(FB_SECRET), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pub }) })
      : await fetch(FB + 'printHosts/' + state.hostId + '.json', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ key: state.fbKey, pub }) });
    if (!r.ok) { const e = new Error('HTTP ' + r.status + (r.status === 401 ? ' (Firebase rules me printHosts nahi laga)' : '')); e.http = true; throw e; }
    fbOk = true;
    if (fbFailAt) { log('✅ Firebase phir se chalu'); fbFailAt = 0; }
  } catch (e) {
    fbOk = false;
    if (!fbFailAt || Date.now() - fbFailAt > 30 * 60000) {
      const why = (e.cause && (e.cause.code || e.cause.message)) || e.message;
      log('ℹ️ Firebase me pata nahi likha (' + why + ')' + (OWNER ? ' — koi dikkat nahi, GitHub se kaam chal raha hai' : ' — internet link badalne par client khud nahi judenge'));
      fbFailAt = Date.now();
    }
  }
}
// ---------------- Apne aap update (maalik ke GitHub se, signature jaanch kar) ----------------
let updState = OWNER ? 'Ye maalik ka Host hai — update yahin se nikalta hai (PUBLISH-HOST-UPDATE.bat)' : 'abhi jaancha nahi';
const UPD_SKIP = new Set(['START-HOST.bat']);            // chalti .bat ko badalna khatarnak — installer hi deta hai
const UPD_OK = /^(host\/[\w.-]+|web\/[\w.-]+|client-pc\/[\w.-]+|out\/MeraPrint\.(apk|json)|README\.md|ADD-TO-STARTUP\.bat|public\.json|update-public\.pem|START-HOST\.bat)$/;
const sha256 = b => crypto.createHash('sha256').update(b).digest('hex');
async function ghRaw(p, binary) {
  const r = await fetch('https://raw.githubusercontent.com/' + GH_REPO + '/HEAD/' + p + '?t=' + Date.now(), { signal: AbortSignal.timeout(60000), cache: 'no-store' });
  if (!r.ok) throw new Error(p + ': HTTP ' + r.status);
  return binary ? Buffer.from(await r.arrayBuffer()) : await r.text();
}
let updating = false;
async function checkUpdate() {
  if (OWNER || !GH_REPO || updating) return;
  const pemFile = path.join(TOP, 'update-public.pem');
  if (!fs.existsSync(pemFile)) { updState = 'update-public.pem nahi mila — apne aap update band'; return; }
  updating = true;
  try {
    const text = await ghRaw('meraprint/app/manifest.json');
    const sig = Buffer.from((await ghRaw('meraprint/app/manifest.sig')).trim(), 'base64');
    if (!crypto.verify('sha256', Buffer.from(text, 'utf8'), fs.readFileSync(pemFile, 'utf8'), sig)) throw new Error('signature galat — update NAHI lagaya');
    const m = JSON.parse(text);
    if (!(m.version > APP_VER)) { updState = '✅ Naya hai (version ' + APP_VER + ')'; return; }
    log('⬇️ Naya update mila: version ' + m.version + (m.note ? ' — ' + m.note : ''));
    const stage = path.join(TOP, '.update');
    fs.rmSync(stage, { recursive: true, force: true });
    const todo = [];
    for (const f of m.files) {
      if (!UPD_OK.test(f.path) || UPD_SKIP.has(f.path)) continue;
      const dest = path.join(TOP, f.path);
      if (fs.existsSync(dest) && sha256(fs.readFileSync(dest)) === f.sha256) continue;
      const b = await ghRaw('meraprint/app/files/' + f.path, true);
      if (sha256(b) !== f.sha256) throw new Error(f.path + ' ka hash nahi mila (GitHub abhi purani file de raha hai) — thodi der baad dobara');
      const st = path.join(stage, f.path);
      fs.mkdirSync(path.dirname(st), { recursive: true });
      fs.writeFileSync(st, b);
      todo.push(f.path);
    }
    for (const fp of todo) { fs.mkdirSync(path.dirname(path.join(TOP, fp)), { recursive: true }); fs.copyFileSync(path.join(stage, fp), path.join(TOP, fp)); }
    fs.writeFileSync(path.join(TOP, 'app-version.json'), JSON.stringify({ version: m.version, date: m.date || '' }, null, 1));
    fs.rmSync(stage, { recursive: true, force: true });
    log('✅ Update laga (version ' + m.version + ', ' + todo.length + ' file). Host 5 second me dobara chalu hoga...');
    updState = 'version ' + m.version + ' laga — dobara chalu ho raha hai';
    setTimeout(() => { helper.cmd('QUIT'); process.exit(99); }, 5000);
  } catch (e) {
    updState = '✖ ' + e.message;
    log('✖ Update nahi laga: ' + e.message);
  } finally { updating = false; }
}
// PC client (MeraPrintClient) ka version — client khud milaa kar update leta hai
function clientVer() {
  try { return sha256(fs.readFileSync(path.join(TOP, 'client-pc', 'MeraPrintClient.cs'), 'utf8').replace(/^\uFEFF/, '').replace(/\r\n/g, '\n')); } catch (e) { return ''; }
}

let tunnelProc = null, tunnelFails = 0;
// Cloudflare ka link kabhi chalte-chalte mar jata hai (cloudflared chalta rehta hai par link band) -> khud jaancho, mara ho to naya lo
async function checkTunnel() {
  if (!publicUrl || !tunnelProc) return;
  try {
    const r = await fetch(publicUrl + '/api/ping', { signal: AbortSignal.timeout(20000), cache: 'no-store' });
    const j = await r.json();
    if (j && j.hostId === state.hostId) { tunnelFails = 0; return; }
  } catch (e) {}
  tunnelFails++;
  log('⚠️ Internet link jawab nahi de raha (' + tunnelFails + '/2): ' + publicUrl);
  if (tunnelFails >= 2) {
    tunnelFails = 0;
    log('🔄 Internet link mar gaya — naya link le rahe hain...');
    try { tunnelProc.kill(); } catch (e) {}
  }
}
let lastLan = '';
setInterval(() => {   // PC ka LAN pata badla (WiFi / router restart) -> turant sab ko batao
  const l = lanUrls().join(',');
  if (lastLan && l !== lastLan) { log('📶 LAN pata badla: ' + l); announce(); setTimeout(checkTunnel, 15000); }
  lastLan = l;
}, 30000).unref();
function startTunnel() {
  if (!cfg.internet) { log('🌐 Internet band (host-config.json: internet=false) — sirf LAN'); return; }
  const exe = [path.join(TOOLS, 'cloudflared.exe'), path.join(path.dirname(cfg.meracallConfig || ''), 'pc-data', 'cloudflared.exe')].find(f => fs.existsSync(f));
  if (!exe) { log('✖ cloudflared.exe nahi mila — abhi sirf LAN (same WiFi) se chalega. START-HOST.bat dobara chalaiye.'); return; }
  const t = spawn(exe, ['tunnel', '--url', 'http://localhost:' + cfg.port, '--no-autoupdate'], { windowsHide: true });
  tunnelProc = t; tunnelFails = 0;
  const onOut = d => {
    const m = /https:\/\/[a-z0-9-]+\.trycloudflare\.com/.exec(String(d));
    if (m && m[0] !== publicUrl) { publicUrl = m[0]; log('🌐 Internet par chalu: ' + publicUrl); announce(); }
  };
  t.stdout.on('data', onOut); t.stderr.on('data', onOut);
  t.on('exit', code => { if (tunnelProc === t) tunnelProc = null; log('✖ Tunnel band hua (' + code + '), 10 sec me dobara...'); publicUrl = ''; announce(); setTimeout(startTunnel, 10000); });
  process.on('exit', () => { try { t.kill(); } catch (e) {} });
}

// ---------------- Start ----------------
if (require.main === module) {
  server.listen(cfg.port, '0.0.0.0', () => {
    console.log('');
    console.log('  ==============================================');
    console.log('   MeraPrint Host chalu:  ' + cfg.hostName);
    console.log('   Host ID :  ' + state.hostId);
    console.log('   PIN     :  ' + state.pin + '   (naya device jodne ke liye)');
    for (const l of lanUrls()) console.log('   LAN     :  ' + l);
    console.log('   Admin   :  http://localhost:' + cfg.adminPort + '   (ijazat yahan set karein)');
    if (GO_LINK) console.log('   Pakka link (kabhi nahi badlega): ' + GO_LINK + state.hostId);
    console.log('   Version :  ' + APP_VER + (OWNER ? '  (maalik ka Host)' : '  (apne aap update)'));
    console.log('  ==============================================');
    console.log('');
    if (!FB || !FB_SECRET) log('ℹ️ MeraCall config me Firebase nahi mila — bahar se jodne ke liye client me Internet URL daalna padega.');
    helper.start();
    startTunnel();
    setInterval(announce, 60000); announce(); ghGoPage();
    setInterval(checkTunnel, 120000);
    setTimeout(checkUpdate, 60000); setInterval(checkUpdate, 30 * 60000);
    listPrinters().then(l => log('🖨️ Printers: ' + (l.map(x => x.name + (x.isDefault ? ' (default)' : '')).join(', ') || 'koi nahi')));
  });
  server.on('error', e => { log('✖ Port ' + cfg.port + ': ' + e.message + ' (kya Host pehle se chal raha hai?)'); process.exit(1); });
  adminServer.listen(cfg.adminPort, '127.0.0.1');
  const bye = () => { helper.cmd('QUIT'); process.exit(0); };
  process.on('SIGINT', bye); process.on('SIGTERM', bye);
}
module.exports = { server, WS, sumatraSettings, checkUpdate, clientVer };
