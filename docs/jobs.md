# Jobs

How jobs are synchronised. The host runs Derail Valley's job system: it generates jobs, takes
them, validates booklets, pays out and expires them. Clients hold a mirror of every active job,
built from host packets, and never run job logic themselves. When a client puts paperwork in a
validator it sends a request; the host runs the vanilla validator and the result comes back as job
state and item updates.

The vanilla behaviour this builds on is researched in:

- [Job validator and reports](research/DERAIL_VALLEY_JOB_VALIDATOR_REPORT_LOGIC.md): overview,
  booklet and report flow, debt, the report render pipeline.
- [PageBook](research/DERAIL_VALLEY_PAGEBOOK_LOGIC.md): how booklet pages are built and how the
  current page is synced.
- [Career Manager UI](research/DERAIL_VALLEY_CAREER_MANAGER_UI_LOGIC.md): the terminal extension
  layer (no job screens yet; see [Career Manager](#career-manager)).

Job paperwork is a networked item, so the item rules in
[design/item-lifecycle.md](legacy/items/item-lifecycle.md) and [world-sync.md](world-sync.md) apply to
it. See [architecture.md](architecture.md) for the host/client model in general.

## Lifecycle

1. **Generate.** The host's `StationProceduralJobsController` generates jobs; clients are blocked
   from generating. `Station.AddJobToStation` on the host creates a `NetworkedJob`, and the next
   tick sends `ClientboundJobsCreatePacket` for the station.
2. **Mirror.** Each client rebuilds the `Job` from `JobData`, adds it to the station and prints
   the overview on the job board under the host's item NetId.
3. **Take.** A player puts the overview in a validator. On a client this becomes
   `ServerboundJobValidateRequestPacket`; the host runs `JobValidator.ProcessJobOverview`, which
   takes the job and prints a booklet issued to that player.
4. **Broadcast.** The state change and the new booklet go out in `ClientboundJobsUpdatePacket`.
   Every client marks the job taken, prints the booklet at the same validator, and destroys the
   overview, including one sitting in its inventory.
5. **Work.** Task state changes go out as `ClientboundTaskUpdatePacket`; warehouse load/unload
   is requested by clients and run by the host.
6. **Hand in.** A booklet in a validator becomes another validate request. The host runs
   `JobValidator.ValidateJob`: it prints a report (and on completion, the payment) at its
   validator. The report reaches clients as a render recipe plus a normal item Create.
7. **End.** Completed, abandoned and expired jobs go out as state updates. The host destroys
   every booklet copy; clients apply the state.

### Packets

| Packet | Direction | Purpose |
| --- | --- | --- |
| `ClientboundJobsCreatePacket` | host → clients | New jobs at one station (`JobData`). Also sent per job to a joining client. |
| `ClientboundJobsUpdatePacket` | host → clients | Per-station batch of `JobUpdateStruct`: state, start/finish time, validator station, one item NetId and position, issued player. The item is the last changed paper while it's still an object; a booklet gone since (made from its row for a deposit, in the box that frame), or still retiring into the box in that frame, sends none, or each client made one again (at the world's origin, or where it was after the deposit's Destroy). |
| `ClientboundTaskUpdatePacket` | host → clients | One task's new state and start/finish times. |
| `ServerboundJobValidateRequestPacket` | client → host | An overview or booklet put in a validator: job, station, item NetId, `ValidationType`. |
| `ClientboundJobValidateResponsePacket` | host → requester | Rejections only: `Invalid`, and `JobMissing` when the host has no such job. |
| `ServerboundJobBookletSummonRequestPacket` | client → host | The validator's reprint button. |
| `ServerboundJobRerollRequestPacket` | client → host | An offered job's overview dropped in the office bin: job, overview NetId, bin position. See [Re-rolling an offered job](#re-rolling-an-offered-job). |
| `ClientboundJobReportArtifactPacket` | host → one client | Compressed render recipe for one `JobReport`, sent just before its item Create. |
| `ServerboundWarehouseMachineControllerRequestPacket` | client → host | Warehouse Load / Unload button. |
| `ClientboundWarehouseControllerUpdatePacket` | host → clients | Warehouse screen text, job, car and cargo. |

Job packets go only to players at `PlayerLoadingState.ReadyForJobs` or later, and never to the
host's own client.

## Generation and station state

`StationController_Patch.Awake` adds a `NetworkedStationController` to every station. Its NetId is
an FNV-1a hash of the station ID, so it's the same on every peer without negotiation. Each
station also finds its `JobValidator` (queued from `JobValidator.Start`) by matching the
validator's parent name against the station's `<name>_office_anchor`.

`NetworkedStationController` (`Shared/Jobs`) is the part every game has: the lookups, the
validator and the station's `NetworkedJob`s. The host's part is `ServerStationController`
(`Server/Jobs`), added beside it, which removes itself on a client; a client's part is
`ClientStationJobs` (`Client/Jobs`), which builds the client's jobs from the host's packets.

On the host:

- `StationProceduralJobsController.TryToGenerateJobs` and
  `StationController.ExpireAllAvailableJobsInStation` run only on the host (both patches return
  `IsHost()` when a session is up, and run normally in single player).
- `StationJobGenerationRange` distance getters return the distance to the nearest player of any
  peer (`AnyPlayerSqrMag`), so a station generates and keeps jobs when anyone is near it.
- `Station.AddJobToStation` registers the job (`ServerStationController.RegisterOrQueueJob`). If
  the station's networked controller isn't ready yet, the job is queued and drained when it is;
  `ReconcileExistingJobs` also picks up anything already in the station's
  available/taken/abandoned/completed lists (jobs restored from the save).
- `Task.SetJobBelonging` gives each task a `NetworkedTask` (queued until the job's
  `NetworkedJob` exists).
- `BookletCreator.CreateJobOverview` gives the overview a host-assigned item NetId and links it to
  the job (`BindExistingJobOverview` does the same for an overview printed before registration).
- Each tick `ServerStationController` sends new jobs as one `ClientboundJobsCreatePacket` and dirty jobs as
  one `ClientboundJobsUpdatePacket` per station.

`JobData` carries the job ID and type, all tasks (`TaskNetworkData`, including nested tasks, cars,
tracks and cargo), chain origin/destination, required licences, start/finish time, wage, state,
time limit, the overview's NetId and position, and every live booklet copy (NetId, issued player,
position).

On a client, `ClientStationJobs.AddJobs`:

- waits up to 120 frames for every car the job references to exist and be initialised, then
  gives up with a warning;
- rebuilds the `Job` with the host's NetIds for the job and its tasks;
- for an `Available` job, adds it to the station and prints the overview at the host position;
- for an `InProgress` job, adds it as taken (`TakeJob(true)`, the load-game path, so the debt
  controller doesn't react) and prints every booklet copy;
- for any other state, stops there: the `NetworkedJob` exists but isn't added to the station;
- updates the car plates with the job ID.

### On join

At `ReadyForJobs` the host sends one `ClientboundJobsCreatePacket` per `Available` or `InProgress`
job, station by station. Completed, abandoned and expired jobs are not sent. `JobsManager` time is
sent earlier in the save-game data (`ClientboundSaveGameDataPacket.JobManagerTime`).

## Taking a job

`JobValidator.ProcessJobOverview` has a prefix per role: `JobValidator_ServerPatch` on the host
(`Server/Jobs`) and `JobValidator_ClientPatch` on a client (`Client/Jobs`). Both start with the
same checks (`JobValidator_Patch.TryGetOverviewJob`, `Shared/Jobs`):

- printer on cooldown: error sound, nothing else;
- no `NetworkedJob` for the overview, or the local job isn't `Available`: error sound and the
  overview is destroyed locally;
- host: records the validator and the player the booklet will be issued to (the host's own
  player, `NetworkServer.LocalPlayer`, if a client's request didn't set it), then lets vanilla run;
- client: sends `ServerboundJobValidateRequestPacket` with the overview's item NetId, unless a
  request for this job is already in flight, and skips vanilla.

The host's handler rejects the request (`Invalid`) if the item isn't the job's canonical overview.
Otherwise it sets `PendingBookletIssuedToPlayerId` to the requester and calls
`ProcessJobOverview` on the station's validator. Vanilla then takes the job (or prints a
missing-licence report, debt warning and so on), and the `CreateJobBooklet` postfix registers the
booklet as a copy issued to the requester.

The host sends no response on success. The client's `AwaitResponse` coroutine waits up to 3 s for
`ValidatorResponseReceived`; it's set when the `InProgress` job update arrives and prints the
booklet. If the update doesn't come in time, or the host rejected the request, the client plays the
error sound. Either way the in-flight flags are cleared.

### What every peer sees

The update that takes the job carries the new booklet's NetId, position, issued player and
validator station. Each client:

- moves the job from available to taken (`TakeJob(true)`);
- prints the booklet at that validator's printer under the host's NetId, plays the validator
  sound and printer animation;
- destroys its overview of the job.

Destroying paperwork goes through `NetworkedJob.RemoveFromInventory`. It ends any interaction,
removes the item from its container, then calls `InventoryIntegration.RevokeMembership` and
`InventoryIntegration.PurgeContainerMembership`. Vanilla's drop left a locked or reserved slot
pointing at the destroyed overview, so a player holding the overview when someone else took the
job kept a dead slot. If the client doesn't know which overview item belongs to the job (its item
link hadn't arrived), `DestroyJobOverview` falls back to scanning the local inventory for any
`JobOverview` of that job. Booklets are destroyed the same way.

### Ownership

There is no job owner. `NetworkedJob.OwnedBy` exists but is never set; the job is shared by the
crew. Any player can hand in any booklet of the job.

What is per player is the booklet copy. A job can have many physical booklet copies, each with its
own item NetId, and `JobBookletIssuanceIndex` (in `Multiplayer.Core`, unit tested) records at most
one copy per player, by the player's identity (OI-LF-17). The player who took the job gets the first
copy. Only the server issues: the wire still names the player by number (`IssuedToPlayerId`, through
`IJobServer.PlayerNumberOf`), and a client registers its copies unissued.

A booklet, an overview and a report are the job's, never anyone's owned item (D5, OI-ID-12): no
ledger entry, not in the owned list, though a player takes one first (`ServerLedger.Own` and the
first-taker rule leave papers out). Whoever carries one may put it in a box, and a lost booklet goes
to the Lost and Found of the player it was issued to
([lost-and-found.md](design/items/lost-and-found.md#job-papers)).

## Booklets and reports as items

Job documents are never created generically from a prefab on clients. `NetworkedItemManager`
lists `JobOverview`, `JobBooklet`, `JobReport`, `JobExpiredReport` and `JobMissingLicenseReport` as
do-not-create. The job lifecycle builds the object locally from the synced `Job`, assigns the host
NetId, and the item Create that the host still sends binds to that object; if it arrives first it
is parked in `PendingSpecialItemSnapshots` until the object exists. Clients can't adopt job
documents as temporary items.

Works:

- **Overviews** are printed from the synced job at the host's position, then placed by item sync.
  `ObserveJobOverviewRender` publishes `job.overview-render-ready` / `-timeout`.
- **Booklets**: one job, any number of physical copies, one issued copy per player. A late joiner
  rebuilds every live copy from `JobData.JobBooklets`. Incremental updates add copies by NetId
  without duplicating ones the client has.
- **Reprint** (`SummonAllActiveJobBooklets`) is replaced by `ServerboundJobBookletSummonRequestPacket`
  from every player, sent through the player's session (`IJobsClient`, `Client/Jobs`); the host's
  own player's is handled in-process ([host-local-session](design/host-local-session.md), stages 2-3:
  `LocalPlayerSession.Jobs.cs` submits it). With no player session (multiplayer off, or a dedicated
  host), vanilla's summon runs. The host's
  `JobBookletSummonCoordinator`, for every in-progress job, moves the requester's issued copy to
  the printer through an authoritative item transition (`ItemTransitionReason.JobBookletSummon`,
  clearing any retrieval claim) or prints a new copy if they have none. Other players' copies
  stay where they are. One summon runs at a time, 1.25 s per booklet.
- **Pages** are synced as the item's `pageBook.currentPage` tracked value (see the PageBook
  research doc).
- **Reports** (`JobReport`): the host's `CreateJobReport` postfix captures `Job_data` and the
  debt at print time into `JobReportArtifactData` (schema version 1, bounded sizes) and keeps it in
  `JobReportArtifactRegistry` by item NetId. When the report enters a client's interest, the host
  sends `ClientboundJobReportArtifactPacket` ahead of the item Create on the same reliable ordered
  channel. The client renders it with `BookletCreator_JobReport.Create` and binds it to the NetId.
  Interim reports (validated while still in progress) and several reports per job work, because
  reports no longer depend on a job state change. Protocol tests cover the round trip.
- **Payment** banknotes are ordinary networked items with a host-authoritative amount
  (`MoneyAmountPatch`); banking them deposits into the host's shared wallet, which is broadcast.
- **Save**: overviews and booklets are left out of the mod's world-item persistence
  (`WorldItemPersistenceSave.IsJobSystemDocument`) because DV's own job save recreates them;
  persisting both stacked duplicate overviews on the boards. The `save-reload` scenario checks the
  overview count doesn't grow.

Doesn't work, or unverified:

- **Expired and missing-licence reports** have no render recipe and no client-side producer. They
  are do-not-create but not in `IsJobDocumentPrefab`, so a client receiving one falls through to
  generic creation. What clients actually see is unverified.
- **Reports of jobs the client doesn't know** are drawn from their recipe alone (R4C C7):
  `TryMaterializeJobReport` no longer needs the `NetworkedJob`, which a joining client isn't sent
  once the job is over. A job still here keeps the report in its list.
- **Reports across a reload**: a report's recipe is saved in its row's state
  (`JobPapers.Complete`, `mpJobReport`), and a report restored from the save is made from it
  (`ItemSaveLoad.MakePaper`), its recipe registered again under its new NetId for clients.
- **Issuance across sessions**: the issuance index is keyed by identity, so a player who rejoins
  has their copy summoned, not a fresh one printed. It isn't saved: after a reload a reprint may
  issue a fresh copy rather than summon the old one.

## Validation and hand-in

`ValidateJob_Prefix` mirrors the overview path: cooldown and local-state checks, then the host
runs vanilla and a client sends `ServerboundJobValidateRequestPacket` with the exact booklet NetId.
The host rejects (`Invalid`) a booklet that isn't a registered copy of the job; otherwise it calls
`ValidateJob` on that copy at the station's validator. Vanilla then prints a report, and if the job
completes, destroys the booklet and prints the payment, all at the host's validator objects, which
item sync replicates.

`ClientboundJobValidateResponsePacket` is only sent for rejections:

- `JobMissing = true`: the host has no `NetworkedJob` with that NetId. The client's copy is stale
  and the client destroys its `NetworkedJob`.
- `Invalid` only: the offered item was refused. The client sets `ValidatorResponseReceived` and
  `ValidationAccepted = false` and keeps the job. Earlier builds destroyed the job here, which
  made it vanish for that player while the host still had it.

If the host can't find the requesting player or the station's validator, it logs and sends
nothing; the client times out after 3 s.

Debt and licences are host state. On clients,
`CareerManagerDebtController.NumberOfNonZeroPricedDebts` is 0 or 1 from the host's "has debt"
broadcast; the real check runs in the host's vanilla validator.

## Abandon, expire, complete

- **Complete**: host vanilla on hand-in. Clients move the job to completed, call `CompleteJob` and
  destroy their booklet copies.
- **Abandon**: only on the host, from vanilla paths; the mod's own one is deleting a job's car
  with the comms radio (`InProgress` jobs are abandoned, `Available` ones expired). Clients move
  the job to abandoned and call `AbandonJob`.
- **Expire**: host only (`ExpireAllAvailableJobsInStation`). Clients call `ExpireJob` and destroy
  the overview.

On complete, abandon and expire the host destroys every booklet copy and clears the issuance index
(`DestroyJobBooklets`, from `NetworkedJob` through `IJobServer.OnJobEnded`); the item destroys
replicate. Reports are kept.

### Re-rolling an offered job

Vanilla has no way to swap an offered job you don't want: a station generates jobs only when a
player enters its zone, up to its `jobsCapacity`, and expires them all when every player has left.
In multiplayer a player can drop an offered job's **overview** in the station office bin to replace
the job. A taken job's booklet in the bin still abandons the job (vanilla's path).

