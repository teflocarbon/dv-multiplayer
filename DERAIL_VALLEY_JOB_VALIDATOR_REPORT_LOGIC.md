# Derail Valley Job Validator and Job Report Logic

## Implemented multiplayer path (WIP)

The mod now treats a printed `JobReport` as a distinct host-authored artifact rather than an
implicit consequence of a `JobState` update:

1. The host `BookletCreator.CreateJobReport` postfix assigns the physical report its authoritative
   item NetId and captures the base game's detached `Job_data` and optional `Debt_data` values at
   print time.
2. `JobReportArtifactData` converts that data into a bounded schema-version-1 payload. Tasks,
   nested tasks, task cars, track identities, cargo identities, payment values, and semantic debt
   save data are copied; live Unity and job objects never cross the wire.
3. The host retains the recipe by item NetId. When an interested client is about to receive the
   report's normal item `Create`, the artifact packet is sent first on the same reliable ordered
   channel.
4. The client recreates the runtime-rendered report through `BookletCreator_JobReport.Create`, binds
   that exact object to the host NetId, and then lets the existing item snapshot path apply the
   authoritative placement and state.
5. Multiple reports for one job are represented independently. The former singular
   `NetworkedJob.JobReport`/`DirtyCause.JobReport` path and completed-state implicit report creation
   have been removed.

Observability events currently emitted are:

```text
job.report-artifact-sent
job.report-artifact-received
job.report-artifact-materialized
job.report-artifact-rejected
```

The packet projector decodes the bounded semantic recipe instead of displaying its compressed byte
array. Protocol tests cover full recipe round-trip, no-debt reports, and schema rejection.

## Implemented cooperative JobBooklet model (WIP)

The native reprint button does not create a second booklet. It enumerates the process-local
`JobBooklet.allExistingJobBooklets` collection and physically moves every existing active-job
booklet to the selected validator. That behavior is unsuitable for multiplayer: relocating the one
canonical booklet would take it out of a teammate's hand or inventory whenever another player
pressed reprint.

The multiplayer implementation now uses this identity model:

```text
one synchronized Job
-> zero or more authoritative physical JobBooklet copies
-> at most one currently issued copy per player
-> one unique item NetId per physical copy
```

The host records every booklet by item NetId and separately records the player to whom that copy
was issued. Starting a job issues the first copy to the player who submitted the JobOverview. A
client validation request includes the exact physical booklet item NetId; the host no longer
validates whichever booklet happened to be stored in a singular `NetworkedJob.JobBooklet` field.

When a player presses the validator reprint button, the client sends a local validator summon
request to the host. For each in-progress job, the host then:

1. finds the copy already issued to that requesting player;
2. relocates that copy to the printer using the normal authoritative item transition path; or
3. creates a new host-authoritative copy when the player has never been issued one or their prior
   copy was destroyed;
4. publishes the new/relocated copy through the existing job and item synchronization paths;
5. leaves every teammate's issued copy untouched.

Relocating an existing copy is a forced world transition, including when that copy currently sits
in a player's local inventory. Derail Valley's `Inventory.PurgeFromInventory(GameObject)` is not a
general removal operation: it only recognizes entries in `lockedAndReservedMap`. The multiplayer
application path must first call the normal `DropItemFromHandsOrInventory` transition so ordinary
and equipped entries leave the inventory, then purge a remaining locked/reserved silhouette only
when the authoritative summon has cleared that retrieval claim. Otherwise the printer transform
and the inventory slot reference the same Unity object, and the stale inventory state immediately
publishes a newer `InInventory` snapshot that undoes the reprint for every peer.

Initial job state includes all live booklet copies, including item NetId, issued-player ID, and
position. Incremental job updates carry the same issued-player ID for newly created copies. This
means a late-joining client reconstructs every physical copy rather than only the last booklet
created for that job.

The issuance index is pure state logic and has tests for:

```text
two teammates receiving different copies
one copy never being issued to two players
reissuing one player without changing a teammate's copy
destroying a copy making only its player eligible for replacement
zero player and item identities being rejected
```

Completing, abandoning, or expiring a job destroys every remaining authoritative booklet copy and
clears its issuance mappings. Destroying an individual copy removes only that copy and its player
mapping.

