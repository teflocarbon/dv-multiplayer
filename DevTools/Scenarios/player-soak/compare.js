// node compare.js host.json client.json: how far each peer's view of the other strays from where
// the other really was, allowing up to 1.2 s of interpolation lag. Reports sustained errors.
const fs = require('fs');
const v = f => { const o = JSON.parse(fs.readFileSync(f, 'utf8')); const x = Object.values(o.results)[0]; if (!x.ok) throw new Error(f + ': ' + JSON.stringify(x).slice(0, 400)); return x.result; };
const [h, c] = [v(process.argv[2]), v(process.argv[3])];
const MAX = +(process.argv[4] || 3), HOLD = +(process.argv[5] || 5000);
function check(name, truth, view, log) {
  let worst = 0, bad = [], runStart = null, runPeak = 0;
  for (const s of view) {
    let best = Infinity;
    for (const t of truth) { const dt = s[0] - t[0]; if (dt < -250 || dt > 1200) continue; best = Math.min(best, Math.hypot(s[1] - t[1], s[3] - t[3])); }
    if (best === Infinity) continue;
    worst = Math.max(worst, best);
    if (best > MAX) { if (runStart === null) { runStart = s[0]; runPeak = 0; } runPeak = Math.max(runPeak, best); }
    else { if (runStart !== null && s[0] - runStart >= HOLD) bad.push({ from: runStart, ms: s[0] - runStart, peak: runPeak }); runStart = null; }
  }
  const last = view[view.length - 1];
  if (runStart !== null && last && last[0] - runStart >= HOLD) bad.push({ from: runStart, ms: last[0] - runStart, peak: runPeak, open: true });
  const t0 = truth[0] ? truth[0][0] : 0;
  console.log(`   ${name}: ${view.length} samples, worst ${worst.toFixed(1)} m, ${bad.length} stretch(es) off by >${MAX} m for >${HOLD / 1000}s` +
    bad.map(b => `\n      from ${((b.from - t0) / 1000).toFixed(0)}s for ${(b.ms / 1000).toFixed(1)}s, peak ${b.peak.toFixed(1)} m${b.open ? ' (still off at the end)' : ''}`).join(''));
  return bad.length;
}
const fails = check("host's view of the client", c.own, h.seen) + check("client's view of the host", h.own, c.seen);
console.log('   client did: ' + c.log.join(', '));
console.log('   host did:   ' + h.log.join(', '));
if (fails) { console.log('FAIL'); process.exit(1); } console.log('PASS');
