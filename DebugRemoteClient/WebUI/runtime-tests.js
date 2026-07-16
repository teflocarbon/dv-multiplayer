let runtimeTestCapabilities = null, activeRuntimeTest = null, runtimeTestPoll = null;

const runtimeTestTab = document.createElement("button");
runtimeTestTab.id = "runtime-tests-tab";
runtimeTestTab.textContent = "Runtime tests";
document.querySelector(".view-tabs").append(runtimeTestTab);

const runtimeTestView = document.createElement("section");
runtimeTestView.id = "runtime-tests-view";
runtimeTestView.hidden = true;
runtimeTestView.innerHTML = `
  <div class="runtime-test-toolbar">
    <select id="runtime-test-case"><option value="">Select a runtime test</option></select>
    <select id="runtime-test-target"><option value="">Select a process</option></select>
    <button id="runtime-test-run">Run</button>
    <button id="runtime-test-cancel" class="quiet" disabled>Cancel</button>
  </div>
  <p id="runtime-test-description" class="meta">Runtime tests are available only from debug builds with the harness enabled.</p>
  <label class="runtime-test-parameters"><span>Parameters (JSON)</span><textarea id="runtime-test-params" rows="5" spellcheck="false">{}</textarea></label>
  <section id="runtime-test-result" class="runtime-test-result"><p class="empty">Select a test and target process.</p></section>`;
document.querySelector("main").append(runtimeTestView);
window.registerDashboardView("runtime-tests", runtimeTestTab, runtimeTestView, refreshRuntimeTests);

async function refreshRuntimeTests() {
  if (activeView !== "runtime-tests") return;
  try {
    const response = await authenticatedGet("/api/runtime-tests/capabilities");
    if (!response.ok) throw new Error(`Capabilities unavailable (${response.status})`);
    runtimeTestCapabilities = await response.json();
    const cases = $("runtime-test-case"), selectedCase = cases.value;
    cases.replaceChildren(new Option("Select a runtime test", ""));
    for (const test of runtimeTestCapabilities.tests || []) cases.add(new Option(`${test.category} · ${test.displayName}`, test.testId));
    if (Array.from(cases.options).some(option => option.value === selectedCase)) cases.value = selectedCase;
    const targets = $("runtime-test-target"), selectedTarget = targets.value;
    targets.replaceChildren(new Option("Select a process", ""));
    for (const session of knownSessions.filter(value => value.role !== "dashboard" && value.role !== "standalone"))
      targets.add(new Option(`${session.role}${session.playerId == null ? "" : ` P${session.playerId}`} · ${session.playerName || session.sessionId}`, session.sessionId));
    if (Array.from(targets.options).some(option => option.value === selectedTarget)) targets.value = selectedTarget;
    updateRuntimeTestDescription();
  } catch (error) {
    $("runtime-test-result").innerHTML = `<p class="empty">${error.message}</p>`;
  }
}

function selectedRuntimeTest() {
  return (runtimeTestCapabilities?.tests || []).find(test => test.testId === $("runtime-test-case").value);
}

function updateRuntimeTestDescription() {
  const test = selectedRuntimeTest();
  $("runtime-test-description").textContent = test
    ? `${test.fidelity} · ${test.mutationKind} · requires ${(test.requiredCapabilities || []).join(", ") || "no special capability"}`
    : "Runtime tests are available only from debug builds with the harness enabled.";
}

$("runtime-test-case").addEventListener("change", updateRuntimeTestDescription);
$("runtime-test-run").addEventListener("click", async () => {
  const test = selectedRuntimeTest(), target = $("runtime-test-target").value;
  if (!test || !target) { alert("Select a test and target process."); return; }
  let parameters;
  try { parameters = JSON.parse($("runtime-test-params").value || "{}"); }
  catch { alert("Parameters must be valid JSON."); return; }
  parameters = Object.fromEntries(Object.entries(parameters).map(([key, value]) => [key, String(value)]));
  const request = {
    runId: `dashboard-${Date.now()}`, caseId: test.testId, phaseId: "act", stepId: test.testId,
    command: test.testId, targetSessionId: target, mutationKind: test.mutationKind,
    timeoutMilliseconds: test.timeoutMilliseconds, parameters
  };
  const response = await post("/api/runtime-tests/commands", request), accepted = await response.json();
  if (!response.ok || !accepted.accepted) {
    $("runtime-test-result").textContent = accepted.reason || `Request failed (${response.status})`;
    return;
  }
  activeRuntimeTest = accepted;
  $("runtime-test-cancel").disabled = false;
  clearInterval(runtimeTestPoll);
  runtimeTestPoll = setInterval(pollRuntimeTest, 350);
  await pollRuntimeTest();
});

async function pollRuntimeTest() {
  if (!activeRuntimeTest) return;
  const response = await authenticatedGet(activeRuntimeTest.statusUrl);
  if (!response.ok) return;
  const run = await response.json();
  renderRuntimeTest(run);
  if (["Passed", "Failed", "FailedDirty", "Cancelled", "Unsupported"].includes(run.status)) {
    clearInterval(runtimeTestPoll);
    runtimeTestPoll = null;
    $("runtime-test-cancel").disabled = true;
  }
}

function renderRuntimeTest(run) {
  const root = $("runtime-test-result");
  root.replaceChildren();
  const heading = document.createElement("h2");
  heading.textContent = `${run.command} · ${run.status}`;
  root.append(heading);
  for (const process of run.processes || []) {
    const section = document.createElement("section");
    section.className = `runtime-test-process ${String(process.status).toLowerCase()}`;
    const title = document.createElement("h3");
    title.textContent = `${process.role}${process.playerId == null ? "" : ` P${process.playerId}`} · ${process.status}`;
    const detail = document.createElement("pre");
    detail.textContent = JSON.stringify({error: process.error || undefined, result: process.result}, null, 2);
    section.append(title, detail);
    root.append(section);
  }
  if (!(run.processes || []).length) {
    const detail = document.createElement("pre");
    detail.textContent = JSON.stringify({error: run.error || undefined, result: run.result}, null, 2);
    root.append(detail);
  }
}

$("runtime-test-cancel").addEventListener("click", async () => {
  if (activeRuntimeTest) await post(`${activeRuntimeTest.statusUrl}/cancel`, {});
});
setInterval(refreshRuntimeTests, 2000);