### Retrieval and inventory semantics

`InventoryItemSpec.IsEssential` is not treated as network ownership or retrievability. The actual
base-game inventory claim is authoritative for the recall presentation:

```text
inventory slot >= 0
and
Reserved or Locked inventory flag
```

Ordinary inventory placement does not establish a persistent recall owner. A genuinely reserved or
locked slot does. When the validator forcibly summons a shared JobBooklet, the host clears any
player retrieval claim so the booklet cannot leave a stale red silhouette or remain recallable by
the previous holder after being moved to the printer.

### Deferred item safety

A legacy revision-zero deferred Create may still contribute tracked values, but it can no longer
replace placement from a newer authoritative revision. Host delivery also stamps authoritative
ownership, revision, and inventory-claim metadata onto Create snapshots before transmission. These
rules prevent a late booklet Create from rolling a currently held or inventoried copy back to its
old printer position.

### New observability events

```text
job.booklet-summon-started
job.booklet-copy-issued
job.booklet-copy-summoned
job.booklet-summon-completed
```

Each copy event includes job identity, physical item NetId, and issued player ID. Item transition
events remain the source of truth for the resulting placement and authority revision.

### Cooperative acceptance cases

1. Host starts a job and receives booklet copy A.
2. Client presses reprint and receives independently networked copy B.
3. Both players can hold and turn pages on their copies simultaneously.
4. Client validates copy B by its exact item NetId.
5. Interim report printing leaves both booklet copies alive.
6. Reprinting for either player summons only that player's issued copy.
7. A forced summon clears stale reserved/locked recall presentation for the moved copy.
8. A forced summon of an ordinary or equipped copy removes its real inventory membership before
   applying the printer transform.
9. The moved copy remains `Dropped` after the next host item scan and cannot emit a corrective
   `InInventory` revision.
10. Completing the job removes both copies on host and clients.
11. A late joiner reconstructs all currently live copies with no duplicate item NetIds.

## Purpose

This document records the observed base-game lifecycle for the job validator, JobBooklet validation, and JobReport rendering. It also explains why the existing multiplayer implementation synchronizes JobOverview and JobBooklet more reliably than JobReport, and outlines a general network model for rendered job documents.

The findings are based on decompiled Derail Valley code supplied from:

- `JobValidator`
- `BookletCreator`
- `BookletCreator_JobReport`
- `BookletCreator_Debt`
- `JobReport`
- `JobDebtController`
- the current multiplayer job and item synchronization code

## Executive summary

Job reports are not ordinary prefab-based items. Their visible pages are generated at runtime from two independent inputs:

1. a snapshot of the `Job`, converted to `Job_data`;
2. an optional snapshot of the applicable `DisplayableDebt`, converted to `Debt_data`.

The resulting textures are generated locally and assembled into a `MultipleRenderedTexturesBooklet`. A generic `JobReport` prefab Create packet therefore cannot reconstruct the report contents by itself.

The current multiplayer failure is deterministic:

1. The host validates a JobBooklet and creates a JobReport.
2. The host assigns the report a network item ID and sends both job/item updates.
3. The client receives the generic item Create.
4. `NetworkedItemManager` recognizes `JobReport` as a job-owned document and defers generic creation while waiting for the job lifecycle to create the real report.
5. The client job update only invokes `HandleJobStateChange()` if the `JobState` changed.
6. Printing an interim report leaves the job `InProgress`; printing another completed report leaves it `Completed`.
7. No client JobReport is created, so the deferred item Create can never bind.

This is not fundamentally an item transport failure. It is a missing job-document creation message and missing report render data.

## Base-game validator flow

### Starting a job from a JobOverview

`JobValidator.ProcessJobOverview(JobOverview)` performs the base-game validation required to take a job. Depending on the validation result it may print:

- an expired-job report;
- a tutorial warning;
- a missing-license report;
- a debt warning;
- or the actual JobBooklet.

For a successful job start it:

1. takes the job at the station;
2. calls `BookletCreator.CreateJobBooklet(job, ...)`;
3. plays the validator success sound;
4. triggers the booklet printer animation.

The JobBooklet can be reconstructed on each process because the multiplayer implementation already synchronizes the associated `Job`. The client calls the same base-game booklet creator with its corresponding local Job object, assigns the host-issued item NetId, and then lets item synchronization control placement and page state.

