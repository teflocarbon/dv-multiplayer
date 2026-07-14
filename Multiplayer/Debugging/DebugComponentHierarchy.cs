using DV.CabControls;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging;

/// <summary>
/// Creates a bounded, detached description of a selected Unity hierarchy. This deliberately
/// does not reflect arbitrary component properties: many Unity properties allocate, recurse,
/// mutate renderer state, or throw when native state has already been destroyed.
/// </summary>
public static class DebugComponentHierarchy
{
    public sealed class Snapshot
    {
        public DateTime CapturedUtc;
        public string EntityType;
        public string EntityId;
        public string RootPath;
        public int GameObjectCount;
        public int ComponentCount;
        public bool GameObjectsTruncated;
        public bool ComponentsTruncated;
        public List<GameObjectEntry> GameObjects = new();
    }

    public sealed class GameObjectEntry
    {
        public string Path;
        public int Depth;
        public int SiblingIndex;
        public int InstanceId;
        public bool ActiveSelf;
        public bool ActiveInHierarchy;
        public int Layer;
        public string LayerName;
        public string Tag;
        public List<ComponentEntry> Components = new();
    }

    public sealed class ComponentEntry
    {
        public string Type;
        public string FullType;
        public string Assembly;
        public int InstanceId;
        public bool Missing;
        public Dictionary<string, object> Identity;
    }

    public static Snapshot Capture(string entityType, string entityId, Component rootComponent, int maxGameObjects = 512, int maxComponents = 4096)
    {
        if (rootComponent == null) return null;
        maxGameObjects = Mathf.Clamp(maxGameObjects, 1, 2048);
        maxComponents = Mathf.Clamp(maxComponents, 1, 16384);

        Transform root = rootComponent.transform;
        Snapshot result = new()
        {
            CapturedUtc = DateTime.UtcNow,
            EntityType = entityType ?? string.Empty,
            EntityId = entityId ?? string.Empty,
            RootPath = AbsolutePath(root)
        };

        Stack<(Transform transform, int depth)> pending = new();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            if (result.GameObjects.Count >= maxGameObjects)
            {
                result.GameObjectsTruncated = true;
                break;
            }

            (Transform transform, int depth) = pending.Pop();
            if (transform == null) continue;
            GameObject gameObject = transform.gameObject;
            GameObjectEntry gameObjectEntry = new()
            {
                Path = RelativePath(root, transform),
                Depth = depth,
                SiblingIndex = transform.GetSiblingIndex(),
                InstanceId = gameObject.GetInstanceID(),
                ActiveSelf = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Layer = gameObject.layer,
                LayerName = LayerMask.LayerToName(gameObject.layer) ?? string.Empty,
                Tag = SafeTag(gameObject)
            };

            Component[] components;
            try { components = gameObject.GetComponents<Component>(); }
            catch (Exception exception)
            {
                components = Array.Empty<Component>();
                gameObjectEntry.Components.Add(new ComponentEntry
                {
                    Type = "<component-read-failed>", FullType = exception.GetType().FullName,
                    Assembly = string.Empty, Identity = new Dictionary<string, object> { ["message"] = exception.Message }
                });
            }

            foreach (Component component in components)
            {
                if (result.ComponentCount >= maxComponents)
                {
                    result.ComponentsTruncated = true;
                    break;
                }
                result.ComponentCount++;
                gameObjectEntry.Components.Add(Project(component));
            }
            result.GameObjects.Add(gameObjectEntry);
            if (result.ComponentsTruncated) break;

            // Reverse insertion preserves Unity's ordinary first-to-last child order.
            for (int index = transform.childCount - 1; index >= 0; index--)
                pending.Push((transform.GetChild(index), depth + 1));
        }

