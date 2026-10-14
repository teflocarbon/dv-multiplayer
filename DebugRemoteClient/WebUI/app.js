const events = [];
let paused = false;
let renderQueued = false;
let pinnedSequence = null;
let tracePacketType = null;
const suppressedPacketTypes = new Set();
const packetTypes = new Map();
const $ = id => document.getElementById(id);
const controls = [$("search"), $("packet-filter"), $("direction-filter"), $("status-filter")];
const list = $("events"), template = $("event-template"), typeFilter = $("packet-filter");
const traceButton = $("trace-mode");
const suppressionList = $("suppression-list"), suppressionSearch = $("suppression-search"), suppressionSummary = $("suppression-summary");
const inspector = $("inspector"), inspectorTitle = $("inspector-title"), inspectorMeta = $("inspector-meta"), inspectorDetail = $("inspector-detail"), inspectorRaw = $("inspector-raw");

function matches(event) {
  const term = $("search").value.trim().toLowerCase();
  const corpus = `${event.packetType} ${event.summary} ${event.status} ${event.direction}`.toLowerCase();
  return (!term || corpus.includes(term)) && (!tracePacketType || event.packetType === tracePacketType) && (!typeFilter.value || event.packetType === typeFilter.value) && (!$("direction-filter").value || event.direction === $("direction-filter").value) && (!$("status-filter").value || event.status === $("status-filter").value);
}
function render() {
  list.replaceChildren();
  const visible = events.filter(matches).slice(-500);
  for (const event of visible) {
    const node = template.content.cloneNode(true);
    const head = node.querySelector(".event-head"), time = node.querySelector("time");
    time.textContent = formatTime(event.timestamp);
    const direction = node.querySelector(".direction"); direction.textContent = event.direction; direction.classList.add(event.direction);
    node.querySelector(".type").textContent = event.packetType;
    node.querySelector(".summary").textContent = event.summary || "—";
    const status = node.querySelector(".status"); status.textContent = event.status; status.classList.add(event.status.toLowerCase());
    head.addEventListener("click", () => openInspector(event));
    list.append(node);
  }
  $("count").textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;
  $("filtered").textContent = tracePacketType ? `Trace mode: ${tracePacketType}` : visible.length === events.length ? "" : `${visible.length} shown`;
}
function add(event) {
  event = normalize(event);
  if (tracePacketType && event.packetType !== tracePacketType) return;
  events.push(event);
  if (events.length > 10000) events.splice(0, events.length - 10000);
  if (!paused && pinnedSequence === null) scheduleRender();
  else updateStats();
}
function scheduleRender() { if (renderQueued) return; renderQueued = true; setTimeout(() => { renderQueued = false; if (pinnedSequence === null) render(); else updateStats(); }, 100); }
function formatTime(timestamp) {
  const match = /T(\d{2}:\d{2}:\d{2}\.\d{3})/.exec(timestamp);
  return match ? match[1] : timestamp;
}
function formatDetail(detail) {
  if (!detail) return "No typed detail available.";
  try { return JSON.stringify(JSON.parse(detail), null, 2); }
  catch { return detail; }
}
function formatHex(hex) {
  if (!hex) return "No raw payload retained.";
  return hex.match(/.{1,95}/g).join("\n");
}
async function openInspector(event) {
  pinnedSequence = event.sequence;
  $("follow").hidden = false;
  inspector.hidden = false;
  inspectorTitle.textContent = event.packetType;
  inspectorMeta.textContent = `#${event.sequence} · ${event.direction} · ${event.status} · peer ${event.peerId} · channel ${event.channel} · ${event.delivery}`;
  inspectorDetail.textContent = formatDetail(event.detailPreview);
  inspectorRaw.textContent = formatHex(event.rawPreview);
  try {
    const full = normalize(await fetch(`/api/event/${event.sequence}`).then(response => response.ok ? response.json() : Promise.reject(new Error("Event has expired from the local buffer."))));
    if (pinnedSequence === event.sequence) {
      inspectorDetail.textContent = formatDetail(full.detail);
      inspectorRaw.textContent = formatHex(full.rawHex);
    }
  } catch (error) {
    if (pinnedSequence === event.sequence) inspectorDetail.textContent = error.message;
  }
}
controls.forEach(control => control.addEventListener("input", () => { pinnedSequence = null; inspector.hidden = true; $("follow").hidden = true; updateTraceButton(); render(); }));
function updateStats() {
  $("count").textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;
  $("filtered").textContent = tracePacketType ? `Trace mode: ${tracePacketType}` : pinnedSequence === null ? "" : `Pinned on #${pinnedSequence}; new events continue buffering`;
}
function updateTraceButton() {
  if (tracePacketType) {
    traceButton.disabled = false; traceButton.classList.add("active"); traceButton.textContent = `Stop trace: ${tracePacketType}`;
  } else {
    traceButton.classList.remove("active"); traceButton.disabled = !typeFilter.value; traceButton.textContent = "Trace selected type";
  }
}
traceButton.addEventListener("click", () => {
  if (tracePacketType) {
    tracePacketType = null;
    typeFilter.value = "";
  } else if (typeFilter.value) {
    tracePacketType = typeFilter.value;
    events.length = 0;
    pinnedSequence = null;
    inspector.hidden = true;
  }
  updateTraceButton(); render();
});
async function setSuppression(packetType, suppress) {
  try {
    const response = await fetch(`/api/suppression/${encodeURIComponent(packetType)}`, { method: suppress ? "PUT" : "DELETE" });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    if (suppress) suppressedPacketTypes.add(packetType); else suppressedPacketTypes.delete(packetType);
    renderSuppressionList();
  } catch (error) {
    console.error("Could not update capture suppression", error);
    $("connection").textContent = `Suppression update failed: ${error.message}`;
  }
}
function renderSuppressionList() {
  const search = suppressionSearch.value.trim().toLowerCase();
  suppressionList.replaceChildren();
  const entries = Array.from(packetTypes.values()).filter(packet => !search || `${packet.name} ${packet.category} ${packet.direction}`.toLowerCase().includes(search));
  for (const packet of entries) {
    const label = document.createElement("label"); label.className = "suppression-item";
    const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.checked = suppressedPacketTypes.has(packet.name);
    checkbox.addEventListener("change", () => setSuppression(packet.name, checkbox.checked));
    const name = document.createElement("span"); name.className = "suppression-name"; name.textContent = packet.name; name.title = packet.fullName || packet.name;
    const count = document.createElement("span"); count.className = "suppression-count"; count.textContent = packet.count.toLocaleString();
    const meta = document.createElement("span"); meta.className = "suppression-meta"; meta.textContent = `${packet.category || "Runtime"} Â· ${packet.direction}${packet.highFrequency ? " Â· high frequency" : ""}`; meta.title = packet.fullName || packet.name;
    label.append(checkbox, name, count, meta); suppressionList.append(label);
  }
  suppressionSummary.textContent = suppressedPacketTypes.size ? `Silence packet types (${suppressedPacketTypes.size})` : "Silence packet types";
}
function refreshPacketTypes() {
  return fetch("/api/packet-types").then(response => response.ok ? response.json() : Promise.reject(new Error(response.statusText))).then(state => {
    packetTypes.clear();
    for (const packet of state.packetTypes || []) {
      packetTypes.set(packet.name, packet);
      if (packet.suppressed) suppressedPacketTypes.add(packet.name); else suppressedPacketTypes.delete(packet.name);
    }
    // Keep the native filter control stable while its companion suppression menu refreshes.
    // Replacing a focused <select> every second closes its dropdown on some browsers.
    for (const packet of Array.from(packetTypes.values()).sort((a, b) => a.name.localeCompare(b.name))) {
      let option = Array.from(typeFilter.options).find(candidate => candidate.value === packet.name);
      if (!option) { option = new Option("", packet.name); typeFilter.add(option); }
      option.textContent = `${packet.name} (${packet.count.toLocaleString()})`;
    }
    updateTraceButton(); renderSuppressionList();
  }).catch(error => console.warn("Could not load packet catalog", error));
}
suppressionSearch.addEventListener("input", renderSuppressionList);
$("pause").addEventListener("click", event => { paused = !paused; event.target.textContent = paused ? "Resume" : "Pause"; if (!paused && pinnedSequence === null) scheduleRender(); });
$("follow").addEventListener("click", () => { pinnedSequence = null; inspector.hidden = true; $("follow").hidden = true; scheduleRender(); });
$("close-inspector").addEventListener("click", () => { pinnedSequence = null; inspector.hidden = true; $("follow").hidden = true; scheduleRender(); });
$("clear").addEventListener("click", () => { events.length = 0; pinnedSequence = null; inspector.hidden = true; $("follow").hidden = true; render(); });
const stream = new EventSource("/events");
stream.onopen = () => { $("connection").textContent = "Live trace connected"; $("connection").classList.add("live"); };
stream.onerror = () => { $("connection").textContent = "Reconnecting…"; $("connection").classList.remove("live"); };
stream.onmessage = message => add(JSON.parse(message.data));
refreshPacketTypes();
setInterval(refreshPacketTypes, 1000);

// Accept traces from both current and older debug-client binaries. The server now emits
// camelCase, while early builds used the C# PascalCase property names.
function normalize(event) {
  const value = name => event[name] ?? event[name[0].toUpperCase() + name.slice(1)];
  return {
    sequence: value("sequence"), timestamp: value("timestamp"), direction: value("direction"),
    packetType: value("packetType"), status: value("status"), summary: value("summary"),
    detail: value("detail"), rawHex: value("rawHex"), detailPreview: value("detailPreview"), rawPreview: value("rawPreview"), peerId: value("peerId"),
    channel: value("channel"), delivery: value("delivery")
  };
}