### Validating a JobBooklet

`JobValidator.ValidateJob(JobBooklet)` requires:

- a non-null booklet;
- a non-null associated job;
- `JobState.InProgress`;
- the job to exist in `JobsManager.currentJobs`;
- the booklet printer not to be on cooldown.

The important body is conceptually:

```csharp
Job job = jobBooklet.job;
bool completed = JobsManager.Instance.TryToCompleteAJob(job) == JobState.Completed;

DisplayableDebt debt = completed
    ? JobDebtController.Instance.LastStagedJobDebt
    : JobDebtController.Instance.GetExistingJobDebtForJob(job);

if (debt != null && !debt.IsStaged)
    debt.UpdateDebtState();

BookletCreator.CreateJobReport(job, debt, printerPosition, printerRotation,
    WorldMover.OriginShiftParent);

bookletPrinter.Print(false);

if (completed)
{
    jobBooklet.DestroyJobBooklet();
    moneyPrinter.PrintPayment(job);
}
```

Consequences:

- validation always prints a JobReport, even while the job remains in progress;
- validating the same in-progress job repeatedly can create multiple physical reports;
- a completed job can also have more than one printed report;
- completion changes job state and destroys the JobBooklet;
- interim validation changes neither job state nor JobBooklet ownership;
- the debt input is different for interim and completed reports.

## Debt selection and lifecycle

### In-progress validation

For a job which did not complete, the report uses:

```text
JobDebtController.GetExistingJobDebtForJob(job)
```

If the returned debt is not staged, `UpdateDebtState()` refreshes it before rendering. This means the report reflects a point-in-time view of the current job debt and damage state.

### Completed validation

Completing, abandoning, or expiring a job causes `JobDebtController` to stage its existing job debt:

1. unregister the existing debt;
2. update its debt state;
3. obtain `JobDebtData` from the tracker;
4. create a `StagedJobDebt` when the total is positive;
5. store that object as `LastStagedJobDebt`;
6. otherwise set `LastStagedJobDebt` to null;
7. advance and clear the job tracker snapshots.

The completed JobReport consumes `LastStagedJobDebt`. A client that did not run the authoritative completion/debt calculation cannot safely substitute its own `LastStagedJobDebt`; it may be null, stale, or refer to a different job.

## JobReport rendering pipeline

### Public entry point

`BookletCreator.CreateJobReport(Job, DisplayableDebt, ...)` forwards to:

```csharp
BookletCreator_JobReport.Create(job, debt, position, rotation, parent);
```

The first overload immediately detaches the render inputs from the live game objects:

```csharp
Debt_data debtData = debt == null ? null : new Debt_data(debt);
Job_data jobData = new Job_data(job);
return Create(jobData, debtData, position, rotation, parent);
```

This is highly relevant for multiplayer. `Job_data` and `Debt_data` are already purpose-built render snapshots and are better conceptual network boundaries than live `Job` or `DisplayableDebt` objects.

### Runtime object and texture creation

The snapshot overload:

1. instantiates the `JobReport` prefab;
2. names it `JobReport[{job.ID}]`;
3. assigns `JobReport.jobID`;
4. calculates the number of debt pages;
5. creates job summary/task/payment template data;
6. instantiates `JobReportRender` and requests report textures;
7. optionally instantiates the debt-specific `FeesBookletRender`;
8. otherwise instantiates `FeesNoDamageRender`;
9. registers every asynchronous texture producer with the report;
10. returns the physical report while rendering continues.

`JobReport` inherits `MultipleRenderedTexturesBooklet`. That component waits for every registered renderer, combines the generated texture arrays, assigns `PageBook.pageTextures`, and calls `PageBook.Generate()`.

The physical object may therefore exist before its pages have finished rendering. Item binding and PageBook state application must tolerate this asynchronous interval.

### Job-derived contents

The report pages use `Job_data` for at least:

- job ID;
- whether the job is complete or still in progress;
- completion time or current time on job;
- time limit;
- base payment;
- bonus payment;
- total payment;
- job tasks and nested tasks;
- task completion state;
- derailment, missing-car, coupling, destination-track and handbrake warnings;
- page totals.

