using DV;
using DV.InventorySystem;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Serialization;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multiplayer.Debugging;

public sealed class DebugOverlayController : MonoBehaviour
{
    private sealed class EntityRow { public GameObject Root; public Button Button; public TMP_Text Text; public Image Background; public LayoutElement Layout; }
    private static readonly Color Panel = new(0.055f, 0.065f, 0.075f, 0.97f);
    private static readonly Color PanelLight = new(0.085f, 0.1f, 0.115f, 0.98f);
    private static readonly Color Header = new(0.035f, 0.042f, 0.05f, 1f);
    private static readonly Color Accent = new(0.15f, 0.72f, 0.38f, 1f);
    private static readonly Color Selected = new(0.10f, 0.38f, 0.23f, 1f);
    private static readonly Color Muted = new(0.62f, 0.68f, 0.72f, 1f);
    private static readonly Color Foreground = new(0.91f, 0.94f, 0.95f, 1f);
    private static DebugOverlayController instance;
    private static bool visible;
    private static bool frozen;
    private static bool verbose;
    private static bool compact = true;
    private static bool settingsVisible;
    private static bool packetPaused;
    private static bool showNoisyPackets;
    private static bool summaryCopy = true;
    private static string clipboardStatus = string.Empty;
    private static float clipboardStatusUntil;
    private static DebugEntityDto selected;
    private static DebugEvent selectedPacket;
    private static DebugEvent[] packetEvents = Array.Empty<DebugEvent>();
    private static DebugEvent[] packetSnapshot = Array.Empty<DebugEvent>();
    private static readonly HashSet<string> mutedPacketTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> defaultSuppressedPacketTypes = new(
        ProtocolManifestProvider.Current.Packets.Where(packet => packet.SuppressByDefault)
            .Select(packet => ShortPacketName(packet.TypeName)), StringComparer.OrdinalIgnoreCase);
    private static DebugEntityDto[] allEntities = Array.Empty<DebugEntityDto>();
    private static DebugEntityDto[] filteredEntities = Array.Empty<DebugEntityDto>();
    private static string filter = "All";
    private static string search = string.Empty;
    private static int selectedIndex;
    private static int selectedPacketIndex;
    private static long traceModeChangedAfterSequence;
    private static float nextRefresh;
    private static bool cursorCaptured;
    private static CursorLockMode previousCursorLock;
    private static bool previousCursorVisible;

    private GameObject canvasRoot;
    private CanvasScaler canvasScaler;
    private RectTransform panelRect;
    private RectTransform entityListContent;
    private LayoutElement entityPaneLayout;
    private InputField searchInput;
    private GameObject packetControls;
    private GameObject packetModeControls;
    private Button noisyPacketsButton;
    private Button pausePacketsButton;
    private Button mutePacketButton;
    private Button tracePacketButton;
    private Button summaryModeButton;
    private Button decodedModeButton;
    private Button rawModeButton;
    private Button copyModeButton;
    private GameObject settingsPanel;
    private Text settingsText;
    private Button labelsToggleButton;
    private Button throughWallsButton;
    private Button errorsOnlyButton;
    private Button itemsToggleButton;
    private Button playersToggleButton;
    private Button trainsToggleButton;
    private string lastInspectorKey = string.Empty;
    private readonly Dictionary<string, string> previousInspectorState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> changedInspectorFields = new(StringComparer.Ordinal);
    private Text sessionText;
    private Text entityCountText;
    private Text worldText;
    private Text streamText;
    private Text selectedTitle;
    private Text detailsText;
    private Text timelineText;
    private GameObject timelineSection;
    private readonly List<EntityRow> rows = new();
    private readonly Dictionary<string, Image> filterButtons = new(StringComparer.OrdinalIgnoreCase);

    public static bool Verbose => verbose;
    public static void Toggle()
    {
        visible = !visible;
        if (visible) CaptureCursor(); else RestoreCursor();
        instance?.SetVisible(visible);
    }
    public static void ToggleVerbose() { verbose = !verbose; instance?.Refresh(true); }
    public static void ToggleSettings()
    {
        if (!visible)
        {
            visible = true;
            CaptureCursor();
            instance?.SetVisible(true);
        }
        settingsVisible = !settingsVisible;
        if (instance?.settingsPanel != null) instance.settingsPanel.SetActive(settingsVisible);
        instance?.RefreshSettingsPanel();
    }
    public static void ToggleSize()
    {
        compact = !compact;
        instance?.ApplyPanelSize();
        instance?.Refresh(true);
    }
    public static void ToggleFreeze() { frozen = !frozen; if (frozen && selected != null) selected = EntityDebugRegistry.Get(selected.EntityType, selected.EntityId); instance?.Refresh(true); }
    public static void Select(string type, string id)
    {
        selected = EntityDebugRegistry.Get(type, id); frozen = false;
        int index = Array.FindIndex(filteredEntities, item => item.EntityType == type && item.EntityId == id);
        if (index >= 0) selectedIndex = index;
        instance?.Refresh(true);
    }
    public static bool IsSelected(string type, string id) => selected?.EntityType == type && selected.EntityId == id;
    public static bool SummaryCopyMode => summaryCopy;
    public static void ToggleCopyMode() { summaryCopy = !summaryCopy; instance?.Refresh(true); }
    public static string SelectedJson() => filter == "Health"
        ? DebugJson.Serialize(DiagnosticExport(), Formatting.Indented)
        : filter == "Packet" ? selectedPacket == null ? "No packet selected." : DebugJson.Serialize(PacketInspectionExport(selectedPacket), Formatting.Indented)
        : selected == null ? "No entity selected." : DebugJson.Serialize(selected, Formatting.Indented);
    public static bool CopySelected()
    {
        string value = summaryCopy ? SelectionSummary() : SelectedJson();
        return CopyToClipboard(value != null && !value.StartsWith("No ", StringComparison.Ordinal) ? value : null, filter == "Packet" ? "packet" : filter == "Health" ? "health" : "entity");
    }
    public static bool CopyTimeline()
    {
        if (filter == "Health") return CopyToClipboard(summaryCopy ? Plain(FormatHealth()) : DebugJson.Serialize(new { queues = DebugDiagnostics.QueueSnapshot(), dependencies = DebugDiagnostics.DependencySnapshot(), identity = DebugDiagnostics.IdentitySnapshot() }, Formatting.Indented), "health diagnostics");
        if (filter == "Packet")
        {
            string packetType = PacketType(selectedPacket);
            DebugEvent[] events = string.IsNullOrEmpty(packetType) ? Array.Empty<DebugEvent>() : PacketEventsForType(packetType).ToArray();
            return CopyToClipboard(events.Length == 0 ? null : summaryCopy ? PacketTimelineSummary(events) : DebugJson.Serialize(events, Formatting.Indented), "packet timeline");
        }
        return CopyToClipboard(selected == null ? null : summaryCopy ? EntityTimelineSummary(selected.Timeline) : DebugJson.Serialize(selected.Timeline, Formatting.Indented), "timeline");
    }
    public static bool CopyLastEvent()
    {
        if (filter == "Health") return CopyToClipboard(null, "last event");
        DebugEvent item = filter == "Packet" ? selectedPacket : selected?.Timeline?.LastOrDefault();
        return CopyToClipboard(item == null ? null : summaryCopy ? EventSummary(item, filter == "Packet") : DebugJson.Serialize(item, Formatting.Indented), "last event");
    }
    public static bool CopyReplicationFlow() => CopyToClipboard(filter is "Packet" or "Health" || selected?.EntityType != "Item" ? null : summaryCopy ? Plain(FormatReplicationFlow(selected.EntityId)) : DebugJson.Serialize(DebugDiagnostics.ReplicationFlow(selected.EntityId), Formatting.Indented), "replication flow");
    public static bool CopyInterestMatrix() => CopyToClipboard(filter is "Packet" or "Health" || selected?.EntityType != "Item" ? null : summaryCopy ? Plain(FormatInterestMatrix(selected.EntityId)) : DebugJson.Serialize(DebugDiagnostics.InterestMatrix(selected.EntityId), Formatting.Indented), "host matrix");
    public static bool CopyDiagnosticBundle()
    {
        if (selected == null && selectedPacket == null) return CopyToClipboard(null, "diagnostic bundle");
        if (summaryCopy)
        {
            string summary = SelectionSummary();
            string timeline = filter == "Packet" ? PacketTimelineSummary(PacketEventsForType(PacketType(selectedPacket)).ToArray()) : selected == null ? string.Empty : EntityTimelineSummary(selected.Timeline);
            return CopyToClipboard($"DV MULTIPLAYER OBSERVABILITY\n{DebugRuntime.Session?.Role} · PID {DebugRuntime.Session?.ProcessId} · {DateTime.UtcNow:O}\n\n{summary}\n\nEVENT TIMELINE\n{timeline}".Trim(), "diagnostic bundle");
        }
        object bundle = new
        {
            copiedUtc = DateTime.UtcNow,
            session = DebugRuntime.Session,
            runtimeSettings = DebugRuntime.RuntimeSettings,
            world = new
            {
                currentMove = DebugValueSnapshotter.Snapshot(WorldMover.currentMove),
                playerLocal = DebugValueSnapshotter.Snapshot(PlayerManager.PlayerTransform?.position),
                playerAbsolute = DebugValueSnapshotter.Snapshot(PlayerManager.PlayerTransform == null ? (Vector3?)null : PlayerManager.PlayerTransform.position - WorldMover.currentMove),
                playerCar = PlayerManager.Car?.ID ?? string.Empty
            },
            selectedEntity = selected,
            selectedPacket,
            diagnostics = DiagnosticExport()
        };
        return CopyToClipboard(DebugJson.Serialize(bundle, Formatting.Indented), "diagnostic bundle");
    }

