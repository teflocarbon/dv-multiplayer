using System;
using System.Collections.Generic;
using DV.ServicePenalty.UI;
using Multiplayer.Client.Items;
using Multiplayer.Shared.Items;
using TMPro;
using UnityEngine;

namespace Multiplayer.Client.Career;

/// <summary>
/// The career manager's Owned Items page (owned-items.md, "The owned-items list", PR 6a): every
/// item the player owns, wherever it is, as the server words it. Retrieving from here comes with
/// PR 6b; until then the page only lists.
/// </summary>
internal sealed class OwnedItemsCareerManagerScreen : MultiplayerCareerManagerScreen
{
    private readonly List<OwnedItemListRow> items = new();
    private int first;
    private bool loading;
    private uint requestId;

    public override void Activate(IDisplayScreen previousScreen)
    {
        Previous = previousScreen;
        OwnedItemsList.Changed += OnListChanged;
        loading = true;
        ItemClient.Current?.RequestOwnedItems(++requestId);
        RefreshFromModel();
    }

    public override void Disable()
    {
        OwnedItemsList.Changed -= OnListChanged;
        Clear();
    }

    public override void HandleInputAction(InputAction input)
    {
        if (input == InputAction.Cancel)
        {
            Host.Switcher.SetActiveDisplay(Host.HubScreen);
            return;
        }
        if (items.Count == 0) return;
        if (input == InputAction.Up) Selected = Wrapped(Selected - 1, items.Count);
        else if (input == InputAction.Down) Selected = Wrapped(Selected + 1, items.Count);
        EnsureVisible();
        Render();
    }

    private void OnListChanged()
    {
        loading = false;
        RefreshFromModel();
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
            items.Count == 0 ? "NO OWNED ITEMS" : $"{Selected + 1}/{items.Count}";
        Texts.Rows[itemRows].color = Host.Switcher.REGULAR_COLOR;
    }

    private static string Display(OwnedItemListRow item) => string.IsNullOrWhiteSpace(item.DisplayName)
        ? (string.IsNullOrEmpty(item.PrefabName) ? "ITEM" : item.PrefabName)
        : item.DisplayName.Replace("(Clone)", string.Empty).ToUpperInvariant();
}