The client already has much of this information through synchronized jobs and tasks, but the rendered report should use a single authoritative snapshot. Rendering from a later live Job can produce a document which differs from the report that the host printed earlier.

### Debt-derived contents

When debt exists and `totalPrice > 0`, the report chooses a debt renderer from `DebtType` and produces one or more fee pages. Debt rendering uses data including:

- debt ID and type;
- whether it is staged;
- whether it is job, locomotive, owned-car or other debt;
- taxable and fee-tolerance flags;
- component-level car debt data;
- before/after snapshot amounts for job debt;
- price per unit and total price;
- car type and loaded cargo type;
- environment damage types and totals;
- fee assessment and warning state.

If there is no positive debt, the report still adds the no-fees pages. Omitting debt data is therefore not equivalent to omitting the report appendix.

## Exact detached render snapshots

### `Job_data`

The base game already snapshots a live `Job` into a serializable `Job_data` object. Its complete public field set is:

```text
ID
type
state
completionTime
timeOnJob
timeLimit
basePayment
bonusPayment
totalPayment
requiredLicenses
tasksData
chainOriginStationInfo
chainDestinationStationInfo
```

For the supplied `BookletCreator_JobReport.GetReportTemplateData()` implementation, the directly consumed fields are narrower:

```text
ID
state
completionTime
timeOnJob
timeLimit
basePayment
bonusPayment
totalPayment
tasksData
```

The report renderer shown does not directly consume job type, licenses, or station info. Those fields may still be useful for schema consistency with other booklet kinds, but a JobReport-only version-1 payload does not need to serialize them unless a nested task renderer depends on them.

`Job_data(Job)` calculates all payment and timing values at construction time. This reinforces that the network payload must be captured on the host at print time instead of reconstructed from a later client Job.

### `Debt_data`

`Debt_data(DisplayableDebt)` detaches all top-level debt values used for rendering:

```text
ID
debtType
totalPriceOfDamageableResources
sumOfDebts1
sumOfDebts2
environmentDamageTotalPrice
totalPrice
isTaxable
isStaged
debtData[]
countsInFeeTolerance
```

Its derived properties (`IsJobDebt`, `IsJobOrOther`, `IsLocoDebt`, `IsOwnedCarDebt`, and `EnvironmentDamageTypes`) are calculated from `debtType`; they do not need to be serialized.

`countsInFeeTolerance` is time-sensitive. It compares the debt activation time with the most recent 07:00 boundary, so the host-computed Boolean must be transmitted rather than recalculated later on the client.

The public value constructor is suitable for client reconstruction:

```csharp
new Debt_data(
    id,
    debtType,
    totalPriceOfDamageableResources,
    sumOfDebts1,
    sumOfDebts2,
    environmentDamageTotalPrice,
    totalPrice,
    isTaxable,
    isStaged,
    carDebtData,
    countsInFeeTolerance);
```

### `CarDebtData`

Each debt entry contains:

```text
id
carType
loadedCargoType
trackedDebts[]
```

The renderer calls methods on `CarDebtData` to:

- filter unchanged components;
- calculate page count;
- calculate component prices using current game resource parameters;
- distinguish before-snapshot and after-snapshot amounts;
- generate printable component details.

`CarDebtData` provides a semantic save round-trip:

```csharp
JObject GetCarDebtSaveData();
static CarDebtData LoadCarDebtFromSaveData(JObject data);
```

The save representation contains only:

```text
id
carType
optional cargoType
debts[]
```

This creates two viable protocol strategies:

1. Explicitly serialize `DebtComponent` fields and construct `CarDebtData` normally.
2. Serialize each `GetCarDebtSaveData()` result as bounded JSON and reconstruct it through the game's own validated loader.

The explicit DTO remains preferable for compile-time validation and packet projection. The save-data adapter is a reasonable version-1 fallback because it is semantic game save data, not reflected runtime state. It must still have size limits, schema versioning, parse failure diagnostics, and tests.

### `PrintDebtComponentDetails`

This is derived render output rather than required source state:

```text
type
beforeSnapshotAmount
afterSnapshotAmount
totalAmount
pricePerUnit
totalPrice
```

