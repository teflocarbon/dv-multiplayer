let runtimeTestCapabilities = null;
let runtimeTestHistory = [];
let selectedRuntimeTestRequestId = null;
let selectedRuntimeTestRun = null;
let selectedRuntimeTestFingerprint = "";
let selectedRuntimeTestEvents = [];
let runtimeTestPoll = null;
let runtimeScenarioSelections = new Set();
let runtimeScenarioQueue = [];
let runtimeScenarioQueueRunning = false;
let runtimeScenarioPage = 0;
let runtimeTestHistoryPage = 0;
let runtimeTestHistoryRequest = null;
let runtimeTestHistoryRefreshTimer = null;
const runtimeTestDetailRequests = new Map();
let runtimeTestEventRenderTimer = null;
let selectedRuntimeTestEventKeys = new Set();

const runtimeTestPageSize = 30;

const runtimeTestTab = document.createElement("button");
runtimeTestTab.id = "runtime-tests-tab";
runtimeTestTab.textContent = "Tests";
document.querySelector(".view-tabs").append(runtimeTestTab);

const runtimeTestView = document.createElement("section");
runtimeTestView.id = "runtime-tests-view";
runtimeTestView.hidden = true;
runtimeTestView.innerHTML = `
  <section id="runtime-scenario-launcher" class="runtime-scenario-launcher" hidden>
    <div class="runtime-scenario-launcher-head">
      <div><p class="eyebrow">AUTOMATED SCENARIOS</p><h2>Multiplayer regression scenarios</h2><p class="meta">Self-contained host/client workflows with assertions, captures, and verified cleanup.</p></div>
      <div class="runtime-scenario-actions"><span id="runtime-scenario-selection-summary" class="meta">0 selected · queue empty</span><button id="runtime-scenario-select-all" class="quiet">Select all</button><button id="runtime-scenario-clear-selection" class="quiet" disabled>Clear selection</button><button id="runtime-scenario-run-selected" disabled>Run selected</button><button id="runtime-scenario-clear-queue" class="quiet" disabled>Clear queue</button></div>
    </div>
    <div id="runtime-scenario-list" class="runtime-scenario-list"></div>
    <nav class="runtime-test-pagination" aria-label="Scenario pages"><button id="runtime-scenario-page-prev" class="quiet">Previous</button><span id="runtime-scenario-page-summary" class="meta">Page 1 of 1</span><button id="runtime-scenario-page-next" class="quiet">Next</button></nav>
  </section>
  <section class="runtime-test-runner">
    <div class="runtime-test-toolbar">
      <select id="runtime-test-case"><option value="">Select a runtime test</option></select>
      <select id="runtime-test-target"><option value="">Select a process</option></select>
      <button id="runtime-test-run">Run test</button>
      <button id="runtime-test-cancel" class="quiet" disabled>Cancel</button>
    </div>
    <div class="runtime-test-runner-meta">
      <p id="runtime-test-description" class="meta">Debug runtime tests are loading.</p>
      <details class="runtime-test-parameters"><summary>Parameters</summary><textarea id="runtime-test-params" rows="5" spellcheck="false">{}</textarea></details>
    </div>
  </section>
  <details class="runtime-console">
    <summary><span><strong>C# console</strong><small>Externally compiled, Unity main-thread execution</small></span></summary>
    <div class="runtime-console-toolbar">
      <select id="runtime-console-target"><option value="">Select a process</option></select>
      <select id="runtime-console-mode"><option value="expression">Expression</option><option value="body">Method body</option></select>
      <button id="runtime-console-run">Run C#</button>
    </div>
    <textarea id="runtime-console-code" rows="8" spellcheck="false">PlayerManager.PlayerTransform.position - WorldMover.currentMove</textarea>
    <pre id="runtime-console-output">No console command has run yet.</pre>
  </details>
  <div class="runtime-test-layout">
    <aside class="runtime-test-sidebar">
      <div class="runtime-test-sidebar-head"><div><p class="eyebrow">RUN HISTORY</p><strong id="runtime-test-history-count">0 runs</strong></div><button id="runtime-test-refresh" class="quiet">Refresh</button></div>
      <div class="runtime-test-filters"><input id="runtime-test-search" placeholder="Filter runs"><select id="runtime-test-status"><option value="">All statuses</option><option>Running</option><option>Passed</option><option>Failed</option><option>FailedDirty</option><option>Cancelled</option><option>Unsupported</option></select></div>
      <div id="runtime-test-history" class="runtime-test-history"><p class="empty">No test runs yet.</p></div>
      <nav class="runtime-test-pagination runtime-test-history-pagination" aria-label="Run history pages"><button id="runtime-test-history-page-prev" class="quiet">Previous</button><span id="runtime-test-history-page-summary" class="meta">Page 1 of 1</span><button id="runtime-test-history-page-next" class="quiet">Next</button></nav>
    </aside>
    <section id="runtime-test-detail" class="runtime-test-detail"><div class="runtime-test-empty"><p class="eyebrow">RUNTIME TESTS</p><h2>Select a run</h2><p>Run history, assertions, cleanup, process steps, captures, and correlated events appear here.</p></div></section>
  </div>`;
document.querySelector("main").append(runtimeTestView);
window.registerDashboardView("runtime-tests", runtimeTestTab, runtimeTestView, refreshRuntimeTests);

const terminalRuntimeTestStatuses = new Set(["Passed", "Failed", "FailedDirty", "Cancelled", "Unsupported"]);
const noisyRuntimeTestPacketPattern = /(?:heartbeat|keepalive|ping|pong|lobby|serverlist|timesync|weather)/i;
const runtimeTestStatusIcon = status => ({Passed:"✓", Failed:"×", FailedDirty:"!", Cancelled:"■", Unsupported:"?", Running:"●", Queued:"○"}[status] || "○");

