let runtimeEnvironmentTimer = null;
const escapeEnvironment = value => String(value ?? "").replace(/[&<>"']/g, character => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"})[character]);
const environmentTab = document.createElement("button");
environmentTab.textContent = "Environment";
document.querySelector(".view-tabs").append(environmentTab);
const environmentView = document.createElement("section");
environmentView.hidden = true;
environmentView.innerHTML = `
  <div class="environment-grid">
    <label>Derail Valley executable<input id="environment-exe" placeholder="C:\\...\\DerailValley.exe"></label>
    <label>Working directory<input id="environment-working" placeholder="Defaults to executable directory"></label>
    <label>Common launch arguments<input id="environment-common-args"></label>
    <label>Host-only arguments<input id="environment-host-args"></label>
    <label>Client-only arguments<input id="environment-client-args"></label>
    <label>Client destination address<input id="environment-address" value="127.0.0.1"></label>
    <label>Shared host/client port<input id="environment-port" type="number" value="7777"></label>
    <label>Shared host/client password<input id="environment-password" type="password"></label>
    <label>Server name<input id="environment-server-name" value="DVMP automated test"></label>
    <label>Baseline save game mode<select id="environment-save-game-mode"><option value="">Select an explicit game mode</option><option value="Career">Career</option><option value="FreeRoam">Free Roam</option></select></label>
    <label>Baseline save UID<input id="environment-save-uid" type="number" placeholder="Required"><small>UID 0 is valid. No latest-save fallback exists.</small></label>
    <label>Baseline save name<input id="environment-save-name" placeholder="Required exact name"></label>
    <label>Baseline save path<input id="environment-save-base-path" placeholder="Required relative save path"></label>
    <label><span>Managed windows</span><span><input id="environment-minimize-windows" type="checkbox"> Minimize windows</span></label>
    <label><span>Test input</span><span><input id="environment-disable-window-input" type="checkbox" checked> Prevent managed games from locking or hiding the desktop cursor</span></label>
  </div>
  <p class="environment-help">Tests launch only an exact <strong>manual</strong> baseline: UID, name, game mode, and relative save path must all match. Autosaves are never candidates. If the baseline is incomplete, the dashboard only discovers and lists manual saves.</p>
  <div class="environment-actions"><button id="environment-start">Launch host + client</button><button id="environment-stop" class="quiet">Stop managed processes</button></div>
  <section id="environment-status" class="environment-status">
    <div id="environment-stage" class="environment-stage idle"><h2 id="environment-stage-name">Idle</h2><p id="environment-stage-message">Environment is idle.</p><p id="environment-stage-error" class="error" hidden></p><p id="environment-processes" class="meta">Host PID — · Client PID —</p></div>
    <section id="environment-available-saves" hidden><h3>Available manual saves</h3><div id="environment-save-catalog" class="environment-save-catalog"></div></section>
    <div><h3>Selected host save</h3><pre id="environment-host-save">No save selected yet.</pre></div>
    <div class="environment-readiness">
      <div><div class="environment-card-head"><h3>Host</h3><button class="quiet environment-copy" data-copy="host">Copy</button></div><pre id="environment-host-readiness">{}</pre></div>
      <div><div class="environment-card-head"><h3>Client</h3><button class="quiet environment-copy" data-copy="client">Copy</button></div><pre id="environment-client-readiness">{}</pre></div>
    </div>
  </section>`;
document.querySelector("main").append(environmentView);
window.registerDashboardView("environment", environmentTab, environmentView, refreshEnvironment);

const environmentFields = ["exe","working","common-args","host-args","client-args","address","port","password","server-name","save-game-mode","save-uid","save-name","save-base-path"];
let environmentSaveTimer = null;
let browserEnvironmentConfigurationPresent = false;
for (const name of environmentFields) {
  const input = $("environment-" + name), saved = localStorage.getItem("dvmp-environment-" + name);
  if (saved != null) { input.value = saved; browserEnvironmentConfigurationPresent = true; }
  input.addEventListener("input", scheduleEnvironmentConfigurationSave);
}
$("environment-minimize-windows").addEventListener("change", scheduleEnvironmentConfigurationSave);
$("environment-disable-window-input").addEventListener("change", scheduleEnvironmentConfigurationSave);

