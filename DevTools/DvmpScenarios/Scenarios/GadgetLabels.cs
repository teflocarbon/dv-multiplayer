using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.UIFramework;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Labels go through the server (gadgets.md, PR 9). The server puts a label maker by the client and a
    /// DE2 for it; the client picks it up and rides the DE2.
    /// - It labels the DE2 ("MP LABEL"): the label and its text are on the DE2 in both games; the client
    ///   still holds its label maker.
    /// - It starts a label and cancels the popup: nothing new in either game.
    /// - It edits the label's text: the new text in both games.
    /// - It deletes the label from its popup: gone from both games.
    /// </summary>
    [Scenario("gadget-labels", "Labelling a car, editing and deleting the label go through the server; a cancelled label leaves nothing")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetLabels : Scenario<GadgetLabels.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            public float Seconds = 10f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Kit kit = await pair.Host.Run<Kit>(s => InGame.SpawnMaker(s));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(kit.PersistentIds)));
            Require.That(kit.Maker != 0, "the server put a label maker by the client", kit.Note);
            await pair.Client.Run(() => Items.Pickup(kit.Maker), 60);
            Require.Equal(kit.Maker, await pair.Client.Run<ushort>(() => Items.Held()), "the client holds the label maker");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(1500);

            using (await pair.Client.Lag(o.Lag))
            {
                await pair.Client.Run(() => InGame.Label(kit.Maker, loco.Id, "MP LABEL", true));
                Expect.Equal("MP LABEL", await pair.Host.Run<string>(s => InGame.LabelsWhen(s, loco.Id, "MP LABEL", o.Seconds)),
                    "the client labelled the loco: the server's label");
                await Digests(pair, o, loco.Id, "the client labelled the loco");
                Expect.Equal(kit.Maker, await pair.Client.Run<ushort>(() => Items.Held()), "the client still holds its label maker");

                await pair.Client.Run(() => InGame.Label(kit.Maker, loco.Id, "CANCELLED", false));
                await Task.Delay(3000);
                Expect.Equal("MP LABEL", await pair.Host.Run<string>(() => InGame.Labels(loco.Id)),
                    "the client cancelled a label: the server's labels");

                await pair.Client.Run(() => InGame.EditFirstLabel(loco.Id, "EDITED", false));
                Expect.Equal("EDITED", await pair.Host.Run<string>(s => InGame.LabelsWhen(s, loco.Id, "EDITED", o.Seconds)),
                    "the client edited the label: the server's label");
                await Digests(pair, o, loco.Id, "the client edited the label");

                await pair.Client.Run(() => InGame.EditFirstLabel(loco.Id, "", true));
                Expect.Equal("", await pair.Host.Run<string>(s => InGame.LabelsWhen(s, loco.Id, "", o.Seconds)),
                    "the client deleted the label: the server's labels");
                await Digests(pair, o, loco.Id, "the client deleted the label");
            }
        }

        private static async Task Digests(Pair pair, Options o, string loco, string what)
        {
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco, host, o.Seconds + 10f)),
                $"{what}: the client's gadgets on the loco, against the server's");
        }

        public sealed class Kit
        {
            public ushort Maker;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a label maker by the client, owned by it.</summary>
            [Step]
            public static IEnumerator SpawnMaker(Step<Kit> step)
            {
                var client = Gadgets.Client();
                InventoryItemSpec spec = DV.Globals.G.Items.items
                    .Where(s => s != null && s.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && g.GetComponentInChildren<TextGadget>(true) != null &&
                                g.GetComponent<DestroyOnGadgetUnlink>() != null)
                    .OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();
                if (spec == null)
                {
                    step.Result = new Kit { PersistentIds = new string[0], Note = "no label maker in this game" };
                    yield break;
                }
                NetworkedItem item = null;
                yield return Gadgets.SpawnOwned(spec, client.WorldPosition + Vector3.right * 0.8f + Vector3.up * 0.6f, client, i => item = i);
                step.Result = new Kit
                {
                    Maker = item.NetId,
                    PersistentIds = new[] { item.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D") },
                    Note = spec.ItemPrefabName
                };
            }

            /// <summary>Labels the loco as vanilla's label maker does (TryPlace making a new gadget), and
            /// answers its popup with <paramref name="text"/> (OK) or cancels it.</summary>
            [Step]
            public static IEnumerator Label(ushort maker, string loco, string text, bool ok)
            {
                TrainCar car = Dvmp.Lib.Trains.Find(loco);
                GadgetItem gadgetItem = Items.Find(maker).GetComponent<GadgetItem>();
                Vector3 at = car.interior.TransformPoint(new Vector3(0.3f, 1.5f, 0.3f));
                GadgetItem.TryPlace(car.Customization, at, car.interior.rotation, gadgetItem, null, true, out _);
                yield return null;
                TextGadget template = gadgetItem.Gadget.GetComponentInChildren<TextGadget>(true);
                template.currentPopup?.RequestClose(ok ? PopupClosedByAction.Positive : PopupClosedByAction.Abortion, ok ? text : string.Empty);
            }

            /// <summary>Opens the first label's popup, as the label maker does on a label, and sets its
            /// text (OK) or deletes it.</summary>
            [Step]
            public static IEnumerator EditFirstLabel(string loco, string text, bool delete)
            {
                TextGadget label = Dvmp.Lib.Trains.Find(loco).Customization.Customizers.OfType<GadgetBase>()
                    .Select(g => g.GetComponentInChildren<TextGadget>(true)).First(t => t != null);
                label.ShowPopup(useExistingText: true, removeButton: true);
                yield return null;
                label.currentPopup?.RequestClose(delete ? PopupClosedByAction.Negative : PopupClosedByAction.Positive, text);
            }

            /// <summary>The loco's labels' texts, sorted and joined with "|"; "" for none.</summary>
            [Step]
            public static string Labels(string loco) =>
                string.Join("|", Dvmp.Lib.Trains.Find(loco).Customization.Customizers.OfType<GadgetBase>()
                    .Select(g => g.GetComponentInChildren<TextGadget>(true)).Where(t => t != null)
                    .Select(t => t.textMesh.text).OrderBy(t => t, StringComparer.Ordinal));

            [Step]
            public static IEnumerator LabelsWhen(Step<string> step, string loco, string expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Labels(loco);
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }
        }
    }
}
