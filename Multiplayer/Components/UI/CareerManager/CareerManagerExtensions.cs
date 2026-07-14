using DV.ServicePenalty.UI;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Clientbound;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Multiplayer.Components.UI.CareerManager;

internal sealed class CareerManagerExtensionEntry
{
    public string Id;
    public int Order;
    public string Label;
    public Func<CareerManagerExtensionHost, IDisplayScreen> CreateScreen;
}

internal static class CareerManagerExtensionRegistry
{
    private static readonly Dictionary<string, CareerManagerExtensionEntry> entries = new();
    private static bool defaultsRegistered;

    internal static void EnsureDefaults()
    {
        if (defaultsRegistered) return;
        defaultsRegistered = true;
        Register(new CareerManagerExtensionEntry
        {
            Id = "multiplayer.lost-items",
            Order = 100,
            Label = "LOST ITEMS",
            CreateScreen = host => host.LostItemsScreen
        });
    }

    internal static void Register(CareerManagerExtensionEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || entry.CreateScreen == null)
            throw new ArgumentException("Invalid Career Manager extension entry.");
        if (entries.ContainsKey(entry.Id))
            throw new InvalidOperationException($"Career Manager extension '{entry.Id}' already exists.");
        entries.Add(entry.Id, entry);
    }

    internal static IReadOnlyList<CareerManagerExtensionEntry> Snapshot() => entries.Values
        .OrderBy(entry => entry.Order).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
}

internal sealed class CareerManagerExtensionHost : MonoBehaviour
{
    internal CareerManagerMainScreen MainScreen { get; private set; }
    internal DisplayScreenSwitcher Switcher { get; private set; }
    internal TextMeshPro MainRow { get; private set; }
    internal CareerManagerExtensionHubScreen HubScreen { get; private set; }
    internal LostItemsCareerManagerScreen LostItemsScreen { get; private set; }

    internal void Initialize(CareerManagerMainScreen main)
    {
        if (MainScreen != null) return;
        MainScreen = main;
        Switcher = main.screenSwitcher;
        CareerManagerExtensionRegistry.EnsureDefaults();

        Vector3 delta = main.stats.transform.localPosition - main.ownedVehicles.transform.localPosition;
        MainRow = Instantiate(main.stats, main.stats.transform.parent);
        MainRow.name = "MultiplayerExtensionRow";
        MainRow.transform.localPosition = main.stats.transform.localPosition + delta;
        MainRow.text = string.Empty;
        main.selectableText = main.selectableText.Concat(new[] { MainRow }).ToArray();
        main.activeSlotCount = main.selectableText.Length;
        main.selector.UpdateLength(main.activeSlotCount);
        if (!Switcher.allTextFields.Contains(MainRow))
            Switcher.allTextFields.Add(MainRow);

        HubScreen = gameObject.AddComponent<CareerManagerExtensionHubScreen>();
        LostItemsScreen = gameObject.AddComponent<LostItemsCareerManagerScreen>();
        HubScreen.Initialize(this, CreateScreenText("MultiplayerHub", "MULTIPLAYER"));
        LostItemsScreen.Initialize(this, CreateScreenText("LostItems", "LOST ITEMS"));
    }

    internal void ShowMainRow()
    {
        MainScreen.activeSlotCount = MainScreen.selectableText.Length;
        MainScreen.selector.UpdateLength(MainScreen.activeSlotCount);
        MainRow.text = "MULTIPLAYER";
    }

    internal void ClearMainRow()
    {
        if (MainRow != null) MainRow.text = string.Empty;
    }

    internal void OpenHub() => Switcher.SetActiveDisplay(HubScreen);