It should normally be recomputed locally from `CarDebtData` and its `DebtComponent` values. Transmitting it instead would require changing or duplicating `BookletCreator_Debt`, because the existing renderer expects `CarDebtData`.

### `Task_data`

The report task snapshot is a recursive tree with the following fields:

```text
type
instanceTaskType
state
taskStartTime
taskFinishTime
cars[]
startTrackID
destinationTrackID
warehouseTaskType
cargoTypePerCar[]
totalCargoAmount
couplingRequiredAndNotDone
anyHandbrakeRequiredAndNotDone
nestedTasks[]
```

`Task_data(Task)` snapshots destination-track membership for every car at construction time and recursively snapshots nested tasks. This data must therefore come from the host's report-print instant; reconstructing it later from the client's live task can change warnings and completion marks.

`Task_data` has a complete public value constructor, so the client can rebuild the base-game render object without reflection.

The existing multiplayer task protocol already establishes the stable track representation: `TrackID.RailTrackGameObjectID` is sent as a string and resolved through `RailTrackRegistry`. Cargo values already use `CargoTypeLookup`. The report DTO should reuse those conventions.

### `Car_data`

Each task car snapshot contains:

```text
ID
type                  // TrainCarLivery
derailed
isOnDestinationTrack
length
carOnlyMass
capacity
```

The host must transmit the two point-in-time Boolean values. `TrainCarLivery` should cross the wire as its stable livery/type identifier and be resolved through the client's game type registry. Do not attempt to serialize the live `TrainCarLivery` object.

The repository already provides this mapping through `TrainComponentLookup.LiveryFromId()`, backed by `Globals.G.Types.Liveries`. No new livery registry is required.

### `DebtComponent`

The complete source state required to reconstruct a debt component is:

```text
type          // ResourceType
startValue
snapshotValue // -1 means no snapshot
endValue
```

The derived values used in report rendering are deterministic:

```text
StartToEndDiff      = round1(startValue - endValue)
StartToSnapshotDiff = round1(startValue - snapshotValue)
SnapshotToEndDiff   = round1(snapshotValue - endValue)
HasSnapshot         = snapshotValue != -1
```

It has a direct reconstruction constructor:

```csharp
new DebtComponent(startValue, resourceType, endValue, snapshotValue);
```

Its save representation confirms the same minimal schema: `start`, `end`, optional `snap`, and `type`.

## Concrete version-1 DTO mapping

The research now supports a fully explicit payload:

```text
JobReportArtifactData
├── schemaVersion
├── artifactToken
├── itemNetId
├── jobNetId
├── jobId
├── validationStationNetId
├── position / rotation
├── job
│   ├── state and timing
│   ├── payment values
│   └── tasks[]
│       ├── task enums/timing/flags
│       ├── track stable IDs
│       ├── cargo types and amount
│       ├── cars[]
│       │   ├── stable car ID
│       │   ├── stable livery ID
│       │   ├── derail/destination flags
│       │   └── length/mass/capacity
│       └── nestedTasks[]
└── optional debt
    ├── top-level Debt_data scalar fields
    └── cars[]
        ├── stable car ID/type/cargo type
        └── components[]
            └── resource type/start/snapshot/end
```

Every collection and recursion level must be bounded during deserialization. Suggested initial limits:

```text
maximum top-level tasks: 64
maximum nested depth: 8
maximum nested tasks per node: 64
maximum cars per task: 128
maximum debt cars: 128
maximum debt components per car: 64
maximum string length: 256
```

The payload projector should display compact semantic summaries and explicitly report truncation or rejection. It must never reflect through `TrainCarLivery`, `TrackID`, or other game objects.

## Why JobOverview and JobBooklet currently work

JobOverview and JobBooklet are deterministic products of the synchronized Job model:

```text
host job lifecycle event
→ synchronize job state, item NetId, validator and position
→ client finds the corresponding local Job
→ client invokes the same booklet creator
→ runtime textures are generated locally
→ generic item Create binds to that purpose-built object
```

JobReport has an additional unsynchronized input:

```text
Job + point-in-time DisplayableDebt
```

It can also be printed without a JobState transition. The current client job handler incorrectly treats document creation as a side effect of state changes only.

## Current multiplayer implementation gap

### Lost dirty cause

`NetworkedJob` records a `DirtyCause`:

```text
JobOverview
JobBooklet
JobReport
JobState
```

`ClientboundJobsUpdatePacket.FromNetworkedJobs()` uses that cause on the host to choose an item NetId and position, but `JobUpdateStruct` does not serialize the cause. The client only receives the job state and a context-free item ID.

As a result, a packet with:

```text
JobState = InProgress
ItemNetID = 767
```

cannot tell the client whether 767 is the existing JobBooklet or a newly printed JobReport.

### State-change-only creation

`NetworkedStationController.UpdateJobs()` currently calls `HandleJobStateChange()` only if the state differs. That works for:

- Available to InProgress, which creates a JobBooklet;
- InProgress to Completed, which creates the first completion report.

It fails for:

- InProgress to InProgress report printing;
- Completed to Completed report reprinting;
- any other document event whose identity changes without a job-state transition.

### Deferred item Create

`NetworkedItemManager` intentionally avoids generically creating JobOverview, JobBooklet, and JobReport. If the purpose-built object does not yet exist, it stores the Create in `PendingSpecialItemSnapshots`.

This is the correct strategy for rendered documents, but it needs a guaranteed producer. For the failed JobReport:

```text
host item Create sent
→ client receives Create
→ client emits item.special-create-deferred
→ reason: waiting-for-job-lifecycle-object
→ no job-document create event arrives
→ pending snapshot never binds
```

The dashboard previously called this `sent-not-received`. It now treats `item.special-create-deferred` as received and handled, allowing the real `received-not-applied` discontinuity to be reported.

### Multiple report identity

`NetworkedJob` contains both:

- a singular `JobReport` property;
- a `JobReports` list.

The packet builder reads only the singular property. Each new report overwrites it, although multiple physical reports can exist. A durable protocol must identify each printed artifact by its authoritative item NetId or another unique report instance ID, not merely by job ID and document kind.

## Recommended general document protocol

Do not transmit textures. Render them locally from detached semantic data.

Treat job document creation as a first-class lifecycle event separate from job state and generic item placement.

### Suggested envelope

```csharp
public enum JobDocumentKind : byte
{
    Overview,
    JobBooklet,
    JobReport,
    JobExpiredReport,
    JobMissingLicenseReport,
    TutorialWarning,
    DebtWarning
}

public sealed class JobDocumentCreateData
{
    public ushort ItemNetId;
    public ushort JobNetId;
    public string JobId;
    public JobDocumentKind Kind;
    public uint ValidationStationNetId;
    public Vector3 Position;
    public Quaternion Rotation;
    public string ArtifactToken;
    public int SchemaVersion;

    // Present for JobReport.
    public JobReportRenderData Report;
}
```

`ArtifactToken` or `ItemNetId` must distinguish repeated reports for the same job.

### Suggested report render snapshot

The ideal report payload mirrors the detached base-game render inputs:

```text
Job_data
optional Debt_data
```

Do not serialize arbitrary reflected runtime objects. Define explicit network DTOs or explicit adapters for the fields actually consumed by `BookletCreator_JobReport` and `BookletCreator_Debt`.

Two implementation options are reasonable:

1. Serialize explicit multiplayer `JobReportRenderData` and `DebtRenderData` DTOs, then reconstruct `Job_data`/`Debt_data` locally.
2. If the base-game snapshot classes are stable, explicitly serialize their required public fields with a schema version.

The first option is more work but isolates the protocol from game updates and prevents accidental serialization of Unity or computed properties.

### Client creation order

The client should handle a document create idempotently:

1. Reject or merge duplicate `ArtifactToken`/NetId messages.
2. Resolve the synchronized Job by `JobNetId` and verify its string ID.
3. Resolve the validator/printer only for presentation position and effects.
4. Create the correct document using detached render data.
5. Add and initialize `NetworkedItem` with the host-issued NetId.
6. Register it with the owning `NetworkedJob` without emitting a client dirty event.
7. Drain `PendingSpecialItemSnapshots` for that NetId.
8. Allow the normal item snapshot to authoritatively apply placement, holder, ownership and page state.
9. Play the printer effect locally once, if desired.

Either packet order must work:

```text
document create → item Create
item Create → document create
```

### Separation of responsibilities

Keep these states separate:

| Concern | Authority/source |
|---|---|
| Document instance identity | Host-issued item NetId/artifact token |
| Job association | Host job-document event |
| Rendered contents | Host semantic render snapshot, rendered locally |
| Physical placement | Existing item synchronization |
| Current page | `pageBook.currentPage` tracked value |
| Grab/inventory/throw state | Existing item state machine |
| Printer sound and animation | Local presentation side effect |

This avoids encoding printer or rendering behavior into generic item synchronization.

## Protocol and lifecycle concerns

### DirtyCause must be explicit

At minimum, job update packets need to serialize their semantic cause. A more robust approach is a dedicated job-document packet, because a station's dirty-job list can coalesce multiple mutations before the next tick and overwrite `NetworkedJob.Cause`.

For example, job completion can cause:

```text
JobState dirty
JobReport created
JobBooklet destroyed
```

A single mutable enum cannot reliably represent all three operations. An explicit queued document event preserves every report instance.

### Report data must be captured immediately

Capture `Job_data` and `Debt_data` at the same moment the host prints the report. Do not reconstruct the payload on a later network tick from live state:

- the job may progress or complete;
- debt may be updated or paid;
- `LastStagedJobDebt` may point at a later job;
- task/car state may change;
- repeated validation may create semantically different reports.

### Avoid feedback from client construction

The existing `BookletCreator` Harmony postfixes mark `NetworkedJob` dirty only on the host. Preserve that rule. Client-side reconstruction must bind the object without scheduling a new authoritative job-document event.

### Track terminal outcomes

Every deferred special Create needs one of:

- bound and applied;
- explicitly rejected;
- timed out with its dependency named;
- cancelled because the corresponding authoritative artifact was destroyed.

Pending special snapshots should not remain unbounded for the session.

## Observability recommendations

Add events with `jobNetId`, `jobId`, `documentKind`, `itemNetId`, `artifactToken`, validator ID and render schema version:

```text
job.document-print-requested
job.document-render-snapshot-created
job.document-create-sent
job.document-create-received
job.document-render-started
job.document-render-complete
job.document-item-bound
job.document-pending-item-drained
job.document-create-duplicate
job.document-create-rejected
job.document-render-failed
```

For reports also include:

```text
job state at print time
completed flag
debt present
debt staged
debt type
debt ID
debt total
job/task snapshot fingerprint
debt snapshot fingerprint
expected page count
generated page count
```

The item replication dashboard should show `item.special-create-deferred` as received/handled but not applied. It should link the item operation to the corresponding job-document creation operation.

## Acceptance tests

### Interim report

1. Start a job.
2. Validate its JobBooklet before completion from the client.
3. Host remains authoritative for validation and debt refresh.
4. Host and client print exactly one corresponding JobReport instance with the same NetId.
5. Both reports show `In Progress` and equivalent task/debt contents.
6. The JobBooklet remains valid.
7. The pending item Create binds and drains.

### Completion report

1. Complete a job and validate from either host or client.
2. Host stages the correct job debt and snapshots it immediately.
3. Every client creates one report from the same render payload.
4. The JobBooklet is destroyed everywhere.
5. Payment and job state remain host authoritative.
6. Report page count and semantic fingerprints agree across processes.

### Repeated reports

1. Print the same in-progress report multiple times.
2. Each print receives a different item NetId/artifact token.
3. No previous report is overwritten in the job registry.
4. Every report remains independently grabbable and page-synchronized.
5. No duplicate Unity object owns the same NetId.

### Packet ordering

Test both document-first and item-first delivery. Both must create exactly one purpose-built report and leave no pending special snapshot.

## Remaining decompilation targets

The semantic source model required for a version-1 JobReport payload is now understood. No additional decompilation is required before implementing the DTO and lifecycle.

Optional follow-up inspection:

- `JobReportRender`, `FeesBookletRender`, and `FeesNoDamageBookletRender` if texture-generation completion or failure cannot be observed through `MultipleRenderedTexturesBooklet`;
- renderer callbacks only if a later acceptance test shows that `MultipleRenderedTexturesBooklet` does not expose enough completion state.

Stable livery, track, cargo, task, car and debt-component representations are now all known. The implementation can proceed without further game-logic decompilation.