function environmentRequest() { return {
  executablePath: $("environment-exe").value.trim(), workingDirectory: $("environment-working").value.trim(),
  commonArguments: $("environment-common-args").value, hostArguments: $("environment-host-args").value,
  clientArguments: $("environment-client-args").value, address: $("environment-address").value.trim() || "127.0.0.1",
  port: Number($("environment-port").value), password: $("environment-password").value,
  serverName: $("environment-server-name").value.trim() || "DVMP automated test", maxPlayers: 2,
  hostSaveGameMode: $("environment-save-game-mode").value,
  hostSaveUid: $("environment-save-uid").value === "" ? null : Number($("environment-save-uid").value),
  hostSaveName: $("environment-save-name").value.trim(),
  hostSaveBasePath: $("environment-save-base-path").value.trim(),
  minimizeManagedWindows: $("environment-minimize-windows").checked,
  disableManagedWindowInput: $("environment-disable-window-input").checked,
  agentTimeoutSeconds: 120, worldTimeoutSeconds: 300
}; }

function applyEnvironmentConfiguration(value) {
  if (!value) return;
  $("environment-exe").value = value.executablePath || "";
  $("environment-working").value = value.workingDirectory || "";
  $("environment-common-args").value = value.commonArguments || "";
  $("environment-host-args").value = value.hostArguments || "";
  $("environment-client-args").value = value.clientArguments || "";
  $("environment-address").value = value.address || "127.0.0.1";
  $("environment-port").value = value.port || 7777;
  $("environment-password").value = value.password || "";
  $("environment-server-name").value = value.serverName || "DVMP automated test";
  $("environment-save-game-mode").value = value.hostSaveGameMode || "";
  $("environment-save-uid").value = value.hostSaveUid == null ? "" : value.hostSaveUid;
  $("environment-save-name").value = value.hostSaveName || "";
  $("environment-save-base-path").value = value.hostSaveBasePath || "";
  $("environment-minimize-windows").checked = value.minimizeManagedWindows === true;
  $("environment-disable-window-input").checked = value.disableManagedWindowInput !== false;
}
async function saveEnvironmentConfiguration() {
  await waitForEnvironmentSession();
  await post("/api/runtime-environment/config", environmentRequest());
  for (const name of environmentFields) localStorage.removeItem("dvmp-environment-" + name);
  browserEnvironmentConfigurationPresent = false;
}
function scheduleEnvironmentConfigurationSave() {
  clearTimeout(environmentSaveTimer);
  environmentSaveTimer = setTimeout(() => saveEnvironmentConfiguration().catch(() => {}), 300);
}
async function loadEnvironmentConfiguration() {
  try {
    await waitForEnvironmentSession();
    if (browserEnvironmentConfigurationPresent) { await saveEnvironmentConfiguration(); return; }
    const response = await authenticatedGet("/api/runtime-environment/config");
    if (response.ok) applyEnvironmentConfiguration((await response.json()).configuration);
  } catch (_) { }
}
async function waitForEnvironmentSession() {
  for (let attempt = 0; attempt < 100 && !localSession; attempt++) await new Promise(resolve => setTimeout(resolve, 50));
  if (!localSession) throw new Error("dashboard-session-unavailable");
}
loadEnvironmentConfiguration();