The bin is vanilla's `JobAbandoner`: its `OnTriggerEnter` abandons a `JobBooklet` and ignores
everything else. `JobAbandoner_ClientPatch` (`Client/Jobs`) adds overviews: when an `Available`
job's overview enters the trigger, the game whose player let go of it sends
`ServerboundJobRerollRequestPacket` (job NetId, overview NetId, bin position). Every peer's copy of
the paper falls into the bin, so only one game may ask: the paper is this player's if it is in their
hand or inventory, if this game simulates it (its physics lease), or if they let go of it in the
last 5 s, before the lease arrives (`NetworkedItem.LocallyReleasedAt`). A game asks once per job
every 2 s, since the paper has several colliders and can bounce. The request goes through the
player's session (`IJobsClient`); the host's own player's is handled in-process
([host-local-session](design/host-local-session.md), stages 2-3:
`LocalPlayerSession.Jobs.cs` submits it).

The host (`Server/Jobs/JobReroll`) ignores a request for a job that is no longer `Available` or an
item that isn't the job's overview: a second request for the same paper finds the job expired, so
two players binning it can't race. Otherwise it decides:

- **Re-roll** when nobody is in or on any of the job's cars (`NetworkedTrainCar.HasPlayers`, or a
  player's `CarId`). All its cars go, even ones coupled to a locomotive (the loco stays). The host
  removes and destroys the overview, expires the job (`Job.ExpireJob`: the chain controller releases its
  track reservations and marks the cars for vanilla's `UnusedTrainCarDeleter`), deletes the cars
  at once (`CarSpawner.DeleteTrainCars(..., forceInstantDestroy: true)`, removal cause
  `CarRemovalReason.JobRerolled`) instead of leaving them for the deleter, which waits until every
  player is far away, and calls `StationProceduralJobsController.TryToGenerateJobs`
  (`JobReroll.GenerateReplacement`). That refills the station to `jobsCapacity`; vanilla forces the
  first job it generates to be one the players are licensed for. Track space
  (`YardTracksOrganizer`) is the real limit, and the freed cars give it back. A paper carried to
  another station's bin re-rolls its own station's job; if no player is in that station's zone,
  nothing is generated there until one arrives (vanilla generates on arrival).
- **Throw the paper back out** when someone is in or on one of the job's cars (standing on it,
  riding it, in a cab). That is the only refusal. The host applies an authoritative `Thrown`
  transition (`ItemTransitionReason.JobRerollRefused`, move cause `ItemMoveReason.ThrownOutOfBin`)
  from just above the bin, at 3.5 m/s up and 2.5 m/s towards the requester. The requester is the
  transition's actor, so their game gets the physics lease and simulates the flight. The paper
  visibly comes back, which is the player's answer: nothing else is sent. Three things the throw
  has to get right (each lost it in testing):
  - `GrabHandlerItem.Throw` applies `ThrowDirection × mass × 140` as a force for one physics
    step, so the direction is the wanted velocity divided by `140 × Time.fixedDeltaTime`.
  - A gentle drop is held back for 0.3 s (`ItemReleaseGrace`), and the paper can be in the bin
    before the drop reaches the host. The host waits (up to 1.5 s) until its record no longer has
    the paper in a hand or inventory; thrown earlier, the late drop was refused and its correction
    put the paper back at the throw's start, at rest.
  - The host registers the paper in its world cell right after the transition
    (`ServerItemManager.RegisterHostWorldItem`). A paper let go on a car is recorded in the car's
    interior; moved into the world, it was in no cell until its flight was sampled, so every
    player's interest retired it and re-created it at rest.

  When the host's own player binned it, the host simulates the flight, and granting that lease
  ends the one its drop had, which stops the body (`RestoreLocalBodyAfterLease`): the host sets
  the paper's velocity once the new lease exists.