    private ScreenTextSet CreateScreenText(string name, string title)
    {
        TextMeshPro titleText = Instantiate(MainScreen.title, MainScreen.title.transform.parent);
        titleText.name = name + "Title";
        titleText.text = string.Empty;
        TextMeshPro[] rows = new TextMeshPro[4];
        TextMeshPro[] templates = { MainScreen.fees, MainScreen.licenses,
            MainScreen.ownedVehicles, MainScreen.stats };
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = Instantiate(templates[i], templates[i].transform.parent);
            rows[i].name = name + "Row" + i;
            rows[i].text = string.Empty;
            if (!Switcher.allTextFields.Contains(rows[i])) Switcher.allTextFields.Add(rows[i]);
        }
        if (!Switcher.allTextFields.Contains(titleText)) Switcher.allTextFields.Add(titleText);
        return new ScreenTextSet(title, titleText, rows);
    }
}

internal readonly struct ScreenTextSet
{
    internal ScreenTextSet(string title, TextMeshPro titleText, TextMeshPro[] rows)
    {
        Title = title;
        TitleText = titleText;
        Rows = rows;
    }
    internal string Title { get; }
    internal TextMeshPro TitleText { get; }
    internal TextMeshPro[] Rows { get; }
}

internal abstract class MultiplayerCareerManagerScreen : DisplayScreen
{
    protected CareerManagerExtensionHost Host;
    protected ScreenTextSet Texts;
    protected IDisplayScreen Previous;
    protected int Selected;

    internal virtual void Initialize(CareerManagerExtensionHost host, ScreenTextSet texts)
    {
        Host = host;
        Texts = texts;
    }

    protected void RenderRows(IReadOnlyList<string> values)
    {
        Texts.TitleText.text = Texts.Title;
        for (int i = 0; i < Texts.Rows.Length; i++)
        {
            TextMeshPro row = Texts.Rows[i];
            row.text = i < values.Count ? values[i] : string.Empty;
            row.color = i == Selected && i < values.Count
                ? Host.Switcher.HIGHLIGHTED_COLOR : Host.Switcher.REGULAR_COLOR;
        }
    }

    protected void Clear()
    {
        Texts.TitleText.text = string.Empty;
        foreach (TextMeshPro row in Texts.Rows) row.text = string.Empty;
    }

    protected static int Wrapped(int value, int count) => count <= 0 ? 0 : (value % count + count) % count;
}

internal sealed class CareerManagerExtensionHubScreen : MultiplayerCareerManagerScreen
{
    private IReadOnlyList<CareerManagerExtensionEntry> entries = Array.Empty<CareerManagerExtensionEntry>();

    public override void Activate(IDisplayScreen previousScreen)
    {
        Previous = previousScreen;
        entries = CareerManagerExtensionRegistry.Snapshot();
        Selected = Wrapped(Selected, entries.Count);
        Render();
    }

    public override void Disable() => Clear();

    public override void HandleInputAction(InputAction input)
    {
        if (input == InputAction.Cancel)
        {
            Host.Switcher.SetActiveDisplay(Host.MainScreen);
            return;
        }
        if (input == InputAction.Up) Selected = Wrapped(Selected - 1, entries.Count);
        else if (input == InputAction.Down) Selected = Wrapped(Selected + 1, entries.Count);
        else if (input == InputAction.Confirm && entries.Count > 0)
        {
            Host.Switcher.SetActiveDisplay(entries[Selected].CreateScreen(Host));
            return;
        }
        Render();
    }

    private void Render() => RenderRows(entries.Select(entry => entry.Label).ToArray());
}

internal sealed class LostItemsCareerManagerScreen : MultiplayerCareerManagerScreen
{
    private readonly List<LostItemData> items = new();
    private int first;
    private bool loading;
    private bool pending;
    private string message = string.Empty;
    private uint requestId;

    public override void Activate(IDisplayScreen previousScreen)
    {
        Previous = previousScreen;
        NetworkedLostAndFoundManager.ClientListChanged += OnListChanged;
        if (NetworkLifecycle.Instance.Client != null)
            NetworkLifecycle.Instance.Client.LostItemRetrieveCompleted += OnRetrieveCompleted;
        loading = true;
        message = "LOADING...";
        NetworkLifecycle.Instance.Client?.RequestLostItems(++requestId);
        RefreshFromModel();
    }