function runtimeTestProcessLabel(process) {
  const result = process.result || {}, step = process.stepId || "";
  if (process.command === "inventory.fixture-create") {
    const kind = result.isContainer ? "container" : "item";
    const ownership = result.ownerPlayerId == null ? "" : ` · owner P${result.ownerPlayerId} · holder P${result.holderPlayerId}`;
    return `Create ${kind}: ${result.prefabName || "fixture"}${ownership}`;
  }
  if (process.command === "inventory.fixture-destroy")
    return `Retire fixture: NetId ${result.netId || "pending"}`;
  if (process.command === "inventory.inspect" && step.startsWith("wait-for-client-fixtures-"))
    return `Await client fixture projection · attempt ${step.split("-").pop()}`;
  if (process.command === "inventory.inspect" && step.startsWith("verify-fixture-cleanup-"))
    return `Verify fixture cleanup · attempt ${step.split("-").pop()}`;
  if (process.command?.startsWith("scenario."))
    return `Execute scenario: ${process.command.replace("scenario.", "")}`;
  return step || process.command || process.requestId?.split("-").slice(-3).join("-") || "Process step";
}
const runtimeTestStatusClass = status => String(status || "Queued").toLowerCase();
const runtimeTestDuration = run => {
  const start = run.startedUtc || run.queuedUtc;
  const end = run.completedUtc || (terminalRuntimeTestStatuses.has(run.status) ? start : new Date().toISOString());
  if (!start || !end) return "—";
  const milliseconds = Math.max(0, new Date(end) - new Date(start));
  return milliseconds < 1000 ? `${milliseconds}ms` : milliseconds < 60000 ? `${(milliseconds / 1000).toFixed(1)}s` : `${(milliseconds / 60000).toFixed(1)}m`;
};
const runtimeTestTime = value => value ? new Date(value).toLocaleString() : "—";
const runtimeTestText = (tag, text, className) => { const node = document.createElement(tag); node.textContent = text; if (className) node.className = className; return node; };
const runtimeTestButton = (text, handler, className = "quiet") => { const button = runtimeTestText("button", text, className); button.addEventListener("click", handler); return button; };
const runtimeTestCopy = value => navigator.clipboard?.writeText(String(value || ""));

async function refreshRuntimeTests() {
  if (activeView !== "runtime-tests") return;
  await Promise.all([refreshRuntimeTestCapabilities(), refreshRuntimeTestHistory()]);
  if (selectedRuntimeTestRequestId) await loadRuntimeTest(selectedRuntimeTestRequestId);
}

async function refreshRuntimeTestCapabilities() {
  try {
    const response = await authenticatedGet("/api/runtime-tests/capabilities");
    if (!response.ok) throw new Error(`Capabilities unavailable (${response.status})`);
    runtimeTestCapabilities = await response.json();
    const cases = $("runtime-test-case"), selectedCase = cases.value;
    cases.replaceChildren(new Option("Select a runtime test", ""));
    const tests = runtimeTestCapabilities.tests || [];
    const scenarios = tests.filter(isRuntimeScenario);
    const testsByCategory = tests.filter(test => !isRuntimeScenario(test)).reduce((groups, test) => {
      (groups[test.category || "Other"] ||= []).push(test); return groups;
    }, {});
    for (const category of Object.keys(testsByCategory).sort()) {
      const group = document.createElement("optgroup"); group.label = category;
      for (const test of testsByCategory[category]) group.append(new Option(test.displayName, test.testId));
      cases.append(group);
    }
    if (Array.from(cases.options).some(option => option.value === selectedCase)) cases.value = selectedCase;
    const targets = $("runtime-test-target"), selectedTarget = targets.value;
    targets.replaceChildren(new Option("Select a process", ""));
    for (const session of knownSessions.filter(value => value.role !== "dashboard" && value.role !== "standalone"))
      targets.add(new Option(`${session.role}${session.playerId == null ? "" : ` P${session.playerId}`} · ${session.playerName || session.sessionId}`, session.sessionId));
    if (Array.from(targets.options).some(option => option.value === selectedTarget)) targets.value = selectedTarget;
    const consoleTargets = $("runtime-console-target"), selectedConsoleTarget = consoleTargets.value;
    consoleTargets.replaceChildren(new Option("Select a process", ""));
    for (const option of Array.from(targets.options).slice(1))
      consoleTargets.add(new Option(option.textContent, option.value));
    if (Array.from(consoleTargets.options).some(option => option.value === selectedConsoleTarget))
      consoleTargets.value = selectedConsoleTarget;
    else if (targets.value) consoleTargets.value = targets.value;
    $("runtime-console-run").disabled = !tests.some(test => test.testId === "runtime.csharp");
    renderRuntimeScenarioLauncher(scenarios);
    updateRuntimeTestDescription();
  } catch (error) {
    $("runtime-test-description").textContent = error.message;
  }
}

async function refreshRuntimeTestHistory(selectNewest = false) {
  if (runtimeTestHistoryRequest) return runtimeTestHistoryRequest;
  runtimeTestHistoryRequest = refreshRuntimeTestHistoryCore(selectNewest);
  try { return await runtimeTestHistoryRequest; }
  finally { runtimeTestHistoryRequest = null; }
}

async function refreshRuntimeTestHistoryCore(selectNewest = false) {
  try {
    const response = await authenticatedGet("/api/runtime-tests/runs");
    if (!response.ok) throw new Error(`Run history unavailable (${response.status})`);
    runtimeTestHistory = await response.json();
    renderRuntimeTestHistory();
    if ((selectNewest || !selectedRuntimeTestRequestId) && runtimeTestHistory.length)
      await selectRuntimeTestRun(runtimeTestHistory[0].requestId);
  } catch (error) {
    $("runtime-test-history").replaceChildren(runtimeTestText("p", error.message, "empty"));
  }
}