Every peer sees the result through the usual sync: the `Expired` job update, the item destroy, the
car deletes, and a `ClientboundJobsCreatePacket` for the replacement. Debug events:
`job.reroll-accepted`, `job.reroll-refused` (with the reason).

Generation goes through `GenerateReplacement` and vanilla's generator, so a later add-on that wants
to claim some generated jobs before their overviews print hooks the generator, not the re-roll.

### Save, load and rejoin

The host's save is the only job save. DV saves and restores the host's jobs; restored jobs are
registered by `ReconcileExistingJobs` and `OnJobTaken` ignores the load-game take, so a load
doesn't broadcast spurious state changes. A client keeps nothing between sessions: on every join
or rejoin it gets the active jobs afresh at `ReadyForJobs`, with all live booklet copies.

A booklet or overview a player carries is a row, saved with them like any item
(`ItemSaveLoad.IsUnsavedJobPaper`), and back in their slot when they're back (D14, OI-COLD-07,
OI-COLD-15; see "Papers in inventories" below). Ones lying about or on a board aren't saved: the job
system makes them again. On load DV prints a replacement booklet for each taken job into the local
player's inventory (`JobSaveManager.CreateAndInitializeJobBookletForJob`). `JobBookletRecoveryPatch`
holds it until the ledger is loaded and the host's own rows are back, and skips it for a job whose
booklet a player carries (`JobPaperRows.HasBookletRow`). On a player host the rest go to the host's
player; a dedicated host has none, and the patch skips them all there. Players without their
booklet reprint it at a validator.

