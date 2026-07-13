using DV;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Multiplayer.Debugging;

public static class DebugWorldLabelManager
{
    private sealed class Label { public GameObject Object; public TextMeshPro Text; }
    private static readonly List<Label> labels = new();
    private static readonly HashSet<string> enabledTypes = new() { "Item", "Player", "TrainCar" };
    private static float nextUpdate;
    private static bool errorsOnly;
    private static string netIdFilter = string.Empty;
    public static bool Enabled { get; private set; }
    public static bool ErrorsOnly => errorsOnly;
    public static bool IsTypeEnabled(string type) => enabledTypes.Contains(type);
    public static void SetEnabled(bool value) { Enabled = value; Multiplayer.Settings.EnableDebugWorldLabels = value; if (!value) HideAll(); }
    public static void SetType(string type, bool enabled) { string normalized = type?.ToLowerInvariant() switch { "items" => "Item", "players" => "Player", "trains" => "TrainCar", _ => type }; if (string.IsNullOrWhiteSpace(normalized)) return; if (enabled) enabledTypes.Add(normalized); else enabledTypes.Remove(normalized); }
    public static void SetErrorsOnly(bool value) => errorsOnly = value;
    public static void SetNetId(string value) => netIdFilter = value ?? string.Empty;

    public static void Tick()
    {
        if (!Enabled || Time.unscaledTime < nextUpdate || PlayerManager.ActiveCamera == null) return;
        nextUpdate = Time.unscaledTime + 1f / Mathf.Clamp(Multiplayer.Settings.DebugWorldLabelUpdateHz, 1f, 10f);
        Vector3 camera = PlayerManager.ActiveCamera.transform.position;
        var visible = EntityDebugRegistry.Live().Where(item => item.component != null && enabledTypes.Contains(item.dto.EntityType))
            .Where(item => item.dto.EntityType != "Player" || item.component.transform != PlayerManager.PlayerTransform)
            .Where(item => !errorsOnly || item.dto.Severity >= DebugSeverity.Warning).Where(item => string.IsNullOrEmpty(netIdFilter) || item.dto.EntityId == netIdFilter)
            .Select(item => (item.dto, item.component, distance: Vector3.Distance(camera, item.component.transform.position)))
            .Where(item => item.distance <= Multiplayer.Settings.DebugWorldLabelRadius)
            .OrderByDescending(item => DebugOverlayController.IsSelected(item.dto.EntityType, item.dto.EntityId)).ThenByDescending(item => item.dto.Severity).ThenBy(item => item.distance)
            .Take(DebugOverlayController.Verbose ? Math.Min(16, Multiplayer.Settings.DebugWorldLabelMaxCount) : Multiplayer.Settings.DebugWorldLabelMaxCount).ToArray();
        Ensure(visible.Length);
        for (int index = 0; index < labels.Count; index++)
        {
            bool active = index < visible.Length; labels[index].Object.SetActive(active); if (!active) continue;
            var item = visible[index]; Transform transform = labels[index].Object.transform;
            transform.position = item.component.transform.position + Vector3.up * (item.dto.EntityType == "TrainCar" ? 3.5f : item.dto.EntityType == "Player" ? 2.1f : 1.2f);
            transform.rotation = Quaternion.LookRotation(transform.position - camera); transform.localScale = Vector3.one * Mathf.Clamp(item.distance * 0.025f, 0.06f, 0.5f) * Multiplayer.Settings.DebugWorldLabelScale;
            if (DebugOverlayController.Verbose) EntityDebugRegistry.RefreshLiveState(item.dto.EntityType, item.dto.EntityId);
            DebugEntityDto details = DebugOverlayController.Verbose ? EntityDebugRegistry.GetLabel(item.dto.EntityType, item.dto.EntityId) : item.dto;
            labels[index].Text.fontSize = DebugOverlayController.Verbose ? 4.5f : 6f; labels[index].Text.text = BuildText(details ?? item.dto);
            bool occluded = !Multiplayer.Settings.DebugLabelsThroughWalls && Physics.Linecast(camera, transform.position, out RaycastHit hit) && hit.transform != item.component.transform && !hit.transform.IsChildOf(item.component.transform);
            labels[index].Text.enabled = !occluded;
        }
    }
    private static void Ensure(int count) { while (labels.Count < count) { GameObject go = new("DVMP Debug Label"); TextMeshPro text = go.AddComponent<TextMeshPro>(); text.alignment = TextAlignmentOptions.Center; text.fontSize = 6; text.fontStyle = FontStyles.Bold; text.color = new Color(0.15f, 1f, 0.25f); text.enableCulling = true; labels.Add(new Label { Object = go, Text = text }); } }
    private static void HideAll() { foreach (Label label in labels) if (label.Object != null) label.Object.SetActive(false); }
    private static string BuildText(DebugEntityDto item)
    {
        string header = $"[{item.EntityId}] {item.DisplayName}"; if (!DebugOverlayController.Verbose) return item.Severity >= DebugSeverity.Warning ? $"{header}\n{item.EntityType} | {item.Severity}" : $"{header}\n{item.EntityType}";
        Dictionary<string, object> state = item.LatestState ?? new(); DebugEvent last = item.Timeline?.LastOrDefault(); string lastText = last == null ? "none" : $"{last.EventName} t={last.NetworkTick?.ToString() ?? "-"}";
        if (item.EntityType == "Item") { string storage = Flag(state, "inInventoryStorage") ? "inventory" : Flag(state, "inLostAndFound") ? "lost+found" : Flag(state, "inWorldStorage") ? "world" : "none"; return $"{header}\nState: {Value(state, "itemState")} | Holder: {Value(state, "holderPlayerId")}\nLocal: {Vector(state, "positionLocal")}\nAbs: {Vector(state, "positionAbsolute")}\nVel: {Vector(state, "velocity")}\nParent: {Short(Value(state, "parent"), 28)} | Storage: {storage} | Active: {Value(state, "activeInHierarchy")}\nLast: {lastText}"; }
        if (item.EntityType == "Player") return $"{header}\nCar: {Value(state, "car")} | On car: {Value(state, "isOnCar")} | VR: {Value(state, "isVr")}\nLocal: {Vector(state, "positionLocal")}\nAbs: {Vector(state, "positionAbsolute")}\nLast: {lastText}";
        if (item.EntityType == "TrainCar") return $"{header}\nCar: {Value(state, "carId")} | Derailed: {Value(state, "derailed")} | Tick: {Value(state, "lastPhysicsTick")}\nLocal: {Vector(state, "positionLocal")}\nAbs: {Vector(state, "positionAbsolute")}\nVel: {Vector(state, "velocity")}\nLast: {lastText}";
        return $"{header}\n{item.EntityType} | {item.Severity}\nLast: {lastText}";
    }
    private static string Value(Dictionary<string, object> state, string key) => state.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : "-";
    private static bool Flag(Dictionary<string, object> state, string key) => state.TryGetValue(key, out object value) && value is bool enabled && enabled;
    private static string Vector(Dictionary<string, object> state, string key) { if (!state.TryGetValue(key, out object value) || value is not IDictionary<string, object> vector) return "-"; return $"({Number(vector, "x")}, {Number(vector, "y")}, {Number(vector, "z")})"; }
    private static string Number(IDictionary<string, object> vector, string key) { if (!vector.TryGetValue(key, out object value) || value == null) return "-"; try { return Convert.ToSingle(value).ToString("0.00"); } catch { return "-"; } }
    private static string Short(string value, int length) => string.IsNullOrEmpty(value) || value.Length <= length ? value : value.Substring(0, length) + "...";
}