    private static bool CopyToClipboard(string value, string description)
    {
        if (string.IsNullOrEmpty(value))
        {
            clipboardStatus = $"NO {description.ToUpperInvariant()} TO COPY";
            clipboardStatusUntil = Time.unscaledTime + 2f;
            instance?.Refresh(true);
            return false;
        }
        GUIUtility.systemCopyBuffer = value;
        clipboardStatus = $"COPIED {(summaryCopy ? "SUMMARY " : "FULL ")}{description.ToUpperInvariant()} ({value.Length:N0} CHARS)";
        clipboardStatusUntil = Time.unscaledTime + 2f;
        instance?.Refresh(true);
        return true;
    }

    private void Awake()
    {
        instance = this;
        BuildUi();
        SetVisible(visible);
    }

    private void OnDestroy()
    {
        RestoreCursor();
        if (instance == this) instance = null;
        if (canvasRoot != null) Destroy(canvasRoot);
    }

    public static void HandleKeyboard()
    {
        if (!visible || instance == null) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (control && Input.GetKeyDown(KeyCode.C))
        {
            if (shift) CopyTimeline(); else if (alt) CopyLastEvent(); else CopySelected();
        }
        if (control && Input.GetKeyDown(KeyCode.B)) CopyDiagnosticBundle();
        if (control && Input.GetKeyDown(KeyCode.F))
        {
            instance.searchInput?.ActivateInputField();
            instance.searchInput?.Select();
            return;
        }
        if (settingsVisible)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) ToggleSettings();
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) instance.AdjustLabelScale(-0.1f);
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) instance.AdjustLabelScale(0.1f);
            if (Input.GetKeyDown(KeyCode.Comma)) instance.AdjustRadius(-5f);
            if (Input.GetKeyDown(KeyCode.Period)) instance.AdjustRadius(5f);
            if (control && Input.GetKeyDown(KeyCode.S)) instance.SaveDisplaySettings();
        }
        if (instance.searchInput?.isFocused == true)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) instance.searchInput.DeactivateInputField();
            return;
        }
        if (Input.GetKeyDown(KeyCode.Alpha0)) SetFilter("All");
        if (Input.GetKeyDown(KeyCode.Alpha1)) SetFilter("Item");
        if (Input.GetKeyDown(KeyCode.Alpha2)) SetFilter("Player");
        if (Input.GetKeyDown(KeyCode.Alpha3)) SetFilter("TrainCar");
        if (Input.GetKeyDown(KeyCode.Alpha4)) SetFilter("Packet");
        if (Input.GetKeyDown(KeyCode.Alpha5)) SetFilter("Health");
        if (Input.GetKeyDown(KeyCode.Alpha6)) SetFilter("Inventory");
        instance.Refresh(false);
        if (filter == "Health")
        {
            if (Input.GetKeyDown(KeyCode.A)) { DebugDiagnostics.AutomaticCapturesEnabled = !DebugDiagnostics.AutomaticCapturesEnabled; instance.Refresh(true); }
            return;
        }
        if (filter == "Packet")
        {
            if (Input.GetKeyDown(KeyCode.P)) instance.TogglePacketPause();
            if (Input.GetKeyDown(KeyCode.N)) instance.ToggleNoisyPackets();
            if (Input.GetKeyDown(KeyCode.M)) instance.ToggleSelectedPacketMute();
            if (Input.GetKeyDown(KeyCode.T)) instance.ToggleSelectedPacketTrace();
            if (packetEvents.Length == 0) return;
            bool packetNext = Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.RightBracket);
            bool packetPrevious = Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.LeftBracket);
            if (!packetNext && !packetPrevious) return;
            selectedPacketIndex = packetNext ? (selectedPacketIndex + 1) % packetEvents.Length : (selectedPacketIndex - 1 + packetEvents.Length) % packetEvents.Length;
            selectedPacket = packetEvents[selectedPacketIndex];
            instance.Refresh(true);
            return;
        }
        if (filter == "Health" || filteredEntities.Length == 0) return;
        bool next = Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.RightBracket);
        bool previous = Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.LeftBracket);
        if (!next && !previous) return;
        selectedIndex = next ? (selectedIndex + 1) % filteredEntities.Length : (selectedIndex - 1 + filteredEntities.Length) % filteredEntities.Length;
        DebugEntityDto target = filteredEntities[selectedIndex];
        SelectVisibleEntity(target);
    }

    public static bool SelectLookedAt()
    {
        Camera camera = PlayerManager.ActiveCamera;
        if (camera == null) return false;
        string localPlayerId = DebugRuntime.Session?.PlayerId?.ToString();
        var live = EntityDebugRegistry.Live().Where(item => item.component != null && item.dto.EntityType is "Item" or "Player" or "TrainCar")
            .Where(item => item.dto.EntityType != "Player" || !string.Equals(item.dto.EntityId, localPlayerId, StringComparison.Ordinal))
            .Where(item => PlayerManager.PlayerTransform == null || item.component.transform != PlayerManager.PlayerTransform)
            .ToArray();
        foreach (RaycastHit hit in Physics.SphereCastAll(camera.transform.position, 0.12f, camera.transform.forward, 150f).OrderBy(hit => hit.distance))
        {
            var match = live.Where(item => hit.transform == item.component.transform || hit.transform.IsChildOf(item.component.transform))
                .OrderBy(item => AncestorDistance(hit.transform, item.component.transform))
                .ThenBy(item => item.dto.EntityType == "Item" ? 0 : item.dto.EntityType == "Player" ? 1 : 2)
                .FirstOrDefault();
            if (match.component == null) continue;
            Select(match.dto.EntityType, match.dto.EntityId); return true;
        }
        float bestScore = Mathf.Cos(8f * Mathf.Deg2Rad);
        (DebugEntityDto dto, Component component) best = default;
        foreach (var item in live)
        {
            Vector3 offset = item.component.transform.position - camera.transform.position;
            if (offset.sqrMagnitude is < 0.01f or > 22500f) continue;
            float score = Vector3.Dot(camera.transform.forward, offset.normalized);
            if (score > bestScore) { bestScore = score; best = item; }
        }
        if (best.component == null) return false;
        Select(best.dto.EntityType, best.dto.EntityId); return true;
    }

    private static int AncestorDistance(Transform child, Transform ancestor)
    {
        int distance = 0;
        for (Transform current = child; current != null; current = current.parent, distance++)
            if (current == ancestor) return distance;
        return int.MaxValue;
    }

    private static void SetFilter(string value)
    {
        filter = value;
        filteredEntities = filter == "All" ? allEntities : allEntities.Where(item => item.EntityType == filter).ToArray();
        selectedIndex = Mathf.Clamp(selectedIndex, 0, Math.Max(0, filteredEntities.Length - 1));
        if (filter == "Packet" && selectedPacket == null && packetEvents.Length > 0) selectedPacket = packetEvents[0];
        instance?.Refresh(true);
    }

    private static void SelectVisibleEntity(DebugEntityDto target)
    {
        if (target == null)
            return;
        if (filter == "Inventory")
        {
            selected = target;
            frozen = false;
            instance?.Refresh(true);
            return;
        }
        Select(target.EntityType, target.EntityId);
    }

    private static void SetSearch(string value)
    {
        search = value?.Trim() ?? string.Empty;
        packetEvents = Array.Empty<DebugEvent>();
        selectedIndex = selectedPacketIndex = 0;
        instance?.Refresh(true);
    }

    private void SetVisible(bool value)
    {
        canvasRoot?.SetActive(value);
        if (value) Refresh(true);
    }

    private static void CaptureCursor()
    {
        if (!cursorCaptured) { previousCursorLock = Cursor.lockState; previousCursorVisible = Cursor.visible; cursorCaptured = true; }
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }
    private static void RestoreCursor()
    {
        if (!cursorCaptured) return;
        Cursor.lockState = previousCursorLock; Cursor.visible = previousCursorVisible; cursorCaptured = false;
    }

    private void Refresh(bool force)
    {
        if (!visible || canvasRoot == null || !force && Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.25f;
        if (filter == "Packet")
        {
            // Do not rebuild hundreds of entity DTOs while inspecting traffic. The packet
            // view already snapshots the bounded event store and should remain isolated.
            RefreshPacketEvents();
        }
        else if (filter == "Inventory")
        {
            allEntities = LocalInventoryEntities();
            filteredEntities = allEntities.Where(EntityMatchesSearch).ToArray();
            selectedIndex = Mathf.Clamp(selectedIndex, 0, Math.Max(0, filteredEntities.Length - 1));
            if (!frozen && selected != null)
                selected = filteredEntities.FirstOrDefault(item => item.EntityId == selected.EntityId) ?? selected;
        }
        else if (filter != "Health")
        {
            DebugEntityDto[] summaries = EntityDebugRegistry.Summaries().Where(item => item.EntityType is "Item" or "Player" or "TrainCar")
                .Where(EntityMatchesSearch).ToArray();
            // Cap only the numerous item records. A global cap caused items to hide every player
            // and train because the registry is sorted by entity type. Search is applied first.
            allEntities = summaries.Where(item => item.EntityType == "Item").Take(500)
                .Concat(summaries.Where(item => item.EntityType == "Player"))
                .Concat(summaries.Where(item => item.EntityType == "TrainCar"))
                .ToArray();
            filteredEntities = filter == "All" ? allEntities : allEntities.Where(item => item.EntityType == filter).ToArray();
            selectedIndex = Mathf.Clamp(selectedIndex, 0, Math.Max(0, filteredEntities.Length - 1));
            if (!frozen && selected != null)
            {
                EntityDebugRegistry.RefreshLiveState(selected.EntityType, selected.EntityId);
                selected = EntityDebugRegistry.Get(selected.EntityType, selected.EntityId);
            }
        }

        DebugSessionInfo session = DebugRuntime.Session;
        sessionText.text = $"DV MULTIPLAYER  <color=#26B861>OBSERVABILITY</color>   <color=#78858C>{session?.Role?.ToUpperInvariant()} · PID {session?.ProcessId} · TICK {Components.Networking.NetworkLifecycle.Instance?.Tick}</color>";
        DebugDiagnostics.NetworkHealth health = DebugDiagnostics.HealthSnapshot();
        entityCountText.text = filter == "Health"
            ? $"<color=#7E8B92>HEALTH</color>\n<size=22>{DebugDiagnostics.IdentitySnapshot().Length + DebugDiagnostics.DependencySnapshot().Length}</size>  <color=#26B861>ISSUES</color>"
            : filter == "Packet"
            ? $"<color=#7E8B92>PACKET EVENTS</color>\n<size=22>{packetEvents.Length}</size>  <color=#26B861>{(packetPaused ? "PAUSED" : "LIVE")}</color>"
            : filter == "Inventory"
            ? $"<color=#7E8B92>INVENTORY</color>\n<size=22>{filteredEntities.Length}</size>  <color=#26B861>LOCAL ITEMS</color>"
            : $"<color=#7E8B92>ENTITIES</color>\n<size=22>{allEntities.Length}</size>  <color=#26B861>{filter}</color>";
        worldText.text = $"<color=#7E8B92>WORLD ORIGIN</color>\n{WorldMover.currentMove}\n<color=#7E8B92>ABS</color> {(PlayerManager.PlayerTransform == null ? Vector3.zero : PlayerManager.PlayerTransform.position - WorldMover.currentMove)}";
        streamText.text = filter == "Health"
            ? $"<color=#7E8B92>NETWORK</color>\n{health.LatencyLatestMs}ms · jitter {health.LatencyJitterMs:0.0}ms\n<color=#7E8B92>DEBUG</color> {health.ObservabilityAverageMs:0.00}ms"
            : $"<color=#7E8B92>TRACE</color>\n{DebugRuntime.RuntimeSettings?.TraceMode} · 1/{DebugRuntime.RuntimeSettings?.HighFrequencySampling}\n<color=#7E8B92>PORT</color> {session?.FirehosePort}";
        timelineSection.SetActive(verbose || filter == "Packet");
        packetControls?.SetActive(filter == "Packet");
        packetModeControls?.SetActive(filter == "Packet");
        if (entityPaneLayout != null) entityPaneLayout.preferredWidth = filter == "Packet" ? 440 : 350;
        UpdateFilters();
        UpdateRows();
        UpdateInspector();
        ApplyVisualSettings();
        RefreshSettingsPanel();
        RefreshPacketControls();
        RefreshCopyMode();
        if (entityListContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(entityListContent);
    }

    private static bool EntityMatchesSearch(DebugEntityDto item)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return (item.EntityId?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (item.DisplayName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (item.EntityType?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static DebugEntityDto[] LocalInventoryEntities()
    {
        Inventory inventory = Inventory.Instance;
        if (inventory == null)
            return Array.Empty<DebugEntityDto>();
        List<(int slot, DebugEntityDto dto)> items = new();
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(item => item != null).Distinct())
        {
            int slot;
            try { slot = inventory.IndexOf(item.gameObject); }
            catch { continue; }
            if (slot < 0)
                continue;
            string id = item.NetId != 0 ? item.NetId.ToString() : $"unity:{item.gameObject.GetInstanceID()}";
            DebugEntityDto registered = item.NetId != 0 ? EntityDebugRegistry.Get("Item", item.NetId.ToString()) : null;
            items.Add((slot, new DebugEntityDto
            {
                EntityType = "Item",
                EntityId = id,
                DisplayName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
                Severity = registered?.Severity ?? DebugSeverity.Info,
                LastUpdatedUtc = DateTime.UtcNow,
                LatestState = EntityDebugRegistry.ItemState(item),
                Timeline = registered?.Timeline ?? new List<DebugEvent>()
            }));
        }
        return items.OrderBy(item => item.slot).Select(item => item.dto).ToArray();
    }

    private void RefreshPacketEvents()
    {
        if (packetPaused && packetEvents.Length > 0) return;
        packetSnapshot = DebugRuntime.Store?.Snapshot() ?? Array.Empty<DebugEvent>();
        IEnumerable<DebugEvent> visible = packetSnapshot.Where(item => item.Category == "packet").Where(PacketMatchesSearch)
            .Where(item => !mutedPacketTypes.Contains(PacketType(item)))
            .Where(item => showNoisyPackets || !string.IsNullOrWhiteSpace(search) || !defaultSuppressedPacketTypes.Contains(PacketType(item)))
            .Where(item => verbose || !string.IsNullOrWhiteSpace(search) || item.EventName is "packet.raw.receive" or "packet.raw.send");
        DebugEvent[] matches = visible.ToArray();
        packetEvents = matches.Skip(Math.Max(0, matches.Length - 1000)).Reverse().ToArray();
        if (packetEvents.Length > 0 && packetEvents[0].Sequence > traceModeChangedAfterSequence && selectedPacket != null && selectedPacket.Sequence <= traceModeChangedAfterSequence)
        {
            selectedPacketIndex = 0;
            selectedPacket = packetEvents[0];
        }
        int existing = selectedPacket == null ? -1 : Array.FindIndex(packetEvents, item => item.Sequence == selectedPacket.Sequence);
        if (existing >= 0) selectedPacketIndex = existing;
        else
        {
            selectedPacketIndex = Mathf.Clamp(selectedPacketIndex, 0, Math.Max(0, packetEvents.Length - 1));
            if (selectedPacket == null && packetEvents.Length > 0) selectedPacket = packetEvents[selectedPacketIndex];
        }
    }

    private static bool PacketMatchesSearch(DebugEvent item)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        string[] values = { PacketType(item), item.EventName, item.EntityType, item.EntityId, item.RuntimeSide.ToString(), DataText(item, "direction"), DataText(item, "delivery"), DataText(item, "peerId"), DataText(item, "channel"), DataText(item, "payloadFingerprint") };
        return values.Any(value => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IEnumerable<DebugEvent> PacketEventsForType(string packetType)
    {
        DebugEvent[] matches = packetSnapshot.Where(item => item.Category == "packet" && string.Equals(PacketType(item), packetType, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Skip(Math.Max(0, matches.Length - 500));
    }

    private static DebugEvent[] RelatedPacketEvents(DebugEvent selectedEvent)
    {
        if (selectedEvent == null) return Array.Empty<DebugEvent>();
        string packetType = PacketType(selectedEvent);
        string fingerprint = DataText(selectedEvent, "payloadFingerprint");
        return packetSnapshot.Where(item => item.Category == "packet" &&
                (string.Equals(PacketType(item), packetType, StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrEmpty(fingerprint) && string.Equals(DataText(item, "payloadFingerprint"), fingerprint, StringComparison.OrdinalIgnoreCase)) &&
                Math.Abs((item.TimestampUtc - selectedEvent.TimestampUtc).TotalSeconds) <= 0.35)
            .OrderBy(item => item.Sequence).Take(24).ToArray();
    }

    private static object PacketInspectionExport(DebugEvent selectedEvent) => new
    {
        traceMode = DebugRuntime.RuntimeSettings?.TraceMode.ToString() ?? "Summary",
        modeChangedAfterSequence = traceModeChangedAfterSequence,
        selectedEvent,
        relatedStages = RelatedPacketEvents(selectedEvent),
        note = "Decoded and Raw modes affect newly captured events only. Raw bytes are redacted for sensitive login packets."
    };

    private static string PacketType(DebugEvent item)
    {
        if (item == null) return string.Empty;
        string type = DataText(item, "packetType");
        if (!string.IsNullOrWhiteSpace(type)) return type;
        return item.EntityType == "Packet" ? item.EntityId : item.EntityType;
    }
    private static string DataText(DebugEvent item, string key) => item?.Data != null && item.Data.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : string.Empty;
    private static string ShortPacketName(string name) { int index = name?.LastIndexOf('.') ?? -1; return index < 0 ? name ?? string.Empty : name.Substring(index + 1); }

    private static string SelectionSummary()
    {
        if (filter == "Health") return Plain(FormatHealth());
        if (filter == "Packet") return selectedPacket == null ? "No packet selected." : PacketSummary(selectedPacket);
        if (selected == null) return "No entity selected.";
        double ageMs = Math.Max(0, (DateTime.UtcNow - selected.LastUpdatedUtc).TotalMilliseconds);
        return $"{selected.EntityType.ToUpperInvariant()} {selected.EntityId}  {selected.DisplayName}\n{(frozen ? "FROZEN" : "LIVE")} · {ageMs:0}ms\n\n" +
            Plain(FormatState(selected.LatestState) + (selected.EntityType == "Item" ? FormatItemDiagnostics(selected.EntityId) : string.Empty));
    }

    private static string PacketSummary(DebugEvent item)
    {
        StringBuilder builder = new();
        builder.Append($"PACKET {PacketType(item)}  {item.EventName}  #{item.Sequence}\n")
            .Append($"{item.TimestampUtc:O} · {DataText(item, "direction")} · {item.RuntimeSide} · peer {DataText(item, "peerId")} · channel {DataText(item, "channel")} · {DataText(item, "rawLength")} bytes\n");
        foreach (DebugEvent stage in RelatedPacketEvents(item)) builder.Append('\n').Append(EventSummary(stage, true));
        return builder.ToString().TrimEnd();
    }

    private static string PacketTimelineSummary(IEnumerable<DebugEvent> events) => RecentEventSummary(events, true);
    private static string EntityTimelineSummary(IEnumerable<DebugEvent> events) => RecentEventSummary(events, false);
    private static string RecentEventSummary(IEnumerable<DebugEvent> events, bool packet)
    {
        DebugEvent[] items = events?.ToArray() ?? Array.Empty<DebugEvent>();
        return string.Join("\n", items.Skip(Math.Max(0, items.Length - 60)).Select(item => EventSummary(item, packet)));
    }
    private static string EventSummary(DebugEvent item, bool packet) => packet
        ? $"{item.TimestampUtc:HH:mm:ss.fff}  {DataText(item, "direction")}  {item.EventName}  #{item.Sequence} {DataText(item, "rawLength")} bytes"
        : $"{item.TimestampUtc:HH:mm:ss.fff}  {item.EventName}  {Plain(FormatInline(item.Data))}";

    private static readonly Regex RichTextTag = new("<[^>]+>", RegexOptions.Compiled);
    private static string Plain(string value) => string.IsNullOrEmpty(value) ? value : RichTextTag.Replace(value, string.Empty);

    private void TogglePacketPause() { packetPaused = !packetPaused; if (!packetPaused) packetEvents = Array.Empty<DebugEvent>(); Refresh(true); }
    private void ToggleNoisyPackets() { showNoisyPackets = !showNoisyPackets; packetEvents = Array.Empty<DebugEvent>(); Refresh(true); }
    private void ToggleSelectedPacketMute()
    {
        string type = PacketType(selectedPacket); if (string.IsNullOrWhiteSpace(type)) return;
        if (!mutedPacketTypes.Add(type)) mutedPacketTypes.Remove(type);
        packetEvents = Array.Empty<DebugEvent>(); Refresh(true);
    }
    private void ToggleSelectedPacketTrace()
    {
        string type = PacketType(selectedPacket); if (string.IsNullOrWhiteSpace(type)) return;
        DebugTrace.TracePacket(type, !DebugTrace.IsPacketTraced(type)); Refresh(true);
    }
    private void SetPacketTraceMode(DebugTraceMode mode)
    {
        if (DebugRuntime.RuntimeSettings == null) return;
        DebugRuntime.RuntimeSettings.TraceMode = mode;
        DebugRuntime.RuntimeSettings.RawPacketCapture = mode == DebugTraceMode.Raw;
        traceModeChangedAfterSequence = DebugRuntime.Store?.Snapshot().LastOrDefault()?.Sequence ?? 0;
        Refresh(true);
    }
    private void RefreshPacketControls()
    {
        SetPacketButton(noisyPacketsButton, showNoisyPackets ? "NOISY ON" : "NOISY OFF", showNoisyPackets);
        SetPacketButton(pausePacketsButton, packetPaused ? "RESUME" : "PAUSE", packetPaused);
        string type = PacketType(selectedPacket);
        SetPacketButton(mutePacketButton, mutedPacketTypes.Contains(type) ? "UNMUTE" : "MUTE", mutedPacketTypes.Contains(type));
        SetPacketButton(tracePacketButton, DebugTrace.IsPacketTraced(type) ? "TRACED" : "TRACE", DebugTrace.IsPacketTraced(type));
        DebugTraceMode mode = DebugRuntime.RuntimeSettings?.TraceMode ?? DebugTraceMode.Summary;
        SetPacketButton(summaryModeButton, "SUMMARY", mode == DebugTraceMode.Summary);
        SetPacketButton(decodedModeButton, "DECODED", mode == DebugTraceMode.Decoded);
        SetPacketButton(rawModeButton, "RAW", mode == DebugTraceMode.Raw);
    }
    private void RefreshCopyMode() => SetPacketButton(copyModeButton, summaryCopy ? "COPY: SUMMARY" : "COPY: FULL", summaryCopy);
    private static void SetPacketButton(Button button, string caption, bool active)
    {
        if (button == null) return; TMP_Text text = button.GetComponentInChildren<TMP_Text>(); if (text != null) text.text = caption; button.GetComponent<Image>().color = active ? Selected : PanelLight;
    }

    private void UpdateFilters()
    {
        foreach (var pair in filterButtons) pair.Value.color = pair.Key == filter ? Selected : PanelLight;
    }

    private void UpdateRows()
    {
        if (filter == "Health")
        {
            foreach (EntityRow row in rows) row.Root.SetActive(false);
            return;
        }
        if (filter == "Packet")
        {
            int packetCount = Math.Min(rows.Count, packetEvents.Length);
            int packetStart = Mathf.Clamp(selectedPacketIndex - rows.Count / 2, 0, Math.Max(0, packetEvents.Length - rows.Count));
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                EntityRow row = rows[rowIndex]; row.Layout.preferredHeight = 54; bool active = rowIndex < packetCount; row.Root.SetActive(active); if (!active) continue;
                int packetIndex = packetStart + rowIndex; DebugEvent packet = packetEvents[packetIndex]; string type = PacketType(packet); string direction = DataText(packet, "direction");
                string payload = packet.Data.ContainsKey("rawBase64") ? "RAW" : packet.Data.ContainsKey("decoded") ? "DECODED" : !string.IsNullOrEmpty(DataText(packet, "rawLength")) ? DataText(packet, "rawLength") + " bytes" : "stage";
                row.Text.text = $"<color=#78858C>{packet.TimestampUtc:HH:mm:ss.fff}</color>  <color=#26B861>{(direction == "inbound" ? "IN" : direction == "outbound" ? "OUT" : packet.RuntimeSide.ToString().ToUpperInvariant())}</color>  {type}\n<size=12><color=#78858C>{packet.EventName} · {payload} · peer {DataText(packet, "peerId")}</color></size>";
                row.Background.color = selectedPacket?.Sequence == packet.Sequence ? Selected : packetIndex % 2 == 0 ? Panel : PanelLight;
                row.Button.onClick.RemoveAllListeners(); row.Button.onClick.AddListener(() => { selectedPacketIndex = packetIndex; selectedPacket = packet; Refresh(true); });
            }
            return;
        }
        int count = Math.Min(rows.Count, filteredEntities.Length);
        int start = Mathf.Clamp(selectedIndex - rows.Count / 2, 0, Math.Max(0, filteredEntities.Length - rows.Count));
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            EntityRow row = rows[rowIndex];
            row.Layout.preferredHeight = 34;
            bool active = rowIndex < count;
            row.Root.SetActive(active);
            if (!active) continue;
            int entityIndex = start + rowIndex;
            DebugEntityDto entity = filteredEntities[entityIndex];
            if (filter == "Inventory")
            {
                Dictionary<string, object> state = entity.LatestState;
                string flags = StateBool(state, "inventorySlotDropped") ? "SILHOUETTE" : "STORED";
                if (StateBool(state, "claimStolen")) flags += " · STOLEN";
                if (StateBool(state, "foreignOwned")) flags += " · FOREIGN";
                row.Text.text = $"<color=#78858C>SLOT {StateValue(state, "inventorySlot", -1),2}</color>  <color=#26B861>{entity.EntityId}</color>  {entity.DisplayName}\n" +
                    $"<size=12><color=#78858C>{flags} · owner P{StateValue(state, "persistentOwnerPlayerId", 0)} · claim {StateValue(state, "inventoryClaimSlot", -1)}</color></size>";
                row.Layout.preferredHeight = 50;
            }
            else
                row.Text.text = $"<color=#78858C>{entity.EntityType.ToUpperInvariant(),-8}</color>  <color=#26B861>{entity.EntityId}</color>  {entity.DisplayName}" +
                    (entity.Severity >= DebugSeverity.Warning ? $"  <color=#FFB84D>{entity.Severity}</color>" : string.Empty);
            row.Background.color = IsSelected(entity.EntityType, entity.EntityId) ? Selected : entityIndex % 2 == 0 ? Panel : PanelLight;
            row.Button.onClick.RemoveAllListeners();
            row.Button.onClick.AddListener(() => { selectedIndex = entityIndex; SelectVisibleEntity(entity); });
        }
    }

    private void UpdateInspector()
    {
        if (filter == "Health")
        {
            detailsText.supportRichText = true;
            selectedTitle.text = "<color=#78858C>MULTIPLAYER</color>  <color=#26B861>HEALTH & AUTOMATION</color>";
            detailsText.text = FormatHealth();
            timelineText.text = string.Empty;
            return;
        }
        if (filter == "Packet")
        {
            detailsText.supportRichText = false;
            if (selectedPacket == null)
            {
                selectedTitle.text = "<color=#78858C>NO PACKET EVENT SELECTED</color>";
                detailsText.text = packetEvents.Length == 0 ? "No visible packet events. Try a search, enable NOISY packets, or wait for traffic." : "Select a packet event from the list.";
                timelineText.text = string.Empty;
                return;
            }
            string type = PacketType(selectedPacket); string fullJson = DebugJson.Serialize(PacketInspectionExport(selectedPacket), Formatting.Indented);
            const int maximumDisplayedPacketCharacters = 65536;
            detailsText.text = fullJson.Length <= maximumDisplayedPacketCharacters ? fullJson : fullJson.Substring(0, maximumDisplayedPacketCharacters) + "\n\n... display truncated at 64 KiB; switch to COPY: FULL and use COPY VIEW for the complete detached event.";
            string flags = (defaultSuppressedPacketTypes.Contains(type) ? "NOISY " : string.Empty) + (DebugTrace.IsPacketTraced(type) ? "TRACED " : string.Empty) + (mutedPacketTypes.Contains(type) ? "MUTED" : string.Empty);
            string packetCopyStatus = Time.unscaledTime < clipboardStatusUntil ? $"     <color=#26B861>{clipboardStatus}</color>" : string.Empty;
            string capturedBeforeModeChange = selectedPacket.Sequence <= traceModeChangedAfterSequence ? " · PREVIOUS MODE" : string.Empty;
            selectedTitle.text = $"<color=#78858C>PACKET</color>  <color=#26B861>{type}</color>  {selectedPacket.EventName}  <color=#78858C>#{selectedPacket.Sequence} · {DebugRuntime.RuntimeSettings?.TraceMode}{capturedBeforeModeChange} {flags}</color>{packetCopyStatus}";
            DebugEvent[] sameType = PacketEventsForType(type).ToArray();
            timelineText.text = string.Join("\n", sameType.Skip(Math.Max(0, sameType.Length - 60)).Select(item =>
                $"<color=#78858C>{item.TimestampUtc:HH:mm:ss.fff}</color>  <color=#26B861>{DataText(item, "direction")}</color>  {item.EventName}  <color=#78858C>#{item.Sequence} {DataText(item, "rawLength")} bytes</color>"));
            return;
        }
        detailsText.supportRichText = true;
        if (selected == null)
        {
            selectedTitle.text = "<color=#78858C>NO ENTITY SELECTED</color>";
            detailsText.text = "Aim at an item, player, or train and press <color=#26B861>F12</color>, or use the entity list." +
                (Time.unscaledTime < clipboardStatusUntil ? $"\n\n<color=#26B861>{clipboardStatus}</color>" : string.Empty);
            timelineText.text = string.Empty;
            return;
        }
        string inspectorKey = selected.EntityType + ":" + selected.EntityId;
        if (lastInspectorKey != inspectorKey)
        {
            lastInspectorKey = inspectorKey; previousInspectorState.Clear(); changedInspectorFields.Clear();
        }
        foreach (var pair in selected.LatestState)
        {
            string current = DebugJson.Serialize(pair.Value);
            if (previousInspectorState.TryGetValue(pair.Key, out string previous) && previous != current) changedInspectorFields[pair.Key] = Time.unscaledTime + 1.5f;
            previousInspectorState[pair.Key] = current;
        }
        foreach (string key in changedInspectorFields.Where(pair => pair.Value < Time.unscaledTime).Select(pair => pair.Key).ToArray()) changedInspectorFields.Remove(key);
        double ageMs = Math.Max(0, (DateTime.UtcNow - selected.LastUpdatedUtc).TotalMilliseconds);
        string copyStatus = Time.unscaledTime < clipboardStatusUntil ? $"     <color=#26B861>{clipboardStatus}</color>" : string.Empty;
        selectedTitle.text = $"<color=#78858C>{selected.EntityType.ToUpperInvariant()}</color>  <color=#26B861>{selected.EntityId}</color>  {selected.DisplayName}" +
            $"     <color=#78858C>{(frozen ? "FROZEN" : "LIVE")} · {ageMs:0}ms</color>{copyStatus}";
        detailsText.text = FormatState(selected.LatestState, changedInspectorFields.Keys) +
            (selected.EntityType == "Item" ? FormatItemDiagnostics(selected.EntityId) : string.Empty);
        int skip = Math.Max(0, selected.Timeline.Count - 60);
        timelineText.text = string.Join("\n", selected.Timeline.Skip(skip).Select(item =>
            $"<color=#78858C>{item.TimestampUtc:HH:mm:ss.fff}</color>  <color=#26B861>{item.EventName}</color>  {FormatInline(item.Data)}"));
    }

    private void BuildUi()
    {
        canvasRoot = new GameObject("DVMP Observability UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(canvasRoot);
        Canvas canvas = canvasRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = short.MaxValue;
        canvasScaler = canvasRoot.GetComponent<CanvasScaler>(); canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; canvasScaler.referenceResolution = new Vector2(1920, 1080); canvasScaler.matchWidthOrHeight = 0.5f;

        GameObject panel = Ui("Panel", canvasRoot.transform, Panel, true);
        panelRect = panel.GetComponent<RectTransform>(); ApplyPanelSize();
        VerticalLayoutGroup panelLayout = panel.AddComponent<VerticalLayoutGroup>(); Configure(panelLayout, 0, 0); panelLayout.padding = new RectOffset(1, 1, 1, 1);

        GameObject header = Group(panel.transform, "Header", true, Header, 46); header.AddComponent<DebugPanelDragger>().Target = panelRect;
        sessionText = Label(header.transform, "Session", 18, TextAnchor.MiddleLeft, FontStyle.Bold); Flex(sessionText.gameObject, 1);
        AddToolbarButton(header.transform, "SETTINGS F6", ToggleSettings, 108);
        AddToolbarButton(header.transform, "SIZE F7", ToggleSize, 82);
        AddToolbarButton(header.transform, "LABELS", () => DebugWorldLabelManager.SetEnabled(!DebugWorldLabelManager.Enabled), 90);
        AddToolbarButton(header.transform, "FREEZE", ToggleFreeze, 90);
        AddToolbarButton(header.transform, "VERBOSE", ToggleVerbose, 100);
        AddToolbarButton(header.transform, "GAZE F12", () => SelectLookedAt(), 100);
        AddToolbarButton(header.transform, "CLOSE F8", Toggle, 90, new Color(0.35f, 0.12f, 0.12f));

        GameObject main = Group(panel.transform, "Main", true, Color.clear, -1); Flex(main, 1);
        GameObject left = Group(main.transform, "Entities", false, PanelLight, -1); Fixed(left, 350, -1); entityPaneLayout = left.GetComponent<LayoutElement>(); FlexHeight(left, 1);
        searchInput = SearchField(left.transform); Fixed(searchInput.gameObject, -1, 38);
        GameObject filters = Group(left.transform, "Filters", true, Header, 38);
        AddFilter(filters.transform, "All", "0 ALL"); AddFilter(filters.transform, "Item", "1 ITEMS"); AddFilter(filters.transform, "Player", "2 PLAYERS"); AddFilter(filters.transform, "TrainCar", "3 TRAINS");
        GameObject diagnosticFilters = Group(left.transform, "DiagnosticFilters", true, Header, 38);
        AddFilter(diagnosticFilters.transform, "Packet", "4 PACKETS"); AddFilter(diagnosticFilters.transform, "Health", "5 HEALTH"); AddFilter(diagnosticFilters.transform, "Inventory", "6 INVENTORY");
        packetControls = Group(left.transform, "PacketControls", true, Header, 38);
        noisyPacketsButton = AddSettingsButton(packetControls.transform, "NOISY", ToggleNoisyPackets);
        pausePacketsButton = AddSettingsButton(packetControls.transform, "PAUSE", TogglePacketPause);
        mutePacketButton = AddSettingsButton(packetControls.transform, "MUTE", ToggleSelectedPacketMute);
        tracePacketButton = AddSettingsButton(packetControls.transform, "TRACE", ToggleSelectedPacketTrace);
        packetControls.SetActive(false);
        packetModeControls = Group(left.transform, "PacketModes", true, Header, 38);
        summaryModeButton = AddSettingsButton(packetModeControls.transform, "SUMMARY", () => SetPacketTraceMode(DebugTraceMode.Summary));
        decodedModeButton = AddSettingsButton(packetModeControls.transform, "DECODED", () => SetPacketTraceMode(DebugTraceMode.Decoded));
        rawModeButton = AddSettingsButton(packetModeControls.transform, "RAW", () => SetPacketTraceMode(DebugTraceMode.Raw));
        packetModeControls.SetActive(false);
        GameObject list = Scroll(left.transform, "EntityList", out entityListContent); Flex(list, 1);
        for (int index = 0; index < 32; index++) rows.Add(AddEntityRow(entityListContent));

        GameObject right = Group(main.transform, "Inspector", false, Panel, -1); Flex(right, 1);
        GameObject cards = Group(right.transform, "Cards", true, Header, 82);
        entityCountText = Card(cards.transform); worldText = Card(cards.transform); streamText = Card(cards.transform);
        GameObject selectedHeader = Group(right.transform, "SelectedTitle", true, PanelLight, 42);
        selectedTitle = Label(selectedHeader.transform, "Title", 18, TextAnchor.MiddleLeft, FontStyle.Bold); Flex(selectedTitle.gameObject, 1);
        copyModeButton = AddToolbarButton(selectedHeader.transform, "COPY: SUMMARY", ToggleCopyMode, 128);
        AddToolbarButton(selectedHeader.transform, "COPY VIEW", () => CopySelected(), 92);
        AddToolbarButton(selectedHeader.transform, "COPY EVENTS", () => CopyTimeline(), 108);
        AddToolbarButton(selectedHeader.transform, "COPY LAST", () => CopyLastEvent(), 92);
        AddToolbarButton(selectedHeader.transform, "COPY FLOW", () => CopyReplicationFlow(), 92);
        AddToolbarButton(selectedHeader.transform, "COPY MATRIX", () => CopyInterestMatrix(), 102);
        AddToolbarButton(selectedHeader.transform, "COPY BUNDLE", () => CopyDiagnosticBundle(), 112);
        GameObject details = TextScroll(right.transform, "Details", out detailsText); Flex(details, 2);
        timelineSection = Group(right.transform, "TimelineSection", false, Header, -1); Flex(timelineSection, 1);
        Text timelineHeader = Label(timelineSection.transform, "TimelineHeader", 15, TextAnchor.MiddleLeft, FontStyle.Bold); timelineHeader.text = "EVENT TIMELINE  <color=#78858C>LAST 60</color>"; Fixed(timelineHeader.gameObject, -1, 32);
        GameObject timeline = TextScroll(timelineSection.transform, "Timeline", out timelineText); Flex(timeline, 1);
        Text footer = Label(Group(panel.transform, "Footer", true, Header, 34).transform, "Help", 14, TextAnchor.MiddleLeft);
        footer.text = "  <color=#26B861>F6</color> settings   <color=#26B861>F7</color> size   <color=#26B861>F8</color> toggle   <color=#26B861>F9</color> labels   <color=#26B861>F10</color> freeze   <color=#26B861>F11</color> verbose   <color=#26B861>F12</color> gaze   <color=#26B861>Ctrl+F</color> search   <color=#26B861>Ctrl+C</color> copy   <color=#26B861>0-5</color> tabs";
        BuildSettingsPanel();
        ApplyVisualSettings();
    }

    private void BuildSettingsPanel()
    {
        settingsPanel = Group(canvasRoot.transform, "DisplaySettings", false, Panel, -1);
        RectTransform rect = settingsPanel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.33f, 0.28f); rect.anchorMax = new Vector2(0.67f, 0.88f); rect.offsetMin = rect.offsetMax = Vector2.zero;

        GameObject header = Group(settingsPanel.transform, "Header", true, Header, 42);
        Text title = Label(header.transform, "Title", 18, TextAnchor.MiddleLeft, FontStyle.Bold); title.text = "DISPLAY & LABEL SETTINGS"; Flex(title.gameObject, 1);
        AddToolbarButton(header.transform, "SAVE", SaveDisplaySettings, 70);
        AddToolbarButton(header.transform, "CLOSE", ToggleSettings, 72, new Color(0.35f, 0.12f, 0.12f));

        settingsText = Label(settingsPanel.transform, "Summary", 15, TextAnchor.UpperLeft); Fixed(settingsText.gameObject, -1, 70);
        AddSettingStepper(settingsPanel.transform, "WORLD LABEL SCALE", () => AdjustLabelScale(-0.1f), () => AdjustLabelScale(0.1f));
        AddSettingStepper(settingsPanel.transform, "LABEL RADIUS", () => AdjustRadius(-5f), () => AdjustRadius(5f));
        AddSettingStepper(settingsPanel.transform, "MAXIMUM LABELS", () => AdjustMaxLabels(-8), () => AdjustMaxLabels(8));
        AddSettingStepper(settingsPanel.transform, "LABEL UPDATE RATE", () => AdjustUpdateRate(-1f), () => AdjustUpdateRate(1f));
        AddSettingStepper(settingsPanel.transform, "OVERLAY UI SCALE", () => AdjustUiScale(-0.05f), () => AdjustUiScale(0.05f));

        GameObject toggles = Group(settingsPanel.transform, "Toggles", true, PanelLight, 38);
        labelsToggleButton = AddSettingsButton(toggles.transform, "LABELS", () => { DebugWorldLabelManager.SetEnabled(!DebugWorldLabelManager.Enabled); RefreshSettingsPanel(); });
        throughWallsButton = AddSettingsButton(toggles.transform, "THROUGH WALLS", () => { Multiplayer.Settings.DebugLabelsThroughWalls = !Multiplayer.Settings.DebugLabelsThroughWalls; RefreshSettingsPanel(); });
        errorsOnlyButton = AddSettingsButton(toggles.transform, "ERRORS ONLY", () => { DebugWorldLabelManager.SetErrorsOnly(!DebugWorldLabelManager.ErrorsOnly); RefreshSettingsPanel(); });

        GameObject types = Group(settingsPanel.transform, "Types", true, PanelLight, 38);
        itemsToggleButton = AddSettingsButton(types.transform, "ITEMS", () => ToggleLabelType("Item"));
        playersToggleButton = AddSettingsButton(types.transform, "PLAYERS", () => ToggleLabelType("Player"));
        trainsToggleButton = AddSettingsButton(types.transform, "TRAINS", () => ToggleLabelType("TrainCar"));

        Text help = Label(settingsPanel.transform, "Help", 13, TextAnchor.MiddleLeft);
        help.text = "Keyboard: -/+ label scale, ,/. radius, Ctrl+S save, Esc close"; Fixed(help.gameObject, -1, 30);
        settingsPanel.SetActive(settingsVisible);
        RefreshSettingsPanel();
    }

    private void AddSettingStepper(Transform parent, string caption, Action decrease, Action increase)
    {
        GameObject row = Group(parent, caption, true, PanelLight, 38);
        Text label = Label(row.transform, "Label", 15, TextAnchor.MiddleLeft, FontStyle.Bold); label.text = caption; Flex(label.gameObject, 1);
        AddToolbarButton(row.transform, "-", decrease, 44);
        AddToolbarButton(row.transform, "+", increase, 44);
    }

    private Button AddSettingsButton(Transform parent, string caption, Action action)
    {
        Button button = MakeButton(parent, caption, PanelLight); Flex(button.gameObject, 1); button.onClick.AddListener(() => action()); return button;
    }

    private static void SetButtonCaption(Button button, string caption, bool enabled)
    {
        if (button == null) return;
        TMP_Text text = button.GetComponentInChildren<TMP_Text>(); if (text != null) text.text = caption + (enabled ? "  ON" : "  OFF");
        button.GetComponent<Image>().color = enabled ? Selected : PanelLight;
    }

    private void RefreshSettingsPanel()
    {
        if (settingsText == null || Multiplayer.Settings == null) return;
        settingsText.text = $"Label scale: <color=#26B861>{Multiplayer.Settings.DebugWorldLabelScale:0.00}x</color>   Radius: <color=#26B861>{Multiplayer.Settings.DebugWorldLabelRadius:0}m</color>   Max: <color=#26B861>{Multiplayer.Settings.DebugWorldLabelMaxCount}</color>\n" +
            $"Update: <color=#26B861>{Multiplayer.Settings.DebugWorldLabelUpdateHz:0.#} Hz</color>   UI scale: <color=#26B861>{Multiplayer.Settings.DebugOverlayUiScale:0.00}x</color>   Mode: <color=#26B861>{(compact ? "compact" : "expanded")}</color>";
        SetButtonCaption(labelsToggleButton, "LABELS", DebugWorldLabelManager.Enabled);
        SetButtonCaption(throughWallsButton, "THROUGH WALLS", Multiplayer.Settings.DebugLabelsThroughWalls);
        SetButtonCaption(errorsOnlyButton, "ERRORS ONLY", DebugWorldLabelManager.ErrorsOnly);
        SetButtonCaption(itemsToggleButton, "ITEMS", DebugWorldLabelManager.IsTypeEnabled("Item"));
        SetButtonCaption(playersToggleButton, "PLAYERS", DebugWorldLabelManager.IsTypeEnabled("Player"));
        SetButtonCaption(trainsToggleButton, "TRAINS", DebugWorldLabelManager.IsTypeEnabled("TrainCar"));
    }

    private void ToggleLabelType(string type) { DebugWorldLabelManager.SetType(type, !DebugWorldLabelManager.IsTypeEnabled(type)); RefreshSettingsPanel(); }
    private void AdjustLabelScale(float amount) { Multiplayer.Settings.DebugWorldLabelScale = Mathf.Clamp(Multiplayer.Settings.DebugWorldLabelScale + amount, 0.5f, 3f); RefreshSettingsPanel(); }
    private void AdjustRadius(float amount) { Multiplayer.Settings.DebugWorldLabelRadius = Mathf.Clamp(Multiplayer.Settings.DebugWorldLabelRadius + amount, 5f, 500f); RefreshSettingsPanel(); }
    private void AdjustMaxLabels(int amount) { Multiplayer.Settings.DebugWorldLabelMaxCount = Mathf.Clamp(Multiplayer.Settings.DebugWorldLabelMaxCount + amount, 1, 128); RefreshSettingsPanel(); }
    private void AdjustUpdateRate(float amount) { Multiplayer.Settings.DebugWorldLabelUpdateHz = Mathf.Clamp(Multiplayer.Settings.DebugWorldLabelUpdateHz + amount, 1f, 10f); RefreshSettingsPanel(); }
    private void AdjustUiScale(float amount) { Multiplayer.Settings.DebugOverlayUiScale = Mathf.Clamp(Multiplayer.Settings.DebugOverlayUiScale + amount, 0.75f, 1.5f); ApplyVisualSettings(); RefreshSettingsPanel(); }
    private void ApplyVisualSettings()
    {
        if (canvasScaler == null || Multiplayer.Settings == null) return;
        float scale = Mathf.Clamp(Multiplayer.Settings.DebugOverlayUiScale, 0.75f, 1.5f);
        canvasScaler.referenceResolution = new Vector2(1920f / scale, 1080f / scale);
    }
    private void SaveDisplaySettings() { Multiplayer.Settings?.Save(Multiplayer.ModEntry); clipboardStatus = "DISPLAY SETTINGS SAVED"; clipboardStatusUntil = Time.unscaledTime + 2f; Refresh(true); }

    private void ApplyPanelSize()
    {
        if (panelRect == null) return;
        // Compact mode leaves useful portions of the game view visible around the inspector.
        panelRect.anchorMin = compact ? new Vector2(0.025f, 0.25f) : new Vector2(0.04f, 0.06f);
        panelRect.anchorMax = compact ? new Vector2(0.76f, 0.94f) : new Vector2(0.96f, 0.94f);
        panelRect.offsetMin = panelRect.offsetMax = Vector2.zero;
    }

    private void AddFilter(Transform parent, string value, string caption)
    {
        Button button = MakeButton(parent, caption, PanelLight); Flex(button.gameObject, 1); button.onClick.AddListener(() => SetFilter(value)); filterButtons[value] = button.GetComponent<Image>();
    }
    private static InputField SearchField(Transform parent)
    {
        GameObject root = Ui("Search", parent, Header, true); InputField input = root.AddComponent<InputField>(); input.lineType = InputField.LineType.SingleLine;
        Text placeholder = Label(root.transform, "Placeholder", 14, TextAnchor.MiddleLeft); placeholder.text = "Search ID, name, packet type or stage..."; placeholder.color = Muted; Stretch(placeholder.rectTransform, 9);
        Text value = Label(root.transform, "Value", 14, TextAnchor.MiddleLeft); value.color = Foreground; value.supportRichText = false; Stretch(value.rectTransform, 9);
        input.placeholder = placeholder; input.textComponent = value; input.onValueChanged.AddListener(SetSearch); return input;
    }
    private Button AddToolbarButton(Transform parent, string caption, Action action, float width, Color? color = null)
    { Button button = MakeButton(parent, caption, color ?? PanelLight); Fixed(button.gameObject, width, 30); button.onClick.AddListener(() => action()); return button; }
    private EntityRow AddEntityRow(Transform parent)
    {
        Button button = MakeButton(parent, string.Empty, Panel); Fixed(button.gameObject, -1, 34); FlexWidth(button.gameObject, 1); TMP_Text text = button.GetComponentInChildren<TMP_Text>(); text.alignment = TextAlignmentOptions.MidlineLeft; text.fontSize = 15;
        return new EntityRow { Root = button.gameObject, Button = button, Text = text, Background = button.GetComponent<Image>(), Layout = button.GetComponent<LayoutElement>() };
    }
    private static Text Card(Transform parent) { Text text = Label(Ui("Card", parent, PanelLight, false).transform, "Text", 15, TextAnchor.MiddleLeft); Stretch(text.rectTransform, 10); Flex(text.transform.parent.gameObject, 1); return text; }

    private static GameObject Ui(string name, Transform parent, Color color, bool raycast)
    {
        GameObject go = new(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
        if (color.a > 0) { Image image = go.AddComponent<Image>(); image.color = color; image.raycastTarget = raycast; }
        return go;
    }
    private static GameObject Group(Transform parent, string name, bool horizontal, Color color, float height)
    {
        GameObject go = Ui(name, parent, color, false);
        HorizontalOrVerticalLayoutGroup layout = horizontal ? go.AddComponent<HorizontalLayoutGroup>() : go.AddComponent<VerticalLayoutGroup>(); Configure(layout, 4, 8);
        if (height > 0) Fixed(go, -1, height); return go;
    }
    private static void Configure(HorizontalOrVerticalLayoutGroup layout, float spacing, int padding)
    { layout.spacing = spacing; layout.padding = new RectOffset(padding, padding, padding, padding); layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = layout.childForceExpandHeight = false; }
    private static Text Label(Transform parent, string name, int size, TextAnchor anchor, FontStyle style = FontStyle.Normal)
    {
        GameObject go = Ui(name, parent, Color.clear, false); Text text = go.AddComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); text.fontSize = size; text.fontStyle = style; text.alignment = anchor; text.color = Foreground; text.supportRichText = true; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow; return text;
    }
    private static Button MakeButton(Transform parent, string caption, Color color)
    {
        GameObject go = Ui(caption, parent, color, true); Button button = go.AddComponent<Button>(); ColorBlock block = button.colors; block.normalColor = Color.white; block.highlightedColor = new Color(1.15f, 1.15f, 1.15f); block.pressedColor = new Color(0.75f, 0.75f, 0.75f); block.colorMultiplier = 1; button.colors = block;
        GameObject captionObject = Ui("Caption", go.transform, Color.clear, false);
        TextMeshProUGUI text = captionObject.AddComponent<TextMeshProUGUI>();
        text.text = caption; text.fontSize = 14; text.fontStyle = FontStyles.Bold; text.alignment = TextAlignmentOptions.Center; text.color = Foreground; text.richText = true; text.raycastTarget = false;
        Stretch(text.rectTransform, 4); text.rectTransform.SetAsLastSibling();
        return button;
    }
    private static GameObject Scroll(Transform parent, string name, out RectTransform content)
    {
        GameObject root = Ui(name, parent, Header, false); ScrollRect scroll = root.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 55f;
        GameObject viewport = Ui("Viewport", root.transform, Color.clear, true); Stretch(viewport.GetComponent<RectTransform>(), 2); viewport.AddComponent<RectMask2D>();
        GameObject contentGo = Ui("Content", viewport.transform, Color.clear, false); content = contentGo.GetComponent<RectTransform>(); content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1); content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
        VerticalLayoutGroup layout = contentGo.AddComponent<VerticalLayoutGroup>(); Configure(layout, 1, 2); layout.childForceExpandWidth = true; layout.childAlignment = TextAnchor.UpperLeft; ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport.GetComponent<RectTransform>(); scroll.content = content; return root;
    }
    private static GameObject TextScroll(Transform parent, string name, out Text text)
    {
        GameObject root = Ui(name, parent, Header, false); ScrollRect scroll = root.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.scrollSensitivity = 55f;
        GameObject viewport = Ui("Viewport", root.transform, Color.clear, true); Stretch(viewport.GetComponent<RectTransform>(), 6); viewport.AddComponent<RectMask2D>();
        text = Label(viewport.transform, "Content", 15, TextAnchor.UpperLeft); RectTransform rect = text.rectTransform; rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(0.5f, 1); rect.offsetMin = rect.offsetMax = Vector2.zero;
        ContentSizeFitter fitter = text.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize; scroll.viewport = viewport.GetComponent<RectTransform>(); scroll.content = rect; return root;
    }
    private static void Stretch(RectTransform rect, float inset) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset); }
    private static void Fixed(GameObject go, float width, float height) { LayoutElement e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); if (width > 0) e.preferredWidth = width; if (height > 0) e.preferredHeight = height; e.flexibleWidth = width > 0 ? 0 : e.flexibleWidth; e.flexibleHeight = height > 0 ? 0 : e.flexibleHeight; }
    private static void Flex(GameObject go, float value) { LayoutElement e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); e.flexibleWidth = e.flexibleHeight = value; }
    private static void FlexWidth(GameObject go, float value) { LayoutElement e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); e.flexibleWidth = value; }
    private static void FlexHeight(GameObject go, float value) { LayoutElement e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); e.flexibleHeight = value; }

    private static object DiagnosticExport() => new
    {
        health = DebugDiagnostics.HealthSnapshot(),
        queues = DebugDiagnostics.QueueSnapshot(),
        dependencies = DebugDiagnostics.DependencySnapshot(),
        identity = DebugDiagnostics.IdentitySnapshot(),
        automaticCapturesEnabled = DebugDiagnostics.AutomaticCapturesEnabled,
        captureActive = DebugRuntime.Captures?.IsCapturing ?? false,
        capturePath = DebugRuntime.Captures?.CurrentPath ?? string.Empty
    };

    private static string FormatHealth()
    {
        DebugDiagnostics.NetworkHealth health = DebugDiagnostics.HealthSnapshot();
        DebugDiagnostics.QueueHealth[] queues = DebugDiagnostics.QueueSnapshot();
        DebugDiagnostics.DependencyHealth[] dependencies = DebugDiagnostics.DependencySnapshot();
        DebugDiagnostics.IdentityIssue[] identity = DebugDiagnostics.IdentitySnapshot();
        StringBuilder builder = new();
        builder.Append("<color=#26B861><b>NETWORK / TICK</b></color>\n")
            .Append($"Latency: {health.LatencyLatestMs} ms   avg {health.LatencyAverageMs:0.0}   jitter {health.LatencyJitterMs:0.0}   max {health.LatencyMaximumMs}\n")
            .Append($"Tick interval: {health.TickIntervalAverageMs:0.00} ms   jitter avg/max {health.TickJitterAverageMs:0.00}/{health.TickJitterMaximumMs:0.00} ms\n")
            .Append($"Inbound: {health.PacketsInPerSecond:N0} packets/s   {health.BytesInPerSecond:N0} bytes/s\n")
            .Append($"Outbound: {health.PacketsOutPerSecond:N0} packets/s   {health.BytesOutPerSecond:N0} bytes/s\n\n")
            .Append("<color=#26B861><b>OBSERVABILITY SELF-PROFILER</b></color>\n")
            .Append($"Debug work avg/max: {health.ObservabilityAverageMs:0.000}/{health.ObservabilityMaximumMs:0.000} ms\n")
            .Append($"Runtime frame avg/max: {health.FrameAverageMs:0.000}/{health.FrameMaximumMs:0.000} ms   events/s {health.EventsPerSecond:N0}\n")
            .Append($"Automatic captures: {(DebugDiagnostics.AutomaticCapturesEnabled ? "ON" : "OFF")}   active: {DebugRuntime.Captures?.IsCapturing == true}   triggered: {health.AutoCaptureCount}\n")
            .Append($"Last trigger: {health.LastAutoTrigger}\n")
            .Append("<color=#78858C>Press A or use 'debug auto on|off'. Captures are opt-in, last 8 seconds, have a 60-second global cooldown, a 5-minute per-condition cooldown, and a 10-per-session limit.</color>\n\n")
            .Append($"<color=#26B861><b>QUEUE HEALTH ({queues.Length})</b></color>\n");
        foreach (DebugDiagnostics.QueueHealth queue in queues.Take(30))
        {
            double age = queue.OldestUtc == default ? 0 : Math.Max(0, (DateTime.UtcNow - queue.OldestUtc).TotalSeconds);
            builder.Append($"{queue.Kind} {queue.EntityId}  depth {queue.Depth}/{queue.MaximumDepth}  age {age:0.0}s  received/applied {queue.Received}/{queue.Applied}  stale {queue.StaleDiscarded}  gaps {queue.TickGaps} (max {queue.LargestTickGap})\n");
        }
        if (queues.Length == 0) builder.Append("<color=#78858C>No observed queues yet.</color>\n");
        builder.Append($"\n<color=#26B861><b>UNRESOLVED DEPENDENCIES ({dependencies.Length})</b></color>\n");
        foreach (DebugDiagnostics.DependencyHealth item in dependencies.Take(30))
            builder.Append($"<color=#FFB84D>{item.EntityType} {item.EntityId}</color>  {item.Reason}  {item.Detail}  age {(DateTime.UtcNow - item.FirstSeenUtc).TotalSeconds:0.0}s  seen {item.Count}\n");
        if (dependencies.Length == 0) builder.Append("<color=#78858C>None.</color>\n");
        builder.Append($"\n<color=#26B861><b>ID INTEGRITY ({identity.Length})</b></color>\n");
        foreach (DebugDiagnostics.IdentityIssue item in identity.Take(50))
            builder.Append($"<color={(item.Severity >= DebugSeverity.Error ? "#FF6666" : "#FFB84D")}>{item.Code}</color>  ID {item.EntityId}  {item.Detail}\n");
        if (identity.Length == 0) builder.Append("<color=#78858C>No current identity issues.</color>\n");
        return builder.ToString();
    }

    private static string FormatItemDiagnostics(string itemId)
    {
        DebugDiagnostics.DependencyHealth[] dependencies = DebugDiagnostics.DependencySnapshot().Where(item => item.EntityType == "Item" && item.EntityId == itemId).ToArray();
        DebugDiagnostics.IdentityIssue[] identity = DebugDiagnostics.IdentitySnapshot().Where(item => item.EntityId.Split(',').Contains(itemId)).ToArray();
        StringBuilder builder = new("\n");
        builder.Append(FormatReplicationFlow(itemId)).Append('\n').Append(FormatInterestMatrix(itemId));
        if (dependencies.Length > 0)
        {
            builder.Append("\n<color=#FFB84D><b>BLOCKED / UNRESOLVED</b></color>\n");
            foreach (DebugDiagnostics.DependencyHealth item in dependencies) builder.Append($"{item.Reason}  {item.Detail}  age {(DateTime.UtcNow - item.FirstSeenUtc).TotalSeconds:0.0}s\n");
        }
        if (identity.Length > 0)
        {
            builder.Append("\n<color=#FF6666><b>IDENTITY ISSUES</b></color>\n");
            foreach (DebugDiagnostics.IdentityIssue item in identity) builder.Append($"{item.Code}  {item.Detail}\n");
        }
        return builder.ToString();
    }

    private static string FormatReplicationFlow(string itemId)
    {
        Dictionary<string, object>[] flow = DebugDiagnostics.ReplicationFlow(itemId);
        StringBuilder builder = new("<color=#26B861><b>REPLICATION FLOW</b></color>\n");
        foreach (Dictionary<string, object> stage in flow)
            builder.Append(Convert.ToBoolean(stage["observed"]) ? "<color=#26B861>✓</color> " : "<color=#78858C>·</color> ")
                .Append(stage["stage"]).Append(Convert.ToBoolean(stage["observed"]) ? $"  {stage["side"]}  #{stage["sequence"]}" : string.Empty).Append('\n');
        return builder.ToString();
    }

    private static string FormatInterestMatrix(string itemId)
    {
        Dictionary<string, object>[] interest = DebugDiagnostics.InterestMatrix(itemId);
        StringBuilder builder = new("<color=#26B861><b>HOST INTEREST / KNOWN MATRIX</b></color>\n");
        if (interest.Length == 0) builder.Append("<color=#78858C>Available on the host for a live item.</color>\n");
        foreach (Dictionary<string, object> row in interest)
            builder.Append($"P{row["playerId"]} {row["player"]}  {Convert.ToSingle(row["distance"]):0.0}m  nearby={row["nearby"]}  known={row["known"]}  knownTick={row["knownTick"]}  dirty={row["lastDirtyTick"]}  <color=#26B861>{row["decision"]}</color>\n");
        return builder.ToString();
    }

    private static string FormatState(Dictionary<string, object> state, IEnumerable<string> changed = null)
    {
        if (state == null || state.Count == 0) return "<color=#78858C>No state has been observed yet.</color>";
        HashSet<string> changedSet = changed == null ? new HashSet<string>() : new HashSet<string>(changed, StringComparer.Ordinal);
        StringBuilder builder = new();
        foreach (var pair in state.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(changedSet.Contains(pair.Key) ? "<color=#26B861>" : "<color=#78858C>").Append(pair.Key).Append(changedSet.Contains(pair.Key) ? "  *</color>\n  " : "</color>\n  ").Append(FormatValue(pair.Value, 0)).Append("\n");
        return builder.ToString();
    }
    private static string FormatInline(Dictionary<string, object> data) => data == null ? string.Empty : string.Join(" · ", data.Take(3).Select(pair => $"{pair.Key}={FormatValue(pair.Value, 0)}"));
    private static string FormatValue(object value, int depth)
    {
        if (value == null) return "<color=#78858C>null</color>";
        if (value is IDictionary<string, object> map)
        {
            if (map.ContainsKey("x") && map.ContainsKey("y")) return "(" + string.Join(", ", new[] { "x", "y", "z", "w" }.Where(map.ContainsKey).Select(key => Number(map[key]))) + ")";
            if (depth > 1) return $"{{{map.Count} fields}}";
            return string.Join("  ", map.Take(8).Select(pair => $"<color=#78858C>{pair.Key}</color>={FormatValue(pair.Value, depth + 1)}"));
        }
        if (value is System.Collections.IEnumerable enumerable && value is not string) { List<string> values = new(); foreach (object item in enumerable) { if (values.Count == 8) { values.Add("…"); break; } values.Add(FormatValue(item, depth + 1)); } return "[" + string.Join(", ", values) + "]"; }
        return Convert.ToString(value);
    }
    private static string Number(object value) { try { return Convert.ToSingle(value).ToString("0.###"); } catch { return Convert.ToString(value); } }
    private static object StateValue(Dictionary<string, object> state, string key, object fallback) =>
        state != null && state.TryGetValue(key, out object value) && value != null ? value : fallback;
    private static bool StateBool(Dictionary<string, object> state, string key)
    {
        try { return Convert.ToBoolean(StateValue(state, key, false)); }
        catch { return false; }
    }
}

public sealed class DebugPanelDragger : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    public RectTransform Target;
    private Vector2 offset;
    public void OnBeginDrag(PointerEventData eventData) { RectTransformUtility.ScreenPointToLocalPointInRectangle(Target.parent as RectTransform, eventData.position, eventData.pressEventCamera, out Vector2 point); offset = Target.anchoredPosition - point; }
    public void OnDrag(PointerEventData eventData) { if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Target.parent as RectTransform, eventData.position, eventData.pressEventCamera, out Vector2 point)) Target.anchoredPosition = point + offset; }
}