### Papers in inventories

Under D14 a player's inventory holds rows, not objects, and job papers are no exception
(`JobPapers`, `JobPaperRows`):

- **The job keeps its papers by NetId.** `NetworkedJob` keeps its overview's NetId and every booklet
  copy's, object or row. `JobOverview` and `JobBooklets` give only the objects, so `JobData` sends
  only the papers that are objects.
- **A stashed paper is data only on the server and a stand-in in its holder's game.** Its row's state
  names its job: a booklet's own `jobId`, and an overview's added by `JobPapers.Complete`. A report's
  carries its render recipe (`mpJobReport`, the artifact), which outlives its job.
- **Its object is made again from its job when it leaves the slot**, under the same NetId
  (`JobPapers.Make` and `Track`, with `BookletCreator`). The server's `BookletCreator` patches leave
  it unregistered while it's made. The job then registers it again, and other games print their copy
  from that update. A game that has the paper as a stand-in doesn't print it. When the paper goes
  back into its row, every game ready for items is told it's gone, not only the ones that knew it: a
  far game printed its copy from the job's update too.
- **Joining.** A paper's row goes live only while its job wants it (`JobPaperRows.Wanted`):
  - a booklet while its job is in progress;
  - an overview while its job is offered, in place of the one the job printed on the board at the load.
  
  Otherwise it goes, as vanilla's load destroys the booklets no job claims. A report has no job to
  print it again: one with no free slot (or homed in Lost and Found) goes to Lost and Found like other
  items, made from its recipe in `PlayerItems.GoLive`. An unowned one is left at the player's feet,
  as other unowned items are, since Lost and Found takes only owned items. One with no recipe (saved before C7) can never be made, so it
  goes. The host's own join draws its other rows at once and waits up to 10 s for its papers' jobs to
  be registered, so a booklet isn't dropped (and printed again) for a job not known yet.
