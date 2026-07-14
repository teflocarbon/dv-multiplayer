const events = [];
let paused = false, renderQueued = false, pinned = null, localSession = null, capturing = false;
let replicationOperations = [], selectedReplicationId = null, selectedReplication = null, knownSessions = [], replicationVisible = false;
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
function addOption(select, value, label = value) { if (!value || Array.from(select.options).some(option => option.value === value)) return; select.add(new Option(label, value)); }
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
function scheduleRender() { if (renderQueued) return; renderQueued = true; setTimeout(() => { renderQueued = false; render(); }, 100); }
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
    const sessions = await fetch("/api/sessions").then(response => response.json()), root = $("sessions"); knownSessions = sessions; root.replaceChildren();
    for (const session of sessions) { const card = document.createElement("div"); card.className = "session-card"; card.textContent = `${session.role} — ${session.playerName || "unnamed"} — PID ${session.processId} — ${session.sessionId}`; root.append(card); }
  } catch {}
}
fetch("/api/session").then(response => response.json()).then(session => { localSession = session; refreshSessions(); setInterval(refreshSessions, 2000); });
const stream = new EventSource("/events");
stream.onopen = () => { $("connection").textContent = "Live event stream connected"; $("connection").classList.add("live"); };
stream.onerror = () => { $("connection").textContent = "Reconnecting…"; $("connection").classList.remove("live"); };
stream.onmessage = message => add(JSON.parse(message.data));

function showView(replication) {
  replicationVisible = replication;
  $("events-tab").classList.toggle("active", !replication); $("replication-tab").classList.toggle("active", replication);
  $("event-toolbar").hidden = replication; $("event-stats").hidden = replication; $("events").hidden = replication;
  $("inspector").hidden = true; $("replication-view").hidden = !replication;
  if (replication) refreshReplication(); else render();
}
$("events-tab").addEventListener("click", () => showView(false));
$("replication-tab").addEventListener("click", () => showView(true));