        result.GameObjectCount = result.GameObjects.Count;
        return result;
    }

    public static string Summarize(Snapshot snapshot)
    {
        if (snapshot == null) return null;
        List<string> lines = new()
        {
            $"COMPONENT HIERARCHY {snapshot.EntityType} {snapshot.EntityId}",
            $"Root: {snapshot.RootPath}",
            $"Objects: {snapshot.GameObjectCount}{(snapshot.GameObjectsTruncated ? "+ (truncated)" : string.Empty)} · Components: {snapshot.ComponentCount}{(snapshot.ComponentsTruncated ? "+ (truncated)" : string.Empty)}",
            string.Empty
        };
        foreach (GameObjectEntry gameObject in snapshot.GameObjects)
        {
            string indent = new(' ', Math.Min(gameObject.Depth, 32) * 2);
            string flags = $"active={gameObject.ActiveSelf}/{gameObject.ActiveInHierarchy} layer={gameObject.Layer}:{gameObject.LayerName}";
            lines.Add($"{indent}{gameObject.Path}  [{flags}]");
            foreach (ComponentEntry component in gameObject.Components)
            {
                string identity = component.Identity == null || component.Identity.Count == 0
                    ? string.Empty
                    : " · " + string.Join(", ", component.Identity.Select(pair => $"{pair.Key}={Inline(pair.Value)}"));
                lines.Add($"{indent}  - {(component.Missing ? "<missing-script>" : component.FullType)}{identity}");
            }
        }
        return string.Join("\n", lines);
    }

    private static ComponentEntry Project(Component component)
    {
        if (component == null) return new ComponentEntry { Type = "<missing-script>", FullType = "<missing-script>", Assembly = string.Empty, Missing = true };
        Type type = component.GetType();
        ComponentEntry result = new()
        {
            Type = type.Name,
            FullType = type.FullName ?? type.Name,
            Assembly = type.Assembly.GetName().Name,
            InstanceId = component.GetInstanceID(),
            Identity = new Dictionary<string, object>(StringComparer.Ordinal)
        };

        try
        {
            switch (component)
            {
                case Transform transform:
                    result.Identity["localPosition"] = DebugValueSnapshotter.Snapshot(transform.localPosition);
                    result.Identity["localRotation"] = DebugValueSnapshotter.Snapshot(transform.localRotation);
                    result.Identity["localScale"] = DebugValueSnapshotter.Snapshot(transform.localScale);
                    break;
                case Rigidbody rigidbody:
                    result.Identity["isKinematic"] = rigidbody.isKinematic;
                    result.Identity["useGravity"] = rigidbody.useGravity;
                    result.Identity["velocity"] = DebugValueSnapshotter.Snapshot(rigidbody.velocity);
                    result.Identity["angularVelocity"] = DebugValueSnapshotter.Snapshot(rigidbody.angularVelocity);
                    break;
                case Renderer renderer:
                    result.Identity["enabled"] = renderer.enabled;
                    result.Identity["visible"] = renderer.isVisible;
                    result.Identity["sharedMaterialCount"] = renderer.sharedMaterials?.Length ?? 0;
                    result.Identity["sortingLayer"] = renderer.sortingLayerName;
                    result.Identity["sortingOrder"] = renderer.sortingOrder;
                    break;
                case Collider collider:
                    result.Identity["enabled"] = collider.enabled;
                    result.Identity["isTrigger"] = collider.isTrigger;
                    break;
                case Animator animator:
                    result.Identity["enabled"] = animator.enabled;
                    result.Identity["cullingMode"] = animator.cullingMode.ToString();
                    result.Identity["controller"] = animator.runtimeAnimatorController?.name ?? string.Empty;
                    break;
                case Behaviour behaviour:
                    result.Identity["enabled"] = behaviour.enabled;
                    result.Identity["activeAndEnabled"] = behaviour.isActiveAndEnabled;
                    break;
            }

            // Purpose-built identities for the item/page types currently under investigation.
            if (component is PageBook pageBook)
            {
                result.Identity["currentPage"] = pageBook.currentPage;
                result.Identity["pageCount"] = pageBook.PageNum;
                result.Identity["pagesGenerated"] = pageBook.PagesGenerated;
                result.Identity["runtimePageCount"] = pageBook.pages?.Count ?? 0;
                result.Identity["textureCount"] = pageBook.pageTextures?.Length ?? 0;
                result.Identity["bookVolumeModel"] = pageBook.bookVolumeModel?.name ?? string.Empty;
            }
            else if (component is NetworkedItem networkedItem)
            {
                result.Identity["netId"] = networkedItem.NetId;
                result.Identity["itemState"] = networkedItem.DebugCurrentState.ToString();
                result.Identity["authorityRevision"] = networkedItem.AuthorityRevision;
            }
            else if (component is ItemBase item)
            {
                result.Identity["prefabName"] = item.InventorySpecs?.ItemPrefabName ?? string.Empty;
                result.Identity["isGrabbed"] = item.IsGrabbed();
                result.Identity["isEssential"] = item.IsEssential();
            }
        }
        catch (Exception exception)
        {
            result.Identity["snapshotError"] = $"{exception.GetType().Name}: {exception.Message}";
        }
        return result;
    }

    private static string RelativePath(Transform root, Transform current)
    {
        if (current == root) return root.name;
        Stack<string> parts = new();
        for (Transform value = current; value != null && value != root; value = value.parent) parts.Push(value.name);
        return root.name + "/" + string.Join("/", parts);
    }

    private static string AbsolutePath(Transform transform)
    {
        Stack<string> parts = new();
        for (Transform value = transform; value != null; value = value.parent) parts.Push(value.name);
        return string.Join("/", parts);
    }

    private static string SafeTag(GameObject gameObject)
    {
        try { return gameObject.tag ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Inline(object value)
    {
        if (value == null) return "null";
        string serialized = DebugJson.Serialize(value);
        return serialized?.Replace("\r", string.Empty).Replace("\n", " ") ?? string.Empty;
    }
}