    public override void Disable()
    {
        NetworkedLostAndFoundManager.ClientListChanged -= OnListChanged;
        if (NetworkLifecycle.Instance?.Client != null)
            NetworkLifecycle.Instance.Client.LostItemRetrieveCompleted -= OnRetrieveCompleted;
        Clear();
    }

    public override void HandleInputAction(InputAction input)
    {
        if (input == InputAction.Cancel)
        {
            Host.Switcher.SetActiveDisplay(Host.HubScreen);
            return;
        }
        if (pending || items.Count == 0) return;
        if (input == InputAction.Up) Selected = Wrapped(Selected - 1, items.Count);
        else if (input == InputAction.Down) Selected = Wrapped(Selected + 1, items.Count);
        else if (input == InputAction.Confirm)
        {
            LostItemData item = items[Selected];
            pending = true;
            message = "RETRIEVING...";
            NetworkLifecycle.Instance.Client?.RequestLostItemRetrieval(++requestId,
                item.NetId, item.Revision);
        }
        else if (input == InputAction.PrintInfo)
        {
            LostItemData item = items[Selected];
            message = $"ID {item.NetId}  {((global::Multiplayer.Core.Items.LostItemReason)item.Reason)}";
        }
        EnsureVisible();
        Render();
    }

    private void OnListChanged()
    {
        loading = false;
        // A host-side authority transition (for example, a validator reprint) can invalidate a
        // row without completing this screen's own retrieval request. Do not leave the previous
        // failure/status text pinned after the authoritative list has refreshed.
        if (!pending)
            message = string.Empty;
        RefreshFromModel();
    }

    private void OnRetrieveCompleted(ClientboundLostItemRetrieveResultPacket result)
    {
        pending = false;
        message = result.Accepted ? "ITEM RESTORED" : "FAILED: " + (result.RejectionReason ?? "UNKNOWN");
        RefreshFromModel();
    }

    private void RefreshFromModel()
    {
        ushort selectedId = items.Count > 0 && Selected < items.Count ? items[Selected].NetId : (ushort)0;
        items.Clear();
        items.AddRange(NetworkedLostAndFoundManager.ClientItems.OrderBy(item => item.LostUtcTicks));
        int preserved = selectedId == 0 ? -1 : items.FindIndex(item => item.NetId == selectedId);
        Selected = preserved >= 0 ? preserved : Mathf.Clamp(Selected, 0, Math.Max(0, items.Count - 1));
        EnsureVisible();
        Render();
    }

    private void EnsureVisible()
    {
        int itemRows = Math.Max(1, Texts.Rows.Length - 1);
        if (Selected < first) first = Selected;
        if (Selected >= first + itemRows) first = Selected - itemRows + 1;
        first = Mathf.Clamp(first, 0, Math.Max(0, items.Count - itemRows));
    }

    private void Render()
    {
        Texts.TitleText.text = Texts.Title;
        int itemRows = Texts.Rows.Length - 1;
        for (int row = 0; row < itemRows; row++)
        {
            int index = first + row;
            TextMeshPro text = Texts.Rows[row];
            text.text = index < items.Count
                ? $"[{items[index].NetId}] {Display(items[index])}"
                : string.Empty;
            text.color = index == Selected && index < items.Count
                ? Host.Switcher.HIGHLIGHTED_COLOR : Host.Switcher.REGULAR_COLOR;
        }
        if (loading) Texts.Rows[itemRows].text = "LOADING...";
        else if (items.Count == 0) Texts.Rows[itemRows].text = string.IsNullOrEmpty(message) ? "NO LOST ITEMS" : message;
        else Texts.Rows[itemRows].text = string.IsNullOrEmpty(message)
            ? $"{Selected + 1}/{items.Count}  CONFIRM: RETRIEVE  INFO: DETAILS" : message;
        Texts.Rows[itemRows].color = Host.Switcher.REGULAR_COLOR;
    }

    private static string Display(LostItemData item) => string.IsNullOrWhiteSpace(item.DisplayName)
        ? item.PrefabName ?? "ITEM" : item.DisplayName.Replace("(Clone)", string.Empty);
}