- **Leaving.** A holder who leaves takes their papers' rows cold, and their jobs forget the NetIds
  (`JobPaperRows.Forget`), so an overview's row is wanted again at their rejoin. `NetworkedJob` looks
  its papers up by kind, so a NetId kept by mistake never stands for another item.
- **When a job ends**, its papers in inventories go with it (`JobPaperRows.OnJobEnded`): a connected
  holder's is made, taken out of its slot and destroyed, and an absent holder's row is dropped. A job
  update for an overview that has gone since carries none, and a station clears its dirty jobs before
  it sends them, so one that fails can't hold up the station's updates.
- **A summon** of a stashed copy makes its object first, then moves it to the printer.

## Tasks and warehouses

`Task.SetState` on the host (except to `InProgress`) sends `ClientboundTaskUpdatePacket`, which
clients apply with `Task.SetState` plus the times. Warehouse Load/Unload buttons on a client send
`ServerboundWarehouseMachineControllerRequestPacket` (`WarehouseMachineControllerClientPatch`); the
host runs the sequence and its `WarehouseMachineController.SetScreen` broadcasts the screen
(`WarehouseMachineControllerServerPatch`; idle and train-in-range presets are left to each peer).

## Career Manager

The Career Manager extension (`Multiplayer/Client/Career/`) adds a `MULTIPLAYER`
hub with a Lost Items screen; it has no job screens. Job-relevant Career Manager state is the debt
flag above and licences, which are host-owned and broadcast on purchase.