function sessionLabel(id) {
  const session = knownSessions.find(value => value.sessionId === id);
  return session ? `${session.role}${session.playerId == null ? "" : ` P${session.playerId}`} ${session.playerName || ""}`.trim() : id?.slice(-8) || "unknown";
}
function replicationMatches(operation) {
  const term = $("replication-search").value.trim().toLowerCase();
  const status = $("replication-status").value;
  const corpus = `${operation.entityId} ${operation.updateType} ${operation.status} ${operation.discontinuityReason} ${operation.originPlayerId}`.toLowerCase();
  const problem = operation.status === "Discontinuity" || operation.status === "Rejected" || operation.status === "Ambiguous";
  return (!term || corpus.includes(term)) && (!status || operation.status === status) && (!$("replication-problems").checked || problem);
}
function renderReplicationList() {
  const visible = replicationOperations.filter(replicationMatches), root = $("replication-list"); root.replaceChildren();
  $("replication-count").textContent = `${visible.length.toLocaleString()} of ${replicationOperations.length.toLocaleString()} operations`;
  for (const operation of visible) {
    const button = document.createElement("button"); button.className = `replication-op${operation.operationId === selectedReplicationId ? " selected" : ""}`;
    const item = document.createElement("span"); item.className = "item"; item.textContent = `Item ${operation.entityId}`;
    const kind = document.createElement("span"); kind.className = "kind"; kind.textContent = operation.updateType || "update";
    const route = document.createElement("span"); route.className = "route"; const delivery = operation.recipientCount > 0 ? `${operation.appliedRecipientCount}/${operation.recipientCount} applied` : operation.status === "Complete" ? "host applied · no additional recipients" : "no recipients recorded"; route.textContent = `${sessionLabel(operation.originSessionId)} · ${operation.stageCount} stages · ${delivery}${operation.stateDiscontinuityCount ? ` · ${operation.stateDiscontinuityCount} state discontinuities` : ""}${operation.discontinuityReason ? ` · ${operation.discontinuityReason}` : ""}`;
    const status = document.createElement("span"); status.className = `replication-status ${String(operation.status).toLowerCase()}`; status.textContent = operation.status;
    button.append(item, kind, route, status); button.addEventListener("click", () => selectReplication(operation.operationId)); root.append(button);
  }
}
async function selectReplication(id) {
  selectedReplicationId = id; renderReplicationList();
  try { selectedReplication = await fetch(`/api/replication/${encodeURIComponent(id)}`).then(response => response.ok ? response.json() : null); renderReplicationDetail(); } catch {}
}
function valueText(value) { if (value == null) return "—"; return typeof value === "object" ? JSON.stringify(value) : String(value); }
function stateFrom(operation, predicate) { const stage = [...(operation.stages || [])].reverse().find(predicate); return stage?.data || {}; }
function renderReplicationDetail() {
  const root = $("replication-detail"), operation = selectedReplication;
  if (!operation) { root.innerHTML = '<p class="empty">Select a replication operation.</p>'; return; }
  root.replaceChildren();
  const head = document.createElement("div"); head.className = "replication-head";
  const title = document.createElement("div"), eyebrow = document.createElement("p"), heading = document.createElement("h2"), meta = document.createElement("p"); eyebrow.className = "eyebrow"; eyebrow.textContent = `${operation.status} · ${operation.correlationConfidence} correlation`; heading.textContent = `Item ${operation.entityId} · ${operation.updateType || "Update"}`; meta.className = "meta"; meta.textContent = `${operation.operationId} · ${new Date(operation.startedUtc).toISOString()}${operation.discontinuityReason ? ` · ${operation.discontinuityReason}` : ""}`; title.append(eyebrow, heading, meta);
  const actions = document.createElement("div"); actions.className = "replication-actions"; const copySummary = document.createElement("button"), copyFull = document.createElement("button"); copySummary.textContent = "Copy summary"; copyFull.textContent = "Copy full"; copySummary.addEventListener("click", () => navigator.clipboard.writeText(replicationSummary(operation))); copyFull.addEventListener("click", () => navigator.clipboard.writeText(JSON.stringify(operation, null, 2))); actions.append(copySummary, copyFull); head.append(title, actions); root.append(head);
  const recipientTitle = document.createElement("h3"); recipientTitle.textContent = "Recipient expectations"; root.append(recipientTitle, recipientTable(operation));
  const flowTitle = document.createElement("h3"); flowTitle.textContent = "Replication flow"; root.append(flowTitle, flowLanes(operation));
  const discontinuityTitle = document.createElement("h3"); discontinuityTitle.textContent = "Semantic item discontinuities"; root.append(discontinuityTitle, stateDiscontinuities(operation));
  const stateTitle = document.createElement("h3"); stateTitle.textContent = "Resulting state comparison"; root.append(stateTitle, stateComparison(operation));
}
function stateDiscontinuities(operation) {
  const root=document.createElement("div");root.className="state-discontinuities";const comparisons=operation.stateComparisons||[];
  if(!comparisons.length){const empty=document.createElement("p");empty.className="empty";empty.textContent="No comparable post-apply Unity state has arrived yet.";root.append(empty);return root;}
  for(const comparison of comparisons){const section=document.createElement("section");section.className=`state-comparison ${comparison.differences?.length?"has-differences":"matches"}`;const heading=document.createElement("h4");heading.textContent=`${sessionLabel(comparison.sessionId)} — ${comparison.differences?.length||0} discontinuities`;section.append(heading);
    if(!comparison.differences?.length){const ok=document.createElement("p");ok.className="comparison-ok";ok.textContent="Semantic state matches. Process-local identity, local coordinates and local storage bookkeeping were ignored.";section.append(ok);}
    else {const table=document.createElement("table");table.className="state-difference-table";table.innerHTML="<thead><tr><th>Problem</th><th>Field</th><th>Expected</th><th>Actual</th><th>Detail</th></tr></thead>";const body=document.createElement("tbody");for(const difference of comparison.differences){const row=document.createElement("tr");[difference.code,difference.field,difference.expected,difference.actual,difference.detail||"—"].forEach(value=>{const cell=document.createElement("td");cell.textContent=value;row.append(cell);});body.append(row);}table.append(body);section.append(table);}root.append(section);}
  return root;
}
function recipientTable(operation) {
  const table = document.createElement("table"); table.className = "recipient-table"; table.innerHTML = "<thead><tr><th>Recipient</th><th>Interest decision</th><th>Sent</th><th>Received</th><th>Handled</th><th>Applied</th><th>Result</th></tr></thead>"; const body = document.createElement("tbody");
  for (const recipient of operation.recipients || []) { const row = document.createElement("tr"); const values = [`P${recipient.playerId} ${recipient.playerName || ""}`, recipient.decision || "—", recipient.sent, recipient.received, recipient.handled, recipient.applied, recipient.discontinuity || "OK"]; values.forEach((value,index) => { const cell=document.createElement("td"); cell.textContent = typeof value === "boolean" ? (value ? "YES" : "NO") : value; if(typeof value === "boolean") cell.className=value?"yes":"no"; row.append(cell); }); body.append(row); }
  if (!(operation.recipients || []).length) { const row=document.createElement("tr"),cell=document.createElement("td");cell.colSpan=7;cell.className="empty";cell.textContent="No recipient expectation has been observed yet.";row.append(cell);body.append(row); } table.append(body); return table;
}
function flowLanes(operation) {
  const root = document.createElement("div"); root.className = "flow-lanes"; const groups = Object.groupBy ? Object.groupBy(operation.stages || [], stage => stage.sessionId) : (operation.stages || []).reduce((map,stage)=>((map[stage.sessionId] ||= []).push(stage),map),{});
  for (const [sessionId, stages] of Object.entries(groups)) { const lane=document.createElement("section");lane.className="flow-lane";const heading=document.createElement("h4");heading.textContent=sessionLabel(sessionId);lane.append(heading);for(const stage of stages){const row=document.createElement("div");row.className="flow-stage";const elapsed=document.createElement("span"),name=document.createElement("span"),tick=document.createElement("span");elapsed.className="elapsed";elapsed.textContent=`+${Math.max(0,new Date(stage.timestampUtc)-new Date(operation.startedUtc))}ms`;name.textContent=stage.eventName;tick.className="tick";tick.textContent=stage.networkTick==null?`#${stage.sourceSequence}`:`t${stage.networkTick}`;row.append(elapsed,name,tick);lane.append(row);}root.append(lane); } return root;
}
function stateComparison(operation) {
  const host = stateFrom(operation, stage => stage.role === "host" && stage.eventName === "item.snapshot-apply.after"), origin = stateFrom(operation, stage => stage.sessionId === operation.originSessionId && stage.eventName === "item.snapshot-apply.after"), recipients = operation.recipients || [];
  const columns = [{name:"Origin",state:origin},{name:"Host",state:host},...recipients.map(value=>({name:`P${value.playerId}`,state:value.resultingState||{}}))]; const keys=[...new Set(columns.flatMap(column=>Object.keys(column.state)))].sort().slice(0,60); const table=document.createElement("table");table.className="state-table";const head=document.createElement("thead"),headRow=document.createElement("tr");["Field",...columns.map(value=>value.name)].forEach(value=>{const cell=document.createElement("th");cell.textContent=value;headRow.append(cell);});head.append(headRow);table.append(head);const body=document.createElement("tbody");for(const key of keys){const row=document.createElement("tr"),field=document.createElement("td");field.textContent=key;row.append(field);for(const column of columns){const cell=document.createElement("td");cell.textContent=valueText(column.state[key]);row.append(cell);}body.append(row);}if(!keys.length){const row=document.createElement("tr"),cell=document.createElement("td");cell.colSpan=columns.length+1;cell.className="empty";cell.textContent="No post-apply states have been correlated yet.";row.append(cell);body.append(row);}table.append(body);return table;
}
function replicationSummary(operation) { const lines=[`Item ${operation.entityId} · ${operation.updateType || "Update"} · ${operation.status}`,`${operation.operationId} · ${operation.correlationConfidence} correlation`,operation.discontinuityReason?`Discontinuity: ${operation.discontinuityReason}`:"",""];for(const stage of operation.stages||[])lines.push(`${new Date(stage.timestampUtc).toISOString().substring(11,23)}  ${sessionLabel(stage.sessionId)}  ${stage.eventName}  t${stage.networkTick??"-"}`);for(const recipient of operation.recipients||[])lines.push(`P${recipient.playerId} expected=${recipient.expected} sent=${recipient.sent} received=${recipient.received} handled=${recipient.handled} applied=${recipient.applied} ${recipient.discontinuity||""}`);for(const comparison of operation.stateComparisons||[])for(const difference of comparison.differences||[])lines.push(`${sessionLabel(comparison.sessionId)} STATE ${difference.code}: ${difference.field} expected=${difference.expected} actual=${difference.actual}${difference.detail?` (${difference.detail})`:""}`);return lines.filter((value,index)=>value||index===3).join("\n"); }
async function refreshReplication() { if (!replicationVisible) return; try { replicationOperations = await fetch("/api/replication").then(response => response.json()); renderReplicationList(); if(selectedReplicationId) await selectReplication(selectedReplicationId); } catch {} }
[$("replication-search"),$("replication-status"),$("replication-problems")].forEach(control=>control.addEventListener("input",renderReplicationList));
$("replication-captures").addEventListener("click", async event => { const current=await fetch("/api/settings").then(response=>response.json());current.automaticReplicationCaptures=!current.automaticReplicationCaptures;await post("/api/settings",current);event.target.textContent=`Auto captures: ${current.automaticReplicationCaptures?"on":"off"}`; });
setInterval(refreshReplication, 750);