function scheduleRuntimeTestHistoryRefresh(selectNewest = false) {
  if (activeView !== "runtime-tests" || runtimeTestHistoryRefreshTimer) return;
  runtimeTestHistoryRefreshTimer = setTimeout(() => {
    runtimeTestHistoryRefreshTimer = null;
    void refreshRuntimeTestHistory(selectNewest);
  }, 300);
}

function selectedRuntimeTest() {
  return (runtimeTestCapabilities?.tests || []).find(test => test.testId === $("runtime-test-case").value);
}

function isRuntimeScenario(test) {
  return test?.isScenario === true || test?.category === "Scenarios" ||
    test?.testId?.startsWith("scenario.");
}

function renderRuntimeScenarioLauncher(scenarios) {
  const launcher = $("runtime-scenario-launcher"), root = $("runtime-scenario-list");
  root.replaceChildren();
  launcher.hidden = !scenarios.length;
  const ids = new Set(scenarios.map(scenario => scenario.testId));
  runtimeScenarioSelections = new Set([...runtimeScenarioSelections].filter(id => ids.has(id)));
  const pageCount = Math.max(1, Math.ceil(scenarios.length / runtimeTestPageSize));
  runtimeScenarioPage = Math.min(Math.max(0, runtimeScenarioPage), pageCount - 1);
  const pageStart = runtimeScenarioPage * runtimeTestPageSize;
  for (const scenario of scenarios.slice(pageStart, pageStart + runtimeTestPageSize)) {
    const selected = runtimeScenarioSelections.has(scenario.testId);
    const card = document.createElement("article"); card.className = `runtime-scenario-card${selected ? " selected" : ""}`;
    const checkbox = document.createElement("input"); checkbox.type = "checkbox";
    checkbox.checked = selected; checkbox.setAttribute("aria-label", `Select ${scenario.displayName}`);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) runtimeScenarioSelections.add(scenario.testId);
      else runtimeScenarioSelections.delete(scenario.testId);
      renderRuntimeScenarioLauncher(scenarios);
    });
    const copy = document.createElement("div");
    copy.append(runtimeTestText("strong", scenario.displayName));
    copy.append(runtimeTestText("code", scenario.testId));
    copy.append(runtimeTestText("span", `${scenario.fidelity} · ${Math.round((scenario.timeoutMilliseconds || 0) / 1000)}s timeout`, "meta"));
    card.append(checkbox, copy);
    root.append(card);
  }
  $("runtime-scenario-page-summary").textContent = `${scenarios.length ? pageStart + 1 : 0}-${Math.min(pageStart + runtimeTestPageSize, scenarios.length)} of ${scenarios.length} · Page ${runtimeScenarioPage + 1} of ${pageCount}`;
  $("runtime-scenario-page-prev").disabled = runtimeScenarioPage === 0;
  $("runtime-scenario-page-next").disabled = runtimeScenarioPage >= pageCount - 1;
  updateRuntimeScenarioControls(scenarios);
}

function updateRuntimeScenarioControls(scenarios = []) {
  const selected = runtimeScenarioSelections.size, pending = runtimeScenarioQueue.filter(item => ["Queued", "Submitting", "Scheduled"].includes(item.status)).length;
  const running = runtimeScenarioQueue.find(item => item.status === "Running");
  $("runtime-scenario-selection-summary").textContent = `${selected} selected · ${pending} queued${running ? ` · running ${running.test.displayName}` : ""}`;
  $("runtime-scenario-select-all").disabled = !scenarios.length || selected === scenarios.length;
  $("runtime-scenario-clear-selection").disabled = selected === 0;
  $("runtime-scenario-run-selected").disabled = selected === 0;
  $("runtime-scenario-run-selected").textContent = selected ? `Queue selected (${selected})` : "Run selected";
  $("runtime-scenario-clear-queue").disabled = pending === 0;
}

function defaultRuntimeScenarioTarget() {
  const target = $("runtime-test-target");
  if (!target.value) {
    const clients = knownSessions.filter(session => session.role === "client");
    if (clients.length === 1) target.value = clients[0].sessionId;
  }
  return target.value;
}

function queueSelectedRuntimeScenarios() {
  const target = defaultRuntimeScenarioTarget();
  if (!target) { alert("Select a runtime process before queueing scenarios."); return; }
  const scenarios = (runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario);
  const alreadyQueued = new Set(runtimeScenarioQueue.filter(item => ["Queued", "Submitting", "Scheduled", "Running"].includes(item.status)).map(item => item.test.testId));
  for (const scenario of scenarios) {
    if (runtimeScenarioSelections.has(scenario.testId) && !alreadyQueued.has(scenario.testId))
      runtimeScenarioQueue.push({test:scenario, targetSessionId:target, status:"Queued", requestId:"", error:""});
  }
  runtimeScenarioSelections.clear();
  renderRuntimeScenarioLauncher(scenarios);
  void runRuntimeScenarioQueue();
}

