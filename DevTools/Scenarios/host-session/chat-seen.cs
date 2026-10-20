// Any peer: whether a line containing `text` is in this game's chat panel.
var chat = UnityEngine.Object.FindObjectOfType<Multiplayer.Client.UI.ChatGUI>();
var lines = chat == null ? new List<string>()
    : chat.messageList.Where(m => m != null).Select(m => m.GetComponent<TMPro.TextMeshProUGUI>().text).ToList();
string text = ctx.Args["text"];
return new { chat = chat != null, seen = lines.Any(l => l.Contains(text)), last = lines.LastOrDefault() ?? "" };