async function refreshEnvironment() {
  if (activeView !== "environment") return;
  try {
    const response = await authenticatedGet("/api/runtime-environment/status");
    if (!response.ok) throw new Error(`Environment API unavailable (${response.status})`);
    renderEnvironment(await response.json());
  } catch (exception) {
    const error = $("environment-stage-error");
    error.textContent = exception.message;
    error.hidden = false;
  }
}
function renderEnvironment(value) {
  const stage = $("environment-stage"), error = $("environment-stage-error");
  stage.className = `environment-stage ${String(value.stage).toLowerCase()}`;
  $("environment-stage-name").textContent = value.stage;
  $("environment-stage-message").textContent = value.message || "";
  error.textContent = value.error || "";
  error.hidden = !value.error;
  $("environment-processes").textContent = `Host PID ${value.hostProcessId || "—"} · Client PID ${value.clientProcessId || "—"}`;
  const selectedSave = value.hostSaveSelection || {};
  $("environment-host-save").textContent = Object.keys(selectedSave).length
    ? JSON.stringify(selectedSave, null, 2) : "No save selected yet.";
  renderEnvironmentSaveCatalog(value.availableSaves || {});
  updateEnvironmentReadiness("host", value.hostReadiness);
  updateEnvironmentReadiness("client", value.clientReadiness);
  $("environment-start").disabled = !!value.active;
  clearTimeout(runtimeEnvironmentTimer);
  if (value.active) runtimeEnvironmentTimer = setTimeout(refreshEnvironment, 1000);
}
function renderEnvironmentSaveCatalog(catalog) {
  const section = $("environment-available-saves"), root = $("environment-save-catalog");
  const saves = Array.isArray(catalog.saves) ? catalog.saves : [];
  section.hidden = !Object.keys(catalog).length;
  root.replaceChildren();
  if (!Object.keys(catalog).length) return;
  root.append(runtimeTestText("p", `${catalog.saveTypePolicy || "ManualOnly"} · ${saves.length} manual save${saves.length === 1 ? "" : "s"}${catalog.ignoredNonManualSaveCount ? ` · ${catalog.ignoredNonManualSaveCount} non-manual save(s) excluded` : ""}`, "meta"));
  if (!saves.length) { root.append(runtimeTestText("p", "No manual saves were found.", "empty")); return; }
  for (const save of saves) {
    const card = document.createElement("article"); card.className = "environment-save-card";
    const text = document.createElement("div");
    text.append(runtimeTestText("strong", save.saveName || "Unnamed manual save"));
    text.append(runtimeTestText("span", `${save.saveGameMode || "Unknown mode"} · UID ${save.saveUid}`, "meta"));
    text.append(runtimeTestText("code", save.saveBasePath || "Missing path"));
    const use = runtimeTestButton("Use as baseline", () => {
      $("environment-save-game-mode").value = save.saveGameMode || "";
      $("environment-save-uid").value = save.saveUid == null ? "" : save.saveUid;
      $("environment-save-name").value = save.saveName || "";
      $("environment-save-base-path").value = save.saveBasePath || "";
      scheduleEnvironmentConfigurationSave();
    }, "quiet");
    card.append(text, use); root.append(card);
  }
}
function updateEnvironmentReadiness(role, data) {
  const pre = $(`environment-${role}-readiness`), next = JSON.stringify(data || {}, null, 2);
  if (pre.textContent === next) return;
  const scrollTop = pre.scrollTop, scrollLeft = pre.scrollLeft;
  pre.textContent = next;
  pre.scrollTop = scrollTop;
  pre.scrollLeft = scrollLeft;
}
async function copyEnvironmentReadiness(button) {
  const pre = $(`environment-${button.dataset.copy}-readiness`), original = button.textContent;
  try {
    await navigator.clipboard.writeText(pre.textContent);
    button.textContent = "Copied";
  } catch (_) {
    const range = document.createRange(), selection = window.getSelection();
    range.selectNodeContents(pre);
    selection.removeAllRanges();
    selection.addRange(range);
    button.textContent = "Selected";
  }
  setTimeout(() => button.textContent = original, 1200);
}
for (const button of document.querySelectorAll(".environment-copy")) button.addEventListener("click", () => copyEnvironmentReadiness(button));
$("environment-start").addEventListener("click", async () => {
  const response = await post("/api/runtime-environment/start", environmentRequest());
  renderEnvironment(await response.json());
  runtimeEnvironmentTimer = setTimeout(refreshEnvironment, 500);
});
$("environment-stop").addEventListener("click", async () => { const response = await post("/api/runtime-environment/stop", {}); renderEnvironment(await response.json()); });