async function runRuntimeScenarioQueue() {
  if (runtimeScenarioQueueRunning) return;
  runtimeScenarioQueueRunning = true;
  try {
    const monitors = [];
    for (;;) {
      const next = runtimeScenarioQueue.find(item => item.status === "Queued");
      if (!next) break;
      next.status = "Submitting";
      renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
      try {
        const accepted = await enqueueRuntimeTest(next.test, next.targetSessionId, {});
        next.requestId = accepted.requestId;
        next.status = "Scheduled";
        monitors.push(monitorRuntimeScenarioQueueItem(next));
      } catch (error) {
        next.status = "Failed";
        next.error = error.message || String(error);
      }
      renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
    }
    await refreshRuntimeTestHistory(true);
    await Promise.all(monitors);
  } finally {
    runtimeScenarioQueueRunning = false;
    updateRuntimeScenarioControls((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
  }
}

async function monitorRuntimeScenarioQueueItem(item) {
  try {
    const result = await waitForRuntimeTestTerminal(item.requestId,
      item.test.timeoutMilliseconds || 60000);
    item.status = result.status;
    item.error = result.error || "";
  } catch (error) {
    item.status = "Failed";
    item.error = error.message || String(error);
  }
  renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
}

async function waitForRuntimeTestTerminal(requestId, timeoutMilliseconds) {
  const queueDeadline = Date.now() + 4 * 60 * 60 * 1000;
  let executionDeadline = null;
  for (;;) {
    const response = await authenticatedGet(`/api/runtime-tests/runs/${encodeURIComponent(requestId)}`);
    if (!response.ok) throw new Error(`Queued scenario status unavailable (${response.status})`);
    const run = await response.json();
    if (requestId === selectedRuntimeTestRequestId) {
      selectedRuntimeTestRun = run;
      renderRuntimeTestDetail(run);
    }
    if (terminalRuntimeTestStatuses.has(run.status)) return run;
    if (run.status !== "Queued" && executionDeadline == null)
      executionDeadline = Date.now() + Math.max(15000, timeoutMilliseconds + 30000);
    if (run.status === "Queued" && Date.now() >= queueDeadline)
      throw new Error("queued-scenario-start-timeout");
    if (executionDeadline != null && Date.now() >= executionDeadline)
      throw new Error("queued-scenario-execution-timeout");
    await new Promise(resolve => setTimeout(resolve, 500));
  }
}

function updateRuntimeTestDescription() {
  const test = selectedRuntimeTest();
  $("runtime-test-run").textContent = isRuntimeScenario(test) ? "Run scenario" : "Run test";
  $("runtime-test-description").textContent = test
    ? `${test.fidelity} · ${test.mutationKind} · ${(test.requiredCapabilities || []).join(", ") || "no special capability required"}`
    : "Select a debug test and the runtime process that should perform it.";
}

function renderRuntimeTestHistory() {
  const root = $("runtime-test-history");
  const term = $("runtime-test-search").value.trim().toLowerCase();
  const status = $("runtime-test-status").value;
  const visible = runtimeTestHistory.filter(run => (!status || run.status === status) && (!term || `${run.command} ${run.caseId} ${run.runId} ${run.error}`.toLowerCase().includes(term)));
  $("runtime-test-history-count").textContent = `${visible.length} ${visible.length === 1 ? "run" : "runs"}`;
  const pageCount = Math.max(1, Math.ceil(visible.length / runtimeTestPageSize));
  runtimeTestHistoryPage = Math.min(Math.max(0, runtimeTestHistoryPage), pageCount - 1);
  const pageStart = runtimeTestHistoryPage * runtimeTestPageSize;
  const page = visible.slice(pageStart, pageStart + runtimeTestPageSize);
  root.replaceChildren();
  if (!visible.length) root.append(runtimeTestText("p", "No matching test runs.", "empty"));
  for (const run of page) {
    const button = document.createElement("button");
    button.className = `runtime-test-run-card ${runtimeTestStatusClass(run.status)}${run.requestId === selectedRuntimeTestRequestId ? " selected" : ""}`;
    const icon = runtimeTestText("span", runtimeTestStatusIcon(run.status), "runtime-test-status-icon");
    const content = document.createElement("span");
    content.className = "runtime-test-run-card-content";
    content.append(runtimeTestText("strong", run.command || run.caseId || "Unnamed test"));
    content.append(runtimeTestText("span", `${run.status} · ${runtimeTestDuration(run)} · ${runtimeTestTime(run.queuedUtc)}`, "meta"));
    if (run.stepId) content.append(runtimeTestText("span", `${run.phaseId || "run"} / ${run.stepId}`, "runtime-test-card-step"));
    button.append(icon, content);
    button.addEventListener("click", () => selectRuntimeTestRun(run.requestId));
    root.append(button);
  }
  root.scrollTop = 0;
  $("runtime-test-history-page-summary").textContent = `${visible.length ? pageStart + 1 : 0}-${Math.min(pageStart + runtimeTestPageSize, visible.length)} of ${visible.length} · Page ${runtimeTestHistoryPage + 1} of ${pageCount}`;
  $("runtime-test-history-page-prev").disabled = runtimeTestHistoryPage === 0;
  $("runtime-test-history-page-next").disabled = runtimeTestHistoryPage >= pageCount - 1;
}

async function selectRuntimeTestRun(requestId) {
  selectedRuntimeTestRequestId = requestId;
  selectedRuntimeTestFingerprint = "";
  selectedRuntimeTestEvents = [];
  selectedRuntimeTestEventKeys = new Set();
  renderRuntimeTestHistory();
  await loadRuntimeTest(requestId);
  await loadRuntimeTestEvents();
}

async function loadRuntimeTest(requestId) {
  if (requestId !== selectedRuntimeTestRequestId) return;
  let request = runtimeTestDetailRequests.get(requestId);
  if (!request) {
    request = authenticatedGet(`/api/runtime-tests/runs/${encodeURIComponent(requestId)}`);
    runtimeTestDetailRequests.set(requestId, request);
  }
  let response;
  try { response = await request; }
  finally { if (runtimeTestDetailRequests.get(requestId) === request) runtimeTestDetailRequests.delete(requestId); }
  if (!response?.ok || requestId !== selectedRuntimeTestRequestId) return;
  const run = await response.json();
  const fingerprint = JSON.stringify(run);
  selectedRuntimeTestRun = run;
  $("runtime-test-cancel").disabled = terminalRuntimeTestStatuses.has(run.status);
  if (fingerprint !== selectedRuntimeTestFingerprint) {
    selectedRuntimeTestFingerprint = fingerprint;
    renderRuntimeTestDetail(run);
  }
  if (terminalRuntimeTestStatuses.has(run.status)) stopRuntimeTestPolling();
  else startRuntimeTestPolling();
}

function renderRuntimeTestDetail(run) {
  const root = $("runtime-test-detail");
  const stepViewStates = new Map(Array.from(root.querySelectorAll(".runtime-test-step[data-step-key]"), row => {
    const json = row.querySelector("pre");
    return [row.dataset.stepKey, {open:row.open, scrollTop:json?.scrollTop || 0, scrollLeft:json?.scrollLeft || 0}];
  }));
  const rawWasOpen = root.querySelector(".runtime-test-raw")?.open === true;
  const rawJson = root.querySelector(".runtime-test-raw pre");
  const rawScrollTop = rawJson?.scrollTop || 0, rawScrollLeft = rawJson?.scrollLeft || 0;
  const previousScrollTop = root.scrollTop;
  const jsonScrollRestorations = [];
  root.replaceChildren();
  const header = document.createElement("header");
  header.className = "runtime-test-detail-head";
  const title = document.createElement("div");
  title.append(runtimeTestText("p", `${run.caseId || "Runtime test"} · ${runtimeTestTime(run.queuedUtc)}`, "eyebrow"));
  title.append(runtimeTestText("h2", run.command || run.caseId || "Unnamed test"));
  title.append(runtimeTestText("p", run.runId, "meta runtime-test-run-id"));
  const actions = document.createElement("div");
  actions.className = "runtime-test-detail-actions";
  actions.append(runtimeTestText("span", `${runtimeTestStatusIcon(run.status)} ${run.status}`, `runtime-test-status-badge ${runtimeTestStatusClass(run.status)}`));
  actions.append(runtimeTestButton("Copy JSON", () => runtimeTestCopy(JSON.stringify(run, null, 2))));
  actions.append(runtimeTestButton("Rerun", () => rerunRuntimeTest(run)));
  header.append(title, actions);
  root.append(header);

  const summary = document.createElement("div");
  summary.className = "runtime-test-summary-grid";
  for (const [label, value] of [["Duration", runtimeTestDuration(run)], ["Started", runtimeTestTime(run.startedUtc || run.queuedUtc)], ["Phase", run.phaseId || "—"], ["Step", run.stepId || "—"]]) {
    const cell = document.createElement("div"); cell.append(runtimeTestText("span", label), runtimeTestText("strong", value)); summary.append(cell);
  }
  root.append(summary);
  if (run.error) {
    const failure = document.createElement("section");
    failure.className = "runtime-test-cleanup failed";
    failure.append(runtimeTestText("strong", `${runtimeTestStatusIcon(run.status)} Scenario ${run.status}`));
    failure.append(runtimeTestText("span", run.error, "meta"));
    root.append(failure);
  }

  const processes = run.processes || [];
  if (processes.length) {
    root.append(runtimeTestText("h3", "Job steps"));
    const timeline = document.createElement("div"); timeline.className = "runtime-test-timeline";
    for (const [processIndex, process] of processes.entries()) {
      const row = document.createElement("details"); row.className = `runtime-test-step ${runtimeTestStatusClass(process.status)}`;
      const stepKey = process.requestId || `${process.phaseId || "run"}:${process.stepId || process.command || processIndex}:${process.role || ""}`;
      row.dataset.stepKey = stepKey;
      const step = document.createElement("summary");
      step.append(runtimeTestText("span", runtimeTestStatusIcon(process.status), "runtime-test-step-icon"));
      const name = document.createElement("span"); name.append(runtimeTestText("strong", runtimeTestProcessLabel(process))); name.append(runtimeTestText("span", `${process.phaseId || "run"} · ${process.role}${process.playerId == null ? "" : ` P${process.playerId}`} · ${process.status}`, "meta"));
      step.append(name); row.append(step);
      const stepViewState = stepViewStates.get(stepKey);
      let processJsonLoaded = stepViewState?.open === true;
      row.open = processJsonLoaded;
      if (processJsonLoaded) {
        const json = runtimeTestText("pre", JSON.stringify({error:process.error || undefined, result:process.result}, null, 2));
        row.append(json);
        jsonScrollRestorations.push({json, scrollTop:stepViewState.scrollTop, scrollLeft:stepViewState.scrollLeft});
      }
      row.addEventListener("toggle", () => {
        if (!row.open || processJsonLoaded) return;
        processJsonLoaded = true;
        row.append(runtimeTestText("pre", JSON.stringify({error:process.error || undefined, result:process.result}, null, 2)));
      });
      timeline.append(row);
    }
    root.append(timeline);
  }

  const scenario = run.result?.scenarioResult || run.result || {};
  const assertions = scenario.assertions || run.result?.assertions || [];
  if (Array.isArray(assertions) && assertions.length) {
    root.append(runtimeTestText("h3", "Assertions"));
    const list = document.createElement("div"); list.className = "runtime-test-assertions";
    for (const assertion of assertions) {
      const passed = assertion.passed !== false && assertion.success !== false;
      const row = document.createElement("div"); row.className = passed ? "passed" : "failed";
      row.append(runtimeTestText("span", passed ? "✓" : "×"), runtimeTestText("strong", assertion.assertionId || assertion.name || assertion.id || assertion.message || "Unnamed assertion"));
      const detail = assertion.detail || assertion.error || (Object.prototype.hasOwnProperty.call(assertion, "actual") ? `Observed: ${typeof assertion.actual === "string" ? assertion.actual : JSON.stringify(assertion.actual)}` : "");
      if (detail) row.append(runtimeTestText("span", detail, "meta"));
      list.append(row);
    }
    root.append(list);
  }

  if (Object.prototype.hasOwnProperty.call(run.result || {}, "cleanupClean")) {
    const clean = run.result.cleanupClean === true;
    const cleanup = document.createElement("section"); cleanup.className = `runtime-test-cleanup ${clean ? "passed" : "failed"}`;
    cleanup.append(runtimeTestText("strong", `${clean ? "✓" : "!"} Fixture cleanup ${clean ? "completed" : "needs attention"}`));
    const failures = run.result.cleanupFailures || [];
    cleanup.append(runtimeTestText("span", failures.length ? failures.join("; ") : "All isolated fixture NetIds and physical projections were retired and verified. This is independent of the scenario outcome above.", "meta"));
    root.append(cleanup);
  }

  const captures = run.result?.captureFiles || [];
  if (captures.length) {
    root.append(runtimeTestText("h3", "Artifacts"));
    const list = document.createElement("div"); list.className = "runtime-test-artifacts";
    for (const path of captures) { const row = document.createElement("div"); row.append(runtimeTestText("code", path), runtimeTestButton("Copy path", () => runtimeTestCopy(path))); list.append(row); }
    root.append(list);
  }

  const networkHeading = document.createElement("div");
  networkHeading.className = "runtime-test-section-heading";
  const networkTitle = document.createElement("div");
  networkTitle.append(runtimeTestText("h3", "Network activity"));
  networkTitle.append(runtimeTestText("p", "Packets and item replication recorded only inside this run's execution window.", "meta"));
  networkHeading.append(networkTitle);
  root.append(networkHeading);
  const networkRoot = document.createElement("div"); networkRoot.id = "runtime-test-network"; root.append(networkRoot); renderRuntimeTestNetworkActivity();

  root.append(runtimeTestText("h3", "All correlated events"));
  const eventRoot = document.createElement("div"); eventRoot.id = "runtime-test-events"; eventRoot.className = "runtime-test-events"; root.append(eventRoot); renderRuntimeTestEvents();
  const raw = document.createElement("details"); raw.className = "runtime-test-raw"; raw.append(runtimeTestText("summary", "Raw result"));
  let rawLoaded = rawWasOpen;
  raw.open = rawWasOpen;
  if (rawLoaded) {
    const json = runtimeTestText("pre", JSON.stringify(run, null, 2));
    raw.append(json); jsonScrollRestorations.push({json, scrollTop:rawScrollTop, scrollLeft:rawScrollLeft});
  }
  raw.addEventListener("toggle", () => {
    if (!raw.open || rawLoaded) return;
    rawLoaded = true; raw.append(runtimeTestText("pre", JSON.stringify(run, null, 2)));
  });
  root.append(raw);
  root.scrollTop = previousScrollTop;
  requestAnimationFrame(() => {
    root.scrollTop = previousScrollTop;
    for (const state of jsonScrollRestorations) {
      state.json.scrollTop = state.scrollTop;
      state.json.scrollLeft = state.scrollLeft;
    }
  });
}

async function loadRuntimeTestEvents() {
  if (!selectedRuntimeTestRun?.runId) return;
  const response = await post("/api/events/query", {testRunId:selectedRuntimeTestRun.runId, limit:500});
  if (!response.ok) return;
  selectedRuntimeTestEvents = await response.json();
  selectedRuntimeTestEvents.sort((left, right) => new Date(left.timestampUtc) - new Date(right.timestampUtc) || (left.sequence || 0) - (right.sequence || 0));
  selectedRuntimeTestEventKeys = new Set(selectedRuntimeTestEvents.map(runtimeTestEventKey));
  renderRuntimeTestNetworkActivity();
  renderRuntimeTestEvents();
}

function runtimeTestEventKey(event) { return `${event.sessionId || ""}:${event.sequence ?? ""}`; }

function scheduleRuntimeTestEventRender() {
  if (runtimeTestEventRenderTimer || activeView !== "runtime-tests" || document.hidden) return;
  runtimeTestEventRenderTimer = setTimeout(() => {
    runtimeTestEventRenderTimer = null;
    if (activeView !== "runtime-tests" || document.hidden) return;
    selectedRuntimeTestEvents.sort((left, right) => new Date(left.timestampUtc) - new Date(right.timestampUtc) || (left.sequence || 0) - (right.sequence || 0));
    renderRuntimeTestNetworkActivity();
    renderRuntimeTestEvents();
  }, 300);
}

function runtimeTestEventsInRunWindow() {
  if (!selectedRuntimeTestRun) return [];
  const started = Date.parse(selectedRuntimeTestRun.startedUtc || selectedRuntimeTestRun.queuedUtc || 0);
  const completed = Date.parse(selectedRuntimeTestRun.completedUtc || new Date().toISOString());
  return selectedRuntimeTestEvents.filter(event => {
    const timestamp = Date.parse(event.timestampUtc || 0);
    return (!Number.isFinite(started) || timestamp >= started) && (!Number.isFinite(completed) || timestamp <= completed);
  });
}

function runtimeTestPacketType(event) {
  return event.data?.packetType || event.data?.packetName || (event.entityType === "Packet" ? event.entityId : "") || "Unknown packet";
}

function isNoisyRuntimeTestPacket(event) {
  return event.highFrequency === true || noisyRuntimeTestPacketPattern.test(runtimeTestPacketType(event));
}

function runtimeTestEventDetail(event) {
  const data = event.data || {};
  return data.reason || data.summary || data.status || data.transitionReason || data.delivery || data.prefabName || "";
}

function runtimeTestNetworkRow(event, kind) {
  const row = document.createElement("div");
  row.className = `runtime-test-network-row ${String(event.severity || "Info").toLowerCase()}`;
  const direction = event.data?.direction === "outbound" || event.eventName === "packet.raw.send" ? "→" : event.data?.direction === "inbound" || event.eventName === "packet.raw.receive" ? "←" : "·";
  const identity = kind === "packet" ? runtimeTestPacketType(event) : `Item ${event.entityId || event.data?.itemNetId || "?"}`;
  const detail = kind === "packet"
    ? [event.data?.peerId == null ? "" : `P${event.data.peerId}`, event.data?.delivery, event.data?.rawLength == null ? "" : `${event.data.rawLength} B`].filter(Boolean).join(" · ")
    : runtimeTestEventDetail(event);
  row.append(
    runtimeTestText("time", new Date(event.timestampUtc).toISOString().substring(11, 23)),
    runtimeTestText("span", event.role || event.runtimeSide || "runtime", "runtime-test-network-role"),
    runtimeTestText("span", direction, `runtime-test-network-direction ${event.data?.direction || "internal"}`),
    runtimeTestText("code", identity),
    runtimeTestText("span", event.eventName, "runtime-test-network-event"),
    runtimeTestText("span", detail, "meta"),
    runtimeTestText("span", [event.testPhaseId, event.testStepId].filter(Boolean).join(" / "), "runtime-test-network-step")
  );
  return row;
}

function renderRuntimeTestNetworkActivity() {
  const root = $("runtime-test-network"); if (!root) return;
  root.replaceChildren();
  const windowEvents = runtimeTestEventsInRunWindow();
  const packetEvents = windowEvents.filter(event => event.category === "packet");
  const visiblePacketEvents = packetEvents.filter(event => !isNoisyRuntimeTestPacket(event));
  const wirePackets = visiblePacketEvents.filter(event => event.eventName === "packet.raw.send" || event.eventName === "packet.raw.receive");
  const packetStages = visiblePacketEvents.filter(event => !wirePackets.includes(event));
  const itemEvents = windowEvents.filter(event => event.category === "item" && event.highFrequency !== true);
  const hiddenPacketCount = packetEvents.length - visiblePacketEvents.length;
  const sentCount = wirePackets.filter(event => event.eventName === "packet.raw.send").length;
  const receivedCount = wirePackets.filter(event => event.eventName === "packet.raw.receive").length;
  const itemIds = new Set(itemEvents.map(event => event.entityId || event.data?.itemNetId).filter(Boolean));

  const summary = document.createElement("div"); summary.className = "runtime-test-network-summary";
  for (const [label, value] of [["Sent", sentCount], ["Received", receivedCount], ["Item events", itemEvents.length], ["Items", itemIds.size]]) {
    const cell = document.createElement("div"); cell.append(runtimeTestText("strong", value), runtimeTestText("span", label)); summary.append(cell);
  }
  root.append(summary);

  const grid = document.createElement("div"); grid.className = "runtime-test-network-grid";
  const packets = document.createElement("section"); packets.className = "runtime-test-network-panel";
  const packetHeader = document.createElement("header");
  const packetTitle = document.createElement("div"); packetTitle.append(runtimeTestText("h4", "Packets"), runtimeTestText("p", `${wirePackets.length} wire events${hiddenPacketCount ? ` · ${hiddenPacketCount} noisy hidden` : ""}`, "meta"));
  packetHeader.append(packetTitle); packets.append(packetHeader);
  const packetList = document.createElement("div"); packetList.className = "runtime-test-network-list";
  if (!wirePackets.length) packetList.append(runtimeTestText("p", "No non-noisy packets were sent or received during this run.", "empty"));
  else for (const event of wirePackets) packetList.append(runtimeTestNetworkRow(event, "packet"));
  packets.append(packetList);
  if (packetStages.length) {
    const stages = document.createElement("details"); stages.className = "runtime-test-packet-stages";
    stages.append(runtimeTestText("summary", `Packet processing stages (${packetStages.length})`));
    const stageList = document.createElement("div"); stageList.className = "runtime-test-network-list";
    for (const event of packetStages) stageList.append(runtimeTestNetworkRow(event, "packet"));
    stages.append(stageList); packets.append(stages);
  }

  const items = document.createElement("section"); items.className = "runtime-test-network-panel";
  const itemHeader = document.createElement("header");
  const itemTitle = document.createElement("div"); itemTitle.append(runtimeTestText("h4", "Item replication"), runtimeTestText("p", `${itemEvents.length} lifecycle events across ${itemIds.size} items`, "meta")); itemHeader.append(itemTitle); items.append(itemHeader);
  const itemList = document.createElement("div"); itemList.className = "runtime-test-network-list";
  if (!itemEvents.length) itemList.append(runtimeTestText("p", "No item replication activity was correlated with this run.", "empty"));
  else for (const event of itemEvents) itemList.append(runtimeTestNetworkRow(event, "item"));
  items.append(itemList);
  grid.append(packets, items); root.append(grid);
}

function renderRuntimeTestEvents() {
  const root = $("runtime-test-events"); if (!root) return;
  root.replaceChildren();
  if (!selectedRuntimeTestEvents.length) { root.append(runtimeTestText("p", "No correlated events captured yet.", "empty")); return; }
  for (const event of selectedRuntimeTestEvents.slice(-300)) {
    const row = document.createElement("div"); row.className = `runtime-test-event ${String(event.severity || "Info").toLowerCase()}`;
    row.append(runtimeTestText("time", new Date(event.timestampUtc).toISOString().substring(11, 23)), runtimeTestText("span", event.role || event.sessionId?.slice(-8) || "runtime"), runtimeTestText("code", event.eventName), runtimeTestText("span", event.data?.reason || event.data?.summary || event.data?.status || "", "meta")); root.append(row);
  }
}

async function rerunRuntimeTest(run) {
  const testId = run.caseId || run.command;
  const scenario = (runtimeTestCapabilities?.tests || []).find(test => test.testId === testId);
  if (isRuntimeScenario(scenario)) {
    runtimeScenarioSelections.add(scenario.testId);
    queueSelectedRuntimeScenarios();
    return;
  }
  const option = Array.from($("runtime-test-case").options).find(value => value.value === testId);
  if (option) $("runtime-test-case").value = option.value;
  await startRuntimeTest();
}

async function startRuntimeTest() {
  const test = selectedRuntimeTest(), target = $("runtime-test-target").value;
  if (!test || !target) { alert("Select a test and target process."); return; }
  let parameters;
  try { parameters = JSON.parse($("runtime-test-params").value || "{}"); }
  catch { alert("Parameters must be valid JSON."); return; }
  parameters = Object.fromEntries(Object.entries(parameters).map(([key, value]) => [key, String(value)]));
  try {
    const accepted = await enqueueRuntimeTest(test, target, parameters);
    await refreshRuntimeTestHistory();
    await selectRuntimeTestRun(accepted.requestId);
    startRuntimeTestPolling();
  } catch (error) { alert(error.message || String(error)); }
}

async function runRuntimeConsole() {
  const target = $("runtime-console-target").value;
  const code = $("runtime-console-code").value;
  if (!target || !code.trim()) { alert("Select a process and enter C# code."); return; }
  const descriptor = (runtimeTestCapabilities?.tests || []).find(test => test.testId === "runtime.csharp");
  if (!descriptor) { alert("The running game build does not expose the C# execution bridge."); return; }
  const output = $("runtime-console-output");
  output.textContent = "Compiling and queueingâ€¦";
  try {
    const accepted = await enqueueRuntimeTest(descriptor, target, {code, mode:$("runtime-console-mode").value});
    let run;
    for (;;) {
      const response = await fetch(`/api/runtime-tests/runs/${encodeURIComponent(accepted.requestId)}`);
      run = await response.json();
      if (terminalRuntimeTestStatuses.has(run.status)) break;
      await new Promise(resolve => setTimeout(resolve, 150));
    }
    output.textContent = JSON.stringify(run, null, 2);
    await refreshRuntimeTestHistory();
    await selectRuntimeTestRun(accepted.requestId);
  } catch (error) { output.textContent = error.message || String(error); }
}

async function enqueueRuntimeTest(test, target, parameters) {
  const requestId = globalThis.crypto?.randomUUID ? crypto.randomUUID().replaceAll("-", "") : `${Date.now()}${Math.random().toString(16).slice(2)}`;
  const request = {requestId, runId:`dashboard-${requestId}`, caseId:test.testId, phaseId:"act", stepId:test.testId, command:test.testId, targetSessionId:target, mutationKind:test.mutationKind, timeoutMilliseconds:test.timeoutMilliseconds, parameters};
  const response = await post("/api/runtime-tests/commands", request);
  const accepted = await response.json();
  if (!response.ok || !accepted.accepted) throw new Error(accepted.reason || `Request failed (${response.status})`);
  return accepted;
}

function startRuntimeTestPolling() { if (runtimeTestPoll) return; runtimeTestPoll = setInterval(() => activeView === "runtime-tests" && !document.hidden && selectedRuntimeTestRequestId && loadRuntimeTest(selectedRuntimeTestRequestId), 1500); }
function stopRuntimeTestPolling() { clearInterval(runtimeTestPoll); runtimeTestPoll = null; }

$("runtime-test-case").addEventListener("change", updateRuntimeTestDescription);
$("runtime-test-run").addEventListener("click", startRuntimeTest);
$("runtime-console-run").addEventListener("click", runRuntimeConsole);
$("runtime-scenario-select-all").addEventListener("click", () => {
  for (const scenario of (runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario)) runtimeScenarioSelections.add(scenario.testId);
  renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
});
$("runtime-scenario-clear-selection").addEventListener("click", () => {
  runtimeScenarioSelections.clear(); renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
});
$("runtime-scenario-run-selected").addEventListener("click", queueSelectedRuntimeScenarios);
$("runtime-scenario-clear-queue").addEventListener("click", () => {
  runtimeScenarioQueue = runtimeScenarioQueue.filter(item => !["Queued", "Submitting"].includes(item.status));
  renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
});
$("runtime-scenario-page-prev").addEventListener("click", () => {
  runtimeScenarioPage--; renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
});
$("runtime-scenario-page-next").addEventListener("click", () => {
  runtimeScenarioPage++; renderRuntimeScenarioLauncher((runtimeTestCapabilities?.tests || []).filter(isRuntimeScenario));
});
$("runtime-test-cancel").addEventListener("click", async () => { if (selectedRuntimeTestRequestId) await post(`/api/runtime-tests/runs/${encodeURIComponent(selectedRuntimeTestRequestId)}/cancel`, {}); });
$("runtime-test-refresh").addEventListener("click", () => refreshRuntimeTestHistory());
$("runtime-test-search").addEventListener("input", () => { runtimeTestHistoryPage = 0; renderRuntimeTestHistory(); });
$("runtime-test-status").addEventListener("input", () => { runtimeTestHistoryPage = 0; renderRuntimeTestHistory(); });
$("runtime-test-history-page-prev").addEventListener("click", () => { runtimeTestHistoryPage--; renderRuntimeTestHistory(); });
$("runtime-test-history-page-next").addEventListener("click", () => { runtimeTestHistoryPage++; renderRuntimeTestHistory(); });
window.addEventListener("dvmp-debug-event", event => {
  const item = event.detail;
  if (item?.eventName === "runtime-test.run-updated") {
    scheduleRuntimeTestHistoryRefresh(!selectedRuntimeTestRequestId);
    if (activeView === "runtime-tests" && item.entityId === selectedRuntimeTestRequestId) loadRuntimeTest(selectedRuntimeTestRequestId);
  }
  if (selectedRuntimeTestRun?.runId && item?.testRunId === selectedRuntimeTestRun.runId) {
    const key = runtimeTestEventKey(item);
    if (!selectedRuntimeTestEventKeys.has(key)) {
      selectedRuntimeTestEventKeys.add(key); selectedRuntimeTestEvents.push(item);
      if (selectedRuntimeTestEvents.length > 500) {
        const removed = selectedRuntimeTestEvents.splice(0, selectedRuntimeTestEvents.length - 500);
        for (const old of removed) selectedRuntimeTestEventKeys.delete(runtimeTestEventKey(old));
      }
      scheduleRuntimeTestEventRender();
    }
  }
});
setInterval(() => { if (activeView === "runtime-tests") refreshRuntimeTestHistory(); }, 5000);
