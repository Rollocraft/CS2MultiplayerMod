using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CS2MultiplayerMod.Game;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CS2MultiplayerMod
{
    public class Mod : IMod
    {
        public const string Name = "CS2MultiplayerMod";

        public static ILog log = LogManager.GetLogger(Name).SetShowsErrorsInUI(false);

        public static Setting Setting;

        /// <summary>
        /// Game locale ID -> the <c>locales/&lt;lang&gt;.properties</c> file backing it.
        /// The IDs are the ones the game ships its own dictionaries under, so they must
        /// match exactly (Simplified Chinese is <c>zh-HANS</c>, not <c>zh-CN</c>).
        /// </summary>
        private static readonly KeyValuePair<string, string>[] LocaleSources =
        {
            new KeyValuePair<string, string>("en-US", "en"),
            new KeyValuePair<string, string>("de-DE", "de"),
            new KeyValuePair<string, string>("fr-FR", "fr"),
            new KeyValuePair<string, string>("es-ES", "es"),
            new KeyValuePair<string, string>("it-IT", "it"),
            new KeyValuePair<string, string>("pl-PL", "pl"),
            new KeyValuePair<string, string>("ru-RU", "ru"),
            new KeyValuePair<string, string>("ja-JP", "ja"),
            new KeyValuePair<string, string>("zh-HANS", "zh-HANS"),
        };

        /// <summary>
        /// Log a chatty, troubleshooting-only line - the per-action sync notices and the
        /// periodic diagnostics. Silent unless "Verbose Logging" is enabled in settings, so
        /// the default log stays quiet and only the important lifecycle/fault lines remain.
        /// </summary>
        public static void Verbose(string message)
        {
            if (VerboseEnabled) log.Info(message);
        }

        /// <summary>
        /// Whether anything would come of a <see cref="Verbose"/> call. Ask before *computing* a
        /// diagnostic, not just before logging one: a counter nobody reads must not cost a frame.
        /// </summary>
        public static bool VerboseEnabled => Setting != null && Setting.VerboseLogging;

        /// <summary>
        /// The live multiplayer bridge. Created here and pumped each tick by
        /// <see cref="MultiplayerSystem"/>; the settings screen drives it via
        /// host/join/disconnect buttons.
        /// </summary>
        public static MultiplayerService Service;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            // Crash forensics first: the flight log must be recording before anything
            // else of ours can fail (see FlightRecorder).
            FlightRecorder.Start(typeof(Mod).Assembly.GetName().Version.ToString());

            // Route the sync inbox's rare backpressure/drain warnings to the mod log.
            Game.Sync.Infrastructure.SyncInbox.LogWarn = log.Warn;

            // Also where the Steam relay backend sits, when this copy of the game has one.
            string modFolder = null;
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {Game.Diagnostics.LogPaths.Redact(asset.path)}");
                modFolder = System.IO.Path.GetDirectoryName(asset.path);
            }

            // Register settings and the locale sources backing them (and all runtime
            // strings). The game picks the source matching the language the player
            // set in the options — no mod-specific language setting, like vanilla.
            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            // Each language is one embedded locales/<lang>.properties file, keyed by the
            // locale ID the game itself uses. Key parity across those files is enforced by
            // CI (.github/workflows/locale.yml), not at runtime; a language the game does
            // not offer simply never gets asked for, and unlisted ones fall back to English.
            foreach (var locale in LocaleSources)
                GameManager.instance.localizationManager.AddSource(
                    locale.Key, new PropertiesLocaleSource(Setting, locale.Value));

            // Persist / load settings to the standard mod settings store.
            AssetDatabase.global.LoadSettings(Name, Setting, new Setting(this));

            // Stand up the multiplayer core (portable session + game logger adapter) and
            // register the ECS system that pumps it once per simulation tick.
            var coreLog = new ColossalModLogger(log);

            // Offer Steam's relay as a hosting backend. Availability is decided here once;
            // when Steam is absent the mod simply keeps to direct connections.
            SteamRelayBootstrap.Register(coreLog, modFolder);

            // Needs the backend above: it is what knows the platform account's name.
            Setting.ApplyPlatformNamePreset();

            Service = new MultiplayerService(coreLog);

            // The sync pipeline asks before it reloads a world. Route that question at the live
            // service, which owns the clock, the in-flight-recovery state and the arbiter.
            Game.Sync.Infrastructure.SyncInbox.Arbitrate = Service.SettleResyncReport;

            FlightRecorder.Note("startup-stage service-created");
            log.Info("Multiplayer core initialised. Protocol v" +
                     CS2MultiplayerMod.Core.Protocol.ProtocolConstants.ProtocolVersion +
                     ". Registering sync systems...");

            // UIUpdate, not GameSimulation: the session pump must also run in the main
            // menu (joining from there) and while the game is paused - the options
            // screen pauses the simulation, which previously froze all connection
            // handling exactly while the player was looking at the connect buttons.
            updateSystem.UpdateAt<MultiplayerSystem>(SystemUpdatePhase.UIUpdate);
            // Bindings for the main-menu multiplayer screen (UI module in UI/).
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);
            // UIUpdate, not GameSimulation: the GameSimulation phase stops ticking the
            // moment the game is paused (selectedSpeed 0), so a system there can never
            // observe a pause to replicate it, nor apply a remote pause once stopped -
            // pause/play and speed changes never synced. UIUpdate runs every frame in
            // every state, so the simulation-speed channel (and the rest of the city
            // state) now stays in sync even while a player is paused. Channel capture is
            // gated to ~1 Hz internally, so the render-rate phase adds no extra traffic.
            updateSystem.UpdateAt<Game.Sync.Systems.CityStateSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Capture the host's short-lived MovingAway decision immediately before its native
            // consumer. Register this proxy exactly once: ordering registrations are additive.
            updateSystem.UpdateBefore<
                Game.Sync.Systems.ResidentialOccupancyDepartureCaptureSystem,
                global::Game.Simulation.HouseholdMoveAwaySystem>(
                SystemUpdatePhase.GameSimulation);
            // Preserve the exact household contracts installed with the downloaded world before
            // the client's first native rent calculation can replace them. This seed is a one-shot
            // per world; the identity-aware correction itself runs at the boundary below.
            updateSystem.UpdateBefore<Game.Sync.Systems.ResidentialOccupancyRentSeedSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // RentAdjustSystem writes one of sixteen property buckets. Insert the host correction
            // directly after RentAdjust; the game's existing phase order also leaves it before
            // PropertyRenterSystem, whose later payment pass consumes the corrected value.
            // PropertyRentSyncSystem has the same 1024-frame interval, so this does not scan the
            // whole cache every simulation tick.
            updateSystem.UpdateAfter<Game.Sync.Systems.PropertyRentSyncSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // Household economy is updated in household-specific partitions, independently from
            // the building partition used by occupancy reconciliation. Correct changed households
            // after the final daily-economy writers so every family in a multi-unit building uses
            // the same authoritative snapshot when the residents panel calculates its averages.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdEconomyCorrectionSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // Strip the client's own company closure/seeking proposals at the last point before
            // anything acts on them. The systems that make those proposals stay running because
            // they also produce the figures and demand the rest of the simulation reads.
            updateSystem.UpdateBefore<Game.Sync.Systems.CompanyLifecycleBoundarySystem,
                global::Game.Simulation.CompanyMoveAwaySystem>(SystemUpdatePhase.GameSimulation);
            // Directly after the game's own company bookkeeping, at that system's own interval and
            // over that system's own UpdateFrame partition. This ordering IS the feature: an
            // earlier attempt corrected on a 1024-frame rotation while CompanyEconomyStatisticSystem
            // rewrites the same fields every 128 frames, so every correction was overwritten
            // several times over before the next one arrived and the panels never settled.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyStatsSyncSystem,
                global::Game.Simulation.CompanyEconomyStatisticSystem>(
                SystemUpdatePhase.GameSimulation);
            // Before PropertyProcessingSystem: that system drains the rent-action queue this one
            // fills. The queue is persistent, so an action always survives to the next drain; the
            // ordering is what lets a move-in land in the same tick it was decided in whenever the
            // two updates coincide. Their intervals differ, so the game assigns them independent
            // offsets and that is not every time - worst case the move-in waits a few frames.
            updateSystem.UpdateBefore<Game.Sync.Systems.ResidentialOccupancySyncSystem,
                global::Game.Simulation.PropertyProcessingSystem>(SystemUpdatePhase.GameSimulation);
            // Complete queued household move-ins immediately after the native transaction has
            // established both renter links. PropertyRenterSystem is the next native stage, so a
            // single registration puts finalization before its later payment pass without running
            // the same managed system twice in one simulation phase.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialOccupancyFinalizeSystem,
                global::Game.Simulation.PropertyProcessingSystem>(SystemUpdatePhase.GameSimulation);
            // Also UIUpdate: publishing the local camera focus must keep going while a
            // player is paused (so partners still see where they are), and GameSimulation
            // barely ticked it - the live log showed ~1 position sent per 30 s.
            updateSystem.UpdateAt<Game.Sync.Players.PlayerCursorSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Renders the other players' camera positions as ground rings. Rendering phase
            // so the markers draw every frame, in every state (including paused).
            updateSystem.UpdateAt<Game.Sync.Players.RemotePlayerMarkerSystem>(SystemUpdatePhase.Rendering);
            // Draws incoming map pings, and receives them - the beacon is a command, so the
            // observer has to be attached even in the frames where nothing is on screen.
            // Rendering phase for the same reason as the markers above: pings must appear
            // while the game is paused, which is exactly when players stop to point at things.
            updateSystem.UpdateAt<Game.Sync.Players.MapPingSystem>(SystemUpdatePhase.Rendering);
            // UIUpdate, not GameSimulation: policies can be toggled while the game is paused
            // (the policies panel works paused - the game routes the change through an event
            // entity consumed by the every-frame modification pipeline), but the GameSimulation
            // phase stops ticking at speed 0. A detector there never saw a change made while
            // paused and never applied an incoming one until unpause. The content scan is
            // 1 Hz-gated internally, so the render-rate phase adds no extra cost.
            updateSystem.UpdateAt<Game.Sync.Systems.PolicySyncSystem>(SystemUpdatePhase.UIUpdate);
            // Placement capture runs at ModificationEnd, where the one-frame Created tags
            // from a tool apply are still alive (they are gone by GameSimulation).
            updateSystem.UpdateAt<Game.Sync.Systems.BuildSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.Net.NetSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.DeleteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // After DeleteSyncSystem, which collects this frame's tool-originated removals first:
            // a bulldozed zoned building is a player action and already travels as a delete, so
            // GrowableSync has to be able to tell it apart from the simulation retiring one.
            updateSystem.UpdateAt<Game.Sync.Systems.GrowableSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // In-place road-type replacement (a different net prefab drawn over an existing edge):
            // detected as an Updated-not-Created edge whose PrefabRef changed - see NetReplaceSyncSystem.
            updateSystem.UpdateAt<Game.Sync.Systems.NetReplaceSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.ZoneSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.TerrainSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.UpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.MoveSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.NetUpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.AreaSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.RouteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.TilePurchaseSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // ModificationEnd, after the game's event initialization at Modification2: that pass is
            // what turns a bare disaster event into a placed one (position, radius, duration), and
            // the Created tag it keys on is gone by the next frame. Capturing here reads the
            // resolved disaster, not an empty shell.
            updateSystem.UpdateAt<Game.Sync.Systems.DisasterSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // After the game's own auto-name initialization, which runs late in ModificationEnd and
            // is what fills in a new street's or district's name draw. Capturing before it would
            // read the draw one frame stale. ModificationEnd also keeps working while the game is
            // paused (unlike GameSimulation), so a rename made in a paused city still replicates,
            // and the one-frame Created/Updated tags the auto-name capture keys on are alive here.
            updateSystem.UpdateAfter<Game.Sync.Systems.NameSyncSystem,
                global::Game.Common.RandomLocalizationInitializeSystem>(SystemUpdatePhase.ModificationEnd);
            // UIUpdate, NOT GameSimulation: dev-tree nodes can be purchased while the game
            // is paused (the progression panel works paused, and a node's Locked clears
            // outside the simulation loop), but GameSimulation freezes at selectedSpeed 0.
            // A detector there never saw a purchase made while paused and never applied an
            // incoming one - yet the authoritative DevTreePoints snapshot keeps flowing from
            // CityStateSyncSystem (also UIUpdate) the whole time, refilling the buyer's spent
            // points every second. The result was a client with effectively infinite points
            // and a host that never learned which node was bought. Running here, alongside
            // that points channel, the local spend and the host's deduction keep pace whether
            // the game is paused or not.
            updateSystem.UpdateAt<Game.Sync.Systems.DevTreeSyncSystem>(SystemUpdatePhase.UIUpdate);
            // The visual menu mutates render/building state directly and is usable while paused.
            // Observe it after SelectedInfoUISystem so the resulting state is captured, not UI intent.
            updateSystem.UpdateAfter<Game.Sync.Systems.VisualCustomizationSyncSystem,
                global::Game.UI.InGame.SelectedInfoUISystem>(SystemUpdatePhase.UIUpdate);
            // Realization must run at ToolUpdate: definition entities are consumed at
            // Modification1 and their Updated tag is stripped at Cleanup, so a definition
            // spawned at ModificationEnd is never realized (see SyncRealizeSystem).
            // Repair stranded movers at the front of ToolUpdate, before the default tool can
            // hover/select an invalid legacy instance. The sweep is one-shot per world load and
            // internally frame-budgeted for large cities.
            updateSystem.UpdateBefore<Game.Sync.Systems.WorldRepairSystem>(
                SystemUpdatePhase.ToolUpdate);
            // Complete remote terrain GPU readback at the very start of ToolUpdate, before a local
            // road/object tool can generate a preview from stale CPU heights.
            updateSystem.UpdateBefore<Game.Sync.Systems.TerrainReadbackBarrierSystem>(
                SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Game.Sync.Systems.SyncRealizeSystem>(SystemUpdatePhase.ToolUpdate);
            // Capture one-frame object lifecycle applies after the active object/upgrade tool made
            // its decision but before ToolOutputSystem consumes the complete standing definition
            // graph. This is the only frame that serializes the graph; hover previews stay cheap.
            updateSystem.UpdateBefore<Game.Sync.Systems.ObjectToolApplyCaptureSystem,
                global::Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
            // After ToolOutputBarrier: tools record their definitions through that end-of-phase
            // buffer, so this is the first (and only) slot where they exist as entities but have
            // not been consumed - the gate keeps them out of an armed net commit (see there).
            updateSystem.UpdateAfter<Game.Sync.Systems.DefinitionGateSystem, global::Game.Tools.ToolOutputBarrier>(
                SystemUpdatePhase.ToolUpdate);
            // Immediately before the game's owner resolution: a generated sub-element's owner
            // description is removed by that pass whether or not it resolved, so this is the only
            // slot where an unresolved sub-element can still be traced to its owner.
            updateSystem.UpdateBefore<Game.Sync.Systems.OwnerDefinitionSnapshotSystem,
                global::Game.Tools.FindOwnersSystem2>(SystemUpdatePhase.Modification2B);
            // UIUpdate, not GameSimulation, for the same reason as the session pump:
            // hosting starts from the options screen, which pauses the simulation -
            // at GameSimulation the queued initial world stream for a joining client
            // was never processed while the host sat in the (paused) menu, leaving
            // the client stuck in WaitingForMap forever.
            updateSystem.UpdateAt<Game.Sync.Systems.WorldResyncSystem>(SystemUpdatePhase.UIUpdate);
            FlightRecorder.Note("startup-complete systems-registered");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

            Game.Sync.Infrastructure.SyncInbox.Arbitrate = null;
            ResyncArbiter.Reset();

            if (Service != null)
            {
                Service.Shutdown();
                Service = null;
            }

            if (Setting != null)
            {
                Setting.UnregisterInOptionsUI();
                Setting = null;
            }

            FlightRecorder.Stop();
        }
    }
}