## Mod integration

- **RemoteDispatch** (client-side compatible): `RemoteDispatchPatch` adds every player's position
  and crew to its map. Nothing job-specific.
- **BookletOrganizer** is registered as host-only and **dv-improved-job-overview** as
  client-only compatible (`ModCompatibilityManager`); neither has a patch.

## Testing

The `jobs` scenario (`DevTools/DvmpScenarios/Scenarios/JobsRoundTrip.cs`, needs a running pair from `dvmp up`) runs
a transport or empty-haul job at a station from start to finish: one peer takes it, the host
stands in for the drive by moving the cars to the destination track, one peer hands the booklet
in, and the client banks the payment. Both peers must agree on the job state and booklets after
each step, the client must see the banknotes, and the shared wallet must go up by the payment
exactly once.

```bash
dvmp scenario run jobs --session NAME --arg ACCEPT=host --arg HANDIN=client   # what gate.sh runs
dvmp scenario run jobs --session NAME   # the defaults: ACCEPT=client HANDIN=client STATION=FM
```

`ACCEPT` and `HANDIN` take `host` or `client`.

The `job-reroll` scenario bins an offered job's overview with nobody on its cars (the job and cars
go and a replacement is offered, the same on both peers), then has the client board a car of
another job and bins that one (the paper comes back out and lands at the same spot on both peers).
`WHO=client` (the default, also on a dedicated host) or `WHO=host` bins the paper.

