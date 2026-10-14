const events = [];
let paused = false, renderQueued = false, pinned = null, localSession = null, capturing = false;
const $ = id => document.getElementById(id);
const list = $("events"), template = $("event-template");
const filters = [$("search"), $("session-filter"), $("category-filter"), $("severity-filter")];

function packetType(event) { return event.data?.packetType || (event.entityType === "Packet" ? event.entityId : ""); }
function direction(event) { return event.data?.direction || event.runtimeSide || ""; }
function summary(event) { return event.data?.summary || event.data?.reason || event.data?.text || event.eventName; }
function matches(event) {
  const term = $("search").value.trim().toLowerCase();
  const corpus = `${event.sessionId} ${event.role} ${event.category} ${event.eventName} ${event.entityType} ${event.entityId} ${packetType(event)} ${summary(event)} ${JSON.stringify(event.data || {})}`.toLowerCase();
  return (!term || corpus.includes(term)) && (!$("session-filter").value || event.sessionId === $("session-filter").value) && (!$("category-filter").value || event.category === $("category-filter").value) && (!$("severity-filter").value || event.severity === $("severity-filter").value);
}
function addOption(select, value, label = value) {
  if (!value) return;
  select.__knownValues ||= new Set(Array.from(select.options, option => option.value));
  if (select.__knownValues.has(value)) return;
  select.__knownValues.add(value); select.add(new Option(label, value));
}
function add(event) {
  events.push(event); if (events.length > 50000) events.splice(0, events.length - 50000);
  addOption($("session-filter"), event.sessionId, `${event.role || "session"} · ${event.sessionId}`);
  addOption($("category-filter"), event.category);
  if (!paused && pinned === null) scheduleRender(); else updateStats();
}
function render() {
  list.replaceChildren(); const visible = events.filter(matches).slice(-1000);
  for (const event of visible) {
    const node = template.content.cloneNode(true), head = node.querySelector(".event-head");
    node.querySelector("time").textContent = new Date(event.timestampUtc).toISOString().substring(11, 23);
    node.querySelector(".session").textContent = event.role || event.sessionId?.slice(-8) || "—";
    const dir = node.querySelector(".direction"); dir.textContent = direction(event); dir.classList.add(String(direction(event)).toLowerCase());
    node.querySelector(".type").textContent = packetType(event) || `${event.entityType || event.category}${event.entityId ? ` ${event.entityId}` : ""}`;
    node.querySelector(".summary").textContent = `${event.eventName} · ${summary(event)}`;
    const status = node.querySelector(".status"); status.textContent = event.severity; status.classList.add(String(event.severity).toLowerCase());
    head.addEventListener("click", () => openInspector(event)); list.append(node);
  }
  updateStats(visible.length);
}
function scheduleRender() {
  if (renderQueued || document.hidden) return;
  renderQueued = true;
  setTimeout(() => {
    renderQueued = false;
    if (!document.hidden) render();
  }, 250);
}
function updateStats(visible) { $("count").textContent = `${events.length.toLocaleString()} events`; $("filtered").textContent = visible === undefined ? "Paused or pinned; events continue buffering" : `${visible.toLocaleString()} shown`; }
function openInspector(event) {
  pinned = event.sequence; $("inspector").hidden = false;
  $("inspector-title").textContent = `${event.category} · ${event.eventName}`;
  $("inspector-meta").textContent = `${event.sessionId}:${event.sequence} · ${event.runtimeSide} · ${event.entityType || ""} ${event.entityId || ""}`;
  $("inspector-detail").textContent = JSON.stringify(event, null, 2);
}
filters.forEach(filter => filter.addEventListener("input", () => { pinned = null; $("inspector").hidden = true; render(); }));
$("pause").addEventListener("click", event => { paused = !paused; event.target.textContent = paused ? "Resume" : "Pause"; if (!paused) render(); });
$("clear").addEventListener("click", () => { events.length = 0; pinned = null; $("inspector").hidden = true; render(); });
$("close-inspector").addEventListener("click", () => { pinned = null; $("inspector").hidden = true; render(); });
async function post(path, body) { return fetch(path, { method:"POST", headers:{"Content-Type":"application/json", "X-DVMP-Debug-Token":localSession?.apiToken || ""}, body:JSON.stringify(body || {}) }); }
$("mark").addEventListener("click", async () => { const text = prompt("Marker text"); if (text) await post("/api/mark", {text}); });
$("capture").addEventListener("click", async event => { if (!capturing) { const name = prompt("Capture name", "capture"); if (!name) return; await post("/api/capture/start", {name}); capturing = true; event.target.textContent = "Stop capture"; } else { await post("/api/capture/stop", {}); capturing = false; event.target.textContent = "Start capture"; } });
$("raw").addEventListener("click", async event => { const current = await fetch("/api/settings").then(response => response.json()); current.traceMode = current.traceMode === "Raw" ? "Summary" : "Raw"; current.rawPacketCapture = current.traceMode === "Raw"; await post("/api/settings", current); event.target.textContent = `Raw: ${current.rawPacketCapture ? "on" : "off"}`; });

async function refreshSessions() {
  try {
    const sessions = await fetch("/api/sessions").then(response => response.json()), root = $("sessions"); root.replaceChildren();
    for (const session of sessions) { const card = document.createElement("div"); card.className = "session-card"; card.textContent = `${session.role} — ${session.playerName || "unnamed"} — PID ${session.processId} — Build ${session.buildNumber || "unknown"} — ${session.sessionId}`; root.append(card); }
  } catch {}
}
fetch("/api/session").then(response => response.json()).then(session => { localSession = session; $("build").textContent = `Build ${session.buildNumber || "development"}`; refreshSessions(); setInterval(refreshSessions, 2000); });
const stream = new EventSource("/events");
stream.onopen = () => { $("connection").textContent = "Live event stream connected"; $("connection").classList.add("live"); };
stream.onerror = () => { $("connection").textContent = "Reconnecting…"; $("connection").classList.remove("live"); };
stream.onmessage = message => add(JSON.parse(message.data));

