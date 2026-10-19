// Where each file under Multiplayer/ goes in the client/server layout (docs/design/code-layout.md).
// `node Tools/CodeLayout/layout.js` writes Tools/CodeLayout/moves.tsv (old path, new path, role,
// and "split" for files that refer to both roles). Every .cs file must match a rule, or it stops.
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '../../Multiplayer');

// [old path (regex on the path under Multiplayer/), new folder under Multiplayer/]. First match wins.
const rules = [
    // The mod: entry, settings, locale (root namespace `Multiplayer`) and the front end.
    [/^(Multiplayer|Settings|Locale)\.cs$/, ''],
    [/^Components\/MainMenu\//, 'Menus/MainMenu'],
    [/^Components\/UI\/GridView\//, 'Menus/Controls'],
    [/^Components\/Util\/HyperlinkHandler\.cs$/, 'Menus/Controls'],
    [/^Components\/UI\/ServerBrowser\//, 'Menus/ServerBrowser'],
    [/^Networking\/Managers\/Client\/ServerBrowserClient\.cs$/, 'Menus/ServerBrowser'],
    // Not Menus/Settings: a namespace of that name would hide the Settings class in the menus.
    [/^Components\/UI\/Settings\//, 'Menus/Controls'],
    [/^Patches\/MainMenu\/LauncherControllerSaveLockPatch\.cs$/, 'Server/Dedicated'],
    [/^Patches\/MainMenu\/SceneSwitcherPatch\.cs$/, 'Shared/Session'],
    [/^Patches\/MainMenu\//, 'Menus/MainMenu'],

    // MPAPI providers.
    [/^API\/(ServerAPIProvider|ServerPlayerWrapper)\.cs$/, 'Server/Api'],
    [/^API\/(ClientAPIProvider|ClientPlayerWrapper)\.cs$/, 'Client/Api'],
    [/^API\//, 'Shared/Api'],

    // Protocol and transport.
    [/^Networking\/Packets\/(.*\/)?/, m => ('Shared/Protocol/Packets/' + (m[1] || '')).replace(/\/$/, '')],
    [/^Networking\/Data\/ServerPlayer\.cs$/, 'Server/Session'],
    [/^Networking\/Data\/PlayerIdentity\.cs$/, 'Shared/Session'],
    [/^Networking\/Data\/(.*\/)?/, m => ('Shared/Protocol/Data/' + (m[1] || '')).replace(/\/$/, '')],
    [/^Networking\/Serialization\//, 'Shared/Protocol/Serialization'],
    [/^Networking\/TransportLayers\//, 'Shared/Transport'],
    [/^Patches\/Util\//, 'Shared/Transport'],

    // Session: connection, login, loading, the lifecycle.
    [/^Networking\/Managers\/NetworkManager\.cs$/, 'Shared/Session'],
    [/^Components\/Networking\/(NetworkLifecycle|NetworkRole|TickedQueue)\.cs$/, 'Shared/Session'],
    [/^Components\/IdMonoBehaviour\.cs$/, 'Shared/Session'],
    [/^Networking\/Managers\/Server\/(NetworkServer|PlayerAuthentication|LobbyServerManager)\.cs$/, 'Server/Session'],
    [/^Networking\/Managers\/Client\/(NetworkClient|HostOwnClient)\.cs$/, 'Client/Session'],
    [/^Components\/Networking\/RpcManager\.cs$/, 'Client/Session'],
    [/^Components\/SaveGame\/(Client_GameSession|StartGameData_ServerSave)\.cs$/, 'Client/Session'],
    [/^Patches\/World\/(AppUtilPatch|GraphicsOptionsPatch)\.cs$/, 'Shared/Session'],
    [/^Patches\/World\/DisplayLoadingInfoPatch\.cs$/, 'Client/Session'],

    // Players.
    [/^Networking\/Managers\/Server\/CullingManager\.cs$/, 'Server/Players'],
    [/^Networking\/Managers\/Client\/ClientPlayerManager\.cs$/, 'Client/Players'],
    [/^Components\/Networking\/Player\/(NetworkedPlayer|NetworkedPlayer\.VrItems|NetworkedPlayerIKHandler)\.cs$/, 'Shared/Players'],
    [/^Components\/Networking\/Player\//, 'Client/Players'],
    [/^Models\//, 'Shared/Players'],
    [/^Patches\/Player\//, 'Client/Players'],

    // Chat and admin commands.
    [/^Networking\/Managers\/Server\/(ChatManager|AdminItemGrants)\.cs$/, 'Server/Chat'],

    // The dedicated host and the world kept loaded around players.
    [/^Components\/Networking\/DedicatedHost(Anchor|SaveLock)?\.cs$/, 'Server/Dedicated'],
    [/^Components\/Networking\/Server(Scenery|Terrain|World)Coverage\.cs$/, 'Server/Coverage'],
    [/^Patches\/World\/(StreamerPatch|TerrainGridPatch|PlayerDistanceGameObjectsDisablerPatch)\.cs$/, 'Server/Coverage'],
    [/^Patches\/Train\/(DistanceLodSystemPatch|DerailedTrainCarIsKinematicHandlerPatch)\.cs$/, 'Server/Coverage'],

    // Trains.
    [/^Components\/TrainComponentLookup\.cs$/, 'Shared/Trains'],
    [/^Components\/Networking\/Train\/(TrainTopologyAuthority|NetworkedLocoRestorationController)\.cs$/, 'Server/Trains'],
    [/^Components\/Networking\/Train\/(TrainPortPresenter|TrainTopologyReconciler|NetworkedCarSpawner)\.cs$/, 'Client/Trains'],
    [/^Components\/Networking\/Train\//, 'Shared/Trains'],
    [/^Patches\/CommsRadio\//, 'Client/Trains'],
    [/^Patches\/Train\/(CarSpawnerPatch|CarVisitCheckerPatch|DerailCausePatch|GarageSpawnerPatch|HandcarDrivePatch|HoseSeparationCheckerPatch|ScriptStripperRuntimePatch|TrainCarPatch|UnusedTrainCarDeleterPatch|WindowsBreakingControllerPatch|BogiePatch)\.cs$/, 'Server/Trains'],
    [/^Patches\/Train\/(CoalPileTransferCoalPatch|CouplerBreakPatch|CouplerChainInteractionPatch|CouplerPatch|DecouplerDeviceLogicPatch|FireboxTransferCoalPatch|HoseAndCockPatch|LocoRestorationControllerPatch|MultipleUnitCablePatch|UICouplingHelperPatch|CarRollingAudioModulePatch)\.cs$/, 'Client/Trains'],
    [/^Patches\/Train\/(FastTravelUIControllerPatch|QuickTutorialInitiatorPatch)\.cs$/, 'Client/UI'],
    [/^Patches\/Train\//, 'Shared/Trains'],

    // Items, containers, Lost and Found, world items.
    [/^Components\/Networking\/World\/(AuthoritativeItemRegistry|ItemChangeLog|NetworkedLostAndFoundManager|OwnedItemsSummary)\.cs$/, 'Server/Items'],
    [/^Components\/Networking\/World\/Containers\/NetworkedColdContainerManager\.cs$/, 'Server/Items'],
    [/^Components\/Networking\/World\/WorldItems\/(AuthoredWorldItemReplenishmentManager|WorldItemPersistence(Manager|Restore|Save))\.cs$/, 'Server/Items'],
    [/^Components\/Networking\/World\/(NetworkedItem|NetworkedItemManager)(\.[A-Za-z]+)?\.cs$/, 'Shared/Items'],
    [/^Components\/Networking\/World\/(TemporaryClientAdoptionCompatibility|NetworkedPageBookState)\.cs$/, 'Shared/Items'],
    [/^Components\/Networking\/World\/(Containers|WorldItems)\//, 'Shared/Items'],
    [/^Integrations\/Inventory\/ColdContainerUiIntegration\.cs$/, 'Client/Items'],
    [/^Integrations\/(Inventory|Storage)\//, 'Shared/Items'],
    [/^Patches\/World\/Items\/(MoneyPatch|RemoteControllerModulePatch|ColdContainerUiPatch|InventoryOwnershipUiPatch)\.cs$/, 'Client/Items'],
    [/^Patches\/World\/Items\/(GrabberRecoveryPatch|InventoryMembershipPatch)\.cs$/, 'Server/Items'],
    [/^Patches\/World\/Items\//, 'Shared/Items'],
    [/^Patches\/World\/StorageControllerPatch\.cs$/, 'Shared/Items'],

    // World: junctions, turntables, switches, time, hazmat, shops.
    [/^Components\/Networking\/World\/(NetworkedJunction|NetworkedTurntable|NetworkedRailTrack|NetworkedGenericSwitch|HazmatSync)\.cs$/, 'Shared/World'],
    [/^Patches\/World\/(JunctionPatch|TurntableControllerPatch|RailTrackPatch|GenericSwitchPatch|PropHosePatch)\.cs$/, 'Shared/World'],
    [/^Patches\/World\/(TimeAdvancePatch|HazmatTileManagerPatch|BedSleepingControllerPatch)\.cs$/, 'Client/World'],
    [/^Patches\/World\/(GameParamsPatch|KeyUsePatch|GlobalShopControllerPatch)\.cs$/, 'Server/World'],

    // Stations: pit stops, plugs, cash registers.
    [/^Components\/Networking\/World\/(NetworkedPitStopStation|NetworkedPluggableObject|NetworkedCashRegisterWithModules)\.cs$/, 'Shared/Stations'],
    [/^Patches\/World\/PluggableObjectPatch\.cs$/, 'Shared/Stations'],
    [/^Patches\/World\/CashRegisterBasePatch\.cs$/, 'Client/Stations'],
    [/^Patches\/World\/CashRegisterWithModulesPatch\.cs$/, 'Server/Stations'],
    [/^Patches\/World\/StationLocoSpawnerPatch\.cs$/, 'Server/Trains'],

    // Jobs.
    [/^Components\/Networking\/Jobs\/JobBookletSummonCoordinator\.cs$/, 'Server/Jobs'],
    [/^Components\/Networking\/Jobs\//, 'Shared/Jobs'],
    [/^Components\/Networking\/World\/NetworkedStationController\.cs$/, 'Shared/Jobs'],
    [/^Patches\/Jobs\/(BookletCreatorPatch|StationControllerPatch|StationJobGenerationRangePatch|StationPatch|StationProceduralJobsControllerPatch|TaskPatch)\.cs$/, 'Server/Jobs'],
    [/^Patches\/Jobs\/(JobValidatorPatch|WarehouseMachineControllerPatch)\.cs$/, 'Client/Jobs'],
    [/^Patches\/Jobs\//, 'Shared/Jobs'],

    // Career: saves, per-player state, money, licences, fees, debt.
    [/^Components\/SaveGame\//, 'Server/Career'],
    [/^Networking\/Managers\/Server\/FeePayment\.cs$/, 'Server/Career'],
    [/^Components\/UI\/CareerManager\//, 'Client/Career'],
    [/^Patches\/SaveGame\/(CareerManagerDebtControllerPatch|CareerManagerFeesScreenPatch|CareerManagerLicensePayingScreenPatch|CareerManagerMainScreenExtensionPatch)\.cs$/, 'Client/Career'],
    [/^Patches\/SaveGame\/(SaveLoadControllerPatch|SimulatedCarDebtTrackerPatch|StartGameData_FromSaveGamePatch)\.cs$/, 'Server/Career'],
    [/^Patches\/SaveGame\//, 'Shared/Career'],
    [/^Patches\/World\/(SaveGameManagerPatch|CarsSaveManagerPatch)\.cs$/, 'Server/Career'],

    // UI on a player's game.
    [/^Components\/Networking\/UI\//, 'Client/UI'],
    [/^Patches\/PauseMenu\//, 'Client/UI'],

    // Everything else that every game needs.
    [/^Debugging\//, 'Shared/Debugging'],
    [/^Utils\//, 'Shared/Utils'],
    [/^ModCompatibility\//, 'Shared/Compatibility'],
    [/^Integrations\/Seasons\//, 'Shared/Integrations'],
    [/^Patches\/Mods\//, 'Shared/Integrations'],
];

function walk(dir) {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory())
            return ['bin', 'obj'].includes(entry.name) ? [] : walk(full);
        return entry.name.endsWith('.cs') ? [path.relative(root, full).split(path.sep).join('/')] : [];
    });
}

const serverRef = /Instance\.Server\b|Instance\.Server[?.]|\bServer_[A-Z]|\bServerPlayer\b|\bNetworkServer\b/;
const clientRef = /Instance\.Client\b|Instance\.Client[?.]|\bClient_[A-Z]|\bNetworkClient\b|\bClientPlayerManager\b/;

const rows = [];
const unmatched = [];
for (const file of walk(root).sort()) {
    const rule = rules.find(([pattern]) => pattern.test(file));
    if (!rule) { unmatched.push(file); continue; }
    const [pattern, target] = rule;
    const folder = typeof target === 'function' ? target(file.match(pattern)) : target;
    const moved = (folder ? folder + '/' : '') + path.basename(file);
    const role = folder.split('/')[0] || 'Mod';
    const text = fs.readFileSync(path.join(root, file), 'utf8');
    const split = serverRef.test(text) && clientRef.test(text) ? 'split' : '';
    rows.push([file, moved, role === 'Menus' ? 'Mod' : role, split]);
}
if (unmatched.length) {
    console.error('No rule for:\n  ' + unmatched.join('\n  '));
    process.exit(1);
}
const collisions = rows.map(r => r[1]).filter((p, i, all) => all.indexOf(p) !== i);
if (collisions.length) {
    console.error('Two files would land on:\n  ' + [...new Set(collisions)].join('\n  '));
    process.exit(1);
}
fs.writeFileSync(path.join(__dirname, 'moves.tsv'),
    '# old path\tnew path\trole\tsplit (refers to both roles)\n' + rows.map(r => r.join('\t')).join('\n') + '\n');

// The same, grouped by new folder, for reading.
const byFolder = {};
for (const [from, to, , split] of rows) {
    const folder = path.posix.dirname(to);
    (byFolder[folder === '.' ? '(root)' : folder] ??= []).push(
        `- \`${path.posix.basename(to)}\` from \`${path.posix.dirname(from) === '.' ? '(root)' : path.posix.dirname(from)}/\`${split ? ' **(split)**' : ''}`);
}
const md = ['# Where each file goes', '',
    'Generated by `layout.js` from the rules in it; see docs/design/code-layout.md. **(split)** marks a',
    'file that refers to both the server and the client: it goes where it does until it is split.', ''];
for (const folder of Object.keys(byFolder).sort())
    md.push(`## ${folder} (${byFolder[folder].length})`, '', ...byFolder[folder], '');
fs.writeFileSync(path.join(__dirname, 'moves.md'), md.join('\n'));
const count = {};
for (const r of rows) count[r[2]] = (count[r[2]] || 0) + 1;
console.log(`${rows.length} files:`, count, `${rows.filter(r => r[3]).length} to split`);