```bash
dvmp scenario run job-reroll --session NAME                     # WHO=client
dvmp scenario run job-reroll --session NAME --arg WHO=host     # a player host
```

Unit tests: `JobBookletIssuanceIndexTests`
(`Multiplayer.Core.Tests`), `JobBookletProtocolTests` and `JobReportArtifactTests`
(`Multiplayer.Protocol.Tests`). See [testing.md](testing.md).

## Known gaps

Open work from an upstream review, to port:

- **Job update races.** An update for a job the client hasn't built yet (still waiting for its
  cars, or never built because the wait timed out) is dropped silently; nothing replays it.
- **`JobsManager` registration for RemoteDispatch.** Clients build jobs through the station and
  `Job.TakeJob(true)` and never register them with `JobsManager`, which RemoteDispatch reads.
- **Validation null check.** For example, `HandleJobStateChange` prints the booklet at
  `validator.bookletPrinter` without checking that the station it found has a validator.
- **Unload hardening.** The static job caches (`NetworkedJob` lookups, pending tasks, pending host
  jobs, `JobReportArtifactRegistry`) are skipped on unload and `JobReportArtifactRegistry.Clear`
  is never called.
- **Work-train "in use" rework.** Work-train requests treat a car as in use if it belongs to an
  in-progress job (`JobsManager.GetJobOfCar`).

Also open:

- A client hand-in gets no positive response, and neither the interim report nor the `Completed`
  update sets `ValidatorResponseReceived`, so the client plays the error sound after the 3 s
  timeout even when the hand-in worked. Clients also don't play the printer animation for a
  hand-in.
- `JobMissing` destroys only the `NetworkedJob` GameObject. The local `Job` stays in the station
  lists and its paperwork stays; `ClientStationJobs.RemoveJob`, which would clean up, is
  never called.
- When the local job state disagrees, the validator prefixes destroy the offered overview or
  booklet locally on a client, without asking the host.
- Clients clear car plates on complete/abandon/expire by calling `UpdateCarPlates` with an empty
  job ID, which returns immediately, so the explicit clear does nothing.
- The rest of the report gaps listed under [Booklets and reports](#booklets-and-reports-as-items).
