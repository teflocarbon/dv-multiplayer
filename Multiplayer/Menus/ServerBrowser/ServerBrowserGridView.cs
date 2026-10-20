using Multiplayer.Shared.Protocol.Data;
using DV.UI;
using Multiplayer.Menus.Controls;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Menus.ServerBrowser;

[RequireComponent(typeof(ContentSizeFitter))]
[RequireComponent(typeof(VerticalLayoutGroup))]
public class ServerBrowserGridView : MPGridView<IServerBrowserGameDetails>
{

    protected override void Awake()
    {
        showPlaceholderWhenEmpty = true;

        //copy the copy
        viewElementPrefab.SetActive(false);
        placeholderElementPrefab = Instantiate(viewElementPrefab);

        //swap controllers
        Destroy(viewElementPrefab.GetComponent<SaveLoadViewElement>());
        Destroy(placeholderElementPrefab.GetComponent<SaveLoadViewElement>());

        viewElementPrefab.AddComponent<ServerBrowserElement>();
        placeholderElementPrefab.AddComponent<ServerBrowserPlaceholderElement>();

        viewElementPrefab.name = "prefabSBElement";
        placeholderElementPrefab.name = "prefabSBPlaceholderElement";

        viewElementPrefab.SetActive(true);
        placeholderElementPrefab.SetActive(true);
         
        base.Awake();
    }
}
