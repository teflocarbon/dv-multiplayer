using System;
using System.Collections.Generic;
using DV.ServicePenalty.UI;
using Multiplayer.Client.Items.OwnedList;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using TMPro;
using UnityEngine;

namespace Multiplayer.Client.Career;

/// <summary>
/// The career manager's Owned Items page (owned-list.md, "The page"): every
/// item the player owns, wherever it is, as the server words it. Confirm retrieves the selected
/// item into Lost and Found when it can go, and the server sends a fresh list.
/// </summary>
internal sealed class OwnedItemsCareerManagerScreen : MultiplayerCareerManagerScreen
{
    private readonly List<OwnedItemListRow> items = new();
    private int first;
    private bool loading;
    private bool pending;
    private string message = string.Empty;
    // The retrieval this screen is waiting on: only its own result clears "RETRIEVING".
    private uint requestId;

    public override void Activate(IDisplayScreen previousScreen)
    {
        Previous = previousScreen;
        OwnedItemsList.Changed += OnListChanged;
        if (ItemClient.Current != null)
            ItemClient.Current.ItemResultReceived += OnRetrieveCompleted;
        loading = true;
        message = string.Empty;
        ItemClient.Current?.RequestItemList(ItemListKind.Owned);
        RefreshFromModel();
    }

    public override void Disable()
    {
        OwnedItemsList.Changed -= OnListChanged;
        if (ItemClient.Current != null)
            ItemClient.Current.ItemResultReceived -= OnRetrieveCompleted;
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
        else if (input == InputAction.Confirm && items[Selected].Retrievable)
        {
            requestId = ItemClient.Current?.SendItemRequest(new ItemRequest
            {
                Action = ItemAction.Retrieve,
                ItemHandle = items[Selected].Handle
            }) ?? 0;
            pending = requestId != 0;
            message = pending ? "RETRIEVING..." : "FAILED: NOT SENT";
        }
        if (input is InputAction.Up or InputAction.Down) message = string.Empty;
        EnsureVisible();
        Render();
    }

    private void OnListChanged()
    {
        loading = false;
        RefreshFromModel();
    }

    private void OnRetrieveCompleted(ClientboundItemResultPacket result)
    {
        if (!pending || result.RequestId != requestId) return;
        pending = false;
        message = result.Accepted ? "SENT TO LOST AND FOUND" : "FAILED: " + (result.Detail ?? result.Refusal.ToString()).ToUpperInvariant();
        Render();
    }

    private void RefreshFromModel()
    {
        uint selectedHandle = items.Count > 0 && Selected < items.Count ? items[Selected].Handle : 0;
        items.Clear();
        items.AddRange(OwnedItemsList.Rows);
        int preserved = selectedHandle == 0 ? -1 : items.FindIndex(item => item.Handle == selectedHandle);
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
            text.text = index < items.Count ? $"{Display(items[index])}: {items[index].Where.ToUpperInvariant()}" : string.Empty;
            text.color = index == Selected && index < items.Count
                ? Host.Switcher.HIGHLIGHTED_COLOR : Host.Switcher.REGULAR_COLOR;
        }
        Texts.Rows[itemRows].text = loading ? "LOADING..." :
            items.Count == 0 ? "NO OWNED ITEMS" :
            !string.IsNullOrEmpty(message) ? message :
            $"{Selected + 1}/{items.Count}" + (items[Selected].Retrievable ? "  CONFIRM: RETRIEVE" : string.Empty);
        Texts.Rows[itemRows].color = Host.Switcher.REGULAR_COLOR;
    }

    private static string Display(OwnedItemListRow item) => string.IsNullOrWhiteSpace(item.DisplayName)
        ? (string.IsNullOrEmpty(item.PrefabName) ? "ITEM" : item.PrefabName)
        : item.DisplayName.Replace("(Clone)", string.Empty).ToUpperInvariant();
}
