using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
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

        /// <summary>
        /// The game's logger, and the destination <see cref="Game.Diagnostics.SyncLog"/> writes to.
        /// Not a front door: log through SyncLog so the line gets its topic, its switch and its
        /// copy in the flight log.
        /// </summary>
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
        /// The live multiplayer bridge. Created here and pumped each tick by
        /// <see cref="MultiplayerSystem"/>; the settings screen drives it via
        /// host/join/disconnect buttons.
        /// </summary>
        public static MultiplayerService Service;

        /// <summary>
        /// The version this build reports to the log, the flight log and the options screen -
        /// the published one, hotfix suffix and all. What a peer is shown is
        /// <see cref="CompatibilityVersion"/>.
        ///
        /// Read from the informational version, which the build stamps from
        /// Properties/PublishConfiguration.xml (see the csproj): that is the number the store
        /// shows and the one a player quotes in a report. The assembly version is only the
        /// fallback - it stays 1.0.0.0 across releases, so while it was the source every build
        /// called itself the same thing and the handshake's version check could never fire.
        /// </summary>
        internal static string Version => _version ?? (_version = ReadVersion());

        /// <summary>
        /// The version a peer is compared against: the numeric release part of
        /// <see cref="Version"/>, so "0.1.6.1h1" meets "0.1.6.1". A hotfix suffix marks a build
        /// that changed nothing the two machines have to agree on - the wire, the prefabs and the
        /// simulation are the release's - so those two still play together, while a different
        /// release is refused (the host can still admit it: IgnoreModCompatibilityChecks).
        /// </summary>
        internal static string CompatibilityVersion =>
            _compatibilityVersion ?? (_compatibilityVersion = ReleasePart(Version));

        /// <summary>
        /// The stamp a locally built copy carries after the version, and an empty string in a
        /// published one: a released build is the version and nothing else. The number changes
        /// on every local build, so it answers the only question a dev build raises - whether
        /// the game is running what was just compiled. Never on the wire: peers compare
        /// <see cref="CompatibilityVersion"/>, which two dev builds of one release share.
        /// </summary>
        internal static string BuildStamp => _buildStamp ?? (_buildStamp = ReadBuildStamp());

        /// <summary>
        /// What the options screen shows. A released build is the version and nothing else - the
        /// protocol number is a development detail and means nothing to a player. A locally built
        /// copy adds the build stamp and the protocol, which is what a dev build is read for.
        /// </summary>
        internal static string VersionLine =>
            BuildStamp.Length == 0
                ? Version
                : L10n.F(L10n.Key.VersionLineDev, Version, BuildStamp,
                    ProtocolConstants.ProtocolVersion);

        /// <summary>Version and build stamp for the log, which is read without a language.</summary>
        internal static string StampedVersion =>
            BuildStamp.Length == 0 ? Version : Version + " (dev " + BuildStamp + ")";

        /// <summary>Build metadata marker the csproj stamps onto a non-release build.</summary>
        private const string DevMarker = "+dev.";

        private static string _version;
        private static string _compatibilityVersion;
        private static string _buildStamp;

        /// <summary>The leading digits-and-dots of a version, without a trailing dot.</summary>
        private static string ReleasePart(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            int end = 0;
            while (end < version.Length && (char.IsDigit(version[end]) || version[end] == '.')) end++;
            while (end > 0 && version[end - 1] == '.') end--;
            return end > 0 ? version.Substring(0, end) : version;
        }

        private static string ReadVersion()
        {
            // A build that has SourceLink, a revision id or our own dev stamp appends "+<...>";
            // these strings are read and compared, so keep only the part a human calls a version.
            string stamped = ReadInformationalVersion();
            if (stamped.Length > 0)
            {
                int plus = stamped.IndexOf('+');
                string text = plus > 0 ? stamped.Substring(0, plus) : stamped;
                if (text.Length > 0) return text;
            }

            return typeof(Mod).Assembly.GetName().Version.ToString();
        }

        private static string ReadBuildStamp()
        {
            string stamped = ReadInformationalVersion();
            int marker = stamped.IndexOf(DevMarker, StringComparison.Ordinal);
            return marker < 0 ? "" : stamped.Substring(marker + DevMarker.Length);
        }

        private static string ReadInformationalVersion()
        {
            try
            {
                var stamped = (System.Reflection.AssemblyInformationalVersionAttribute)
                    System.Attribute.GetCustomAttribute(typeof(Mod).Assembly,
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return stamped == null || stamped.InformationalVersion == null
                    ? "" : stamped.InformationalVersion;
            }
            catch { return ""; /* the caller falls back to the assembly version */ }
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            // Crash forensics first: the flight log must be recording before anything
            // else of ours can fail (see FlightRecorder).
            FlightRecorder.Start(StampedVersion);

            // Route the sync inbox's rare backpressure/drain warnings through the one logger.
            // They are pipeline faults, so they are never gated by a switch.
            Game.Sync.Infrastructure.SyncInbox.LogWarn =
                delegate(string message) { SyncLog.Warn(LogTopic.Pipeline, message); };

            // Also where the Steam relay backend sits, when this copy of the game has one.
            string modFolder = null;
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                SyncLog.Detail(LogTopic.Startup, "Loaded from " + asset.path + ".");
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

            // Stand up the multiplayer core. The portable half logs through the same logger as
            // the rest of the mod; ColossalModLogger is the seam (see there).
            IModLogger coreLog = ColossalModLogger.Instance;

            // Offer Steam's relay as a hosting backend. Availability is decided here once;
            // when Steam is absent the mod simply keeps to direct connections.
            SteamRelayBootstrap.Register(coreLog, modFolder);

            // Needs the backend above: it is what knows the platform account's name.
            Setting.ApplyPlatformNamePreset();

            Service = new MultiplayerService(coreLog);

            // The sync pipeline asks before it reloads a world. Route that question at the live
            // service, which owns the clock, the in-flight-recovery state and the arbiter.
            Game.Sync.Infrastructure.SyncInbox.Arbitrate = Service.SettleResyncReport;

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
            // Remote milestone popups use the game's native MilestoneReachedEvent so the full
            // vanilla screen and our countdown appear. DevTreeSystem also treats that event as
            // a reward; remove only the points from our marked presentation event immediately.
            updateSystem.UpdateAfter<Game.Sync.Systems.RemoteMilestoneRewardCorrectionSystem,
                global::Game.City.DevTreeSystem>(SystemUpdatePhase.GameSimulation);
            // Service fee accounting has producers on both sides of ServiceFeeSystem: transit and
            // parking arrive before it, utility sales/trade after it. Empty the redundant client
            // queue at both boundaries; the host's absolute collected records are reinstalled by
            // channel 24, so no locally timed event can replace them between snapshots.
            updateSystem.UpdateBefore<Game.Sync.Systems.ServiceFeeIngressBoundarySystem,
                global::Game.Simulation.ServiceFeeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.ServiceFeeEgressBoundarySystem,
                global::Game.Simulation.UtilityFeeSystem>(SystemUpdatePhase.GameSimulation);
            // CityServiceBudgetSystem first reads last frame's collected fee/upkeep records and
            // then rebuilds those records from local buildings, networks, upgrades and usage.
            // Install the host input before that read and restore the host output immediately
            // after it, while also pinning the fee/upkeep income and expense array slots.
            updateSystem.UpdateBefore<Game.Sync.Systems.ServiceAccountingInputSystem,
                global::Game.Simulation.CityServiceBudgetSystem>(
                SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAfter<Game.Sync.Systems.ServiceAccountingCorrectionSystem,
                global::Game.Simulation.CityServiceBudgetSystem>(
                SystemUpdatePhase.ModificationEnd);
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
            // Income is the one household scalar whose readers run before that boundary: the
            // wealth component of citizen wellbeing is evaluated a few systems after the pass that
            // recomputes income from this peer's own employment graph, and the building's
            // good-wealth prop requirement is read later still. Restore the host value here, in
            // between, or both follow the client's local employment instead of the host's.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdIncomeBoundarySystem,
                global::Game.Simulation.HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);
            // ResourceBuyerSystem runs real shoppers and SaleEvents after the earlier household
            // boundary. Keep those agents alive, then correct the money and shopped-value result
            // to the host snapshot at the first safe point after the sale is booked.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdPurchaseCorrectionSystem,
                global::Game.Simulation.ResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
            // ResidentsSection derives average fees directly from fulfilled building utility
            // quantities. Correct those fields after the exact native systems that rewrite them;
            // wanted demand, graph connectivity and warning state remain locally simulated.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialElectricityFeeCorrectionSystem,
                global::Game.Simulation.DispatchElectricitySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialWaterFeeCorrectionSystem,
                global::Game.Simulation.DispatchWaterSystem>(SystemUpdatePhase.GameSimulation);
            // Strip the client's own company closure/seeking proposals at the last point before
            // anything acts on them. The systems that make those proposals stay running because
            // they also produce the figures and demand the rest of the simulation reads.
            updateSystem.UpdateBefore<Game.Sync.Systems.CompanyLifecycleBoundarySystem,
                global::Game.Simulation.CompanyMoveAwaySystem>(SystemUpdatePhase.GameSimulation);
            // The host captures company bookkeeping at its native cadence. Clients hold the
            // accounting calculator and consume host figures; keep this partition schedule for
            // capture and for repairing fields also touched by other local company systems.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyStatsSyncSystem,
                global::Game.Simulation.CompanyEconomyStatisticSystem>(
                SystemUpdatePhase.GameSimulation);
            // Company pages can otherwise sit cached until a business's 2,048-frame accounting
            // partition returns. Apply deep state on FindJobSystem's 16-frame cadence: this also
            // repairs the real Employee/Worker graph after local job matching tries to diverge.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyStateBoundarySystem,
                global::Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            // The building panel recalculates Production from the property's efficiency factors on
            // every UI frame, and these two native passes rewrite those factors from state that is
            // local by construction - goods on hand plus a rounding draw for processing, area
            // depletion for extraction. Each correction runs directly after its own writer, at that
            // writer's interval and therefore its update offset, so the panel never reads the local
            // result. The two passes are separate registrations with separate offsets, which is why
            // this is two systems and not one.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyProcessingBoundarySystem,
                global::Game.Simulation.ProcessingCompanySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyExtractorBoundarySystem,
                global::Game.Simulation.ExtractorCompanySystem>(SystemUpdatePhase.GameSimulation);
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
            // HouseholdCitizen is the authoritative inner roster. Birth, individual death and a
            // household split mutate that buffer without emitting RentersUpdated because the
            // family can stay in the same property. Observe it after native citizen initialization
            // so a newborn is complete before the host prioritizes its building; later writers are
            // still caught from their changed version on the following frame.
            updateSystem.UpdateAfter<
                Game.Sync.Systems.ResidentialHouseholdLifecycleObservationSystem,
                global::Game.Citizens.CitizenInitializeSystem>(
                SystemUpdatePhase.GameSimulation);
            // Also UIUpdate: publishing the local camera focus must keep going while a
            // player is paused (so partners still see where they are), and GameSimulation
            // barely ticked it - the live log showed ~1 position sent per 30 s.
            updateSystem.UpdateAt<Game.Sync.Players.PlayerCursorSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Raycast phase, after the tool's own input: the only point where an extra raycast
            // input still joins this frame's job. Without it the default tool's narrow search
            // leaves every road, track and pipe out of what a partner is shown pointing at.
            updateSystem.UpdateAfter<Game.Sync.Players.PlayerHoverRaycastSystem,
                global::Game.Tools.ToolRaycastSystem>(SystemUpdatePhase.Raycast);
            // Renders the other players' camera positions as ground rings. Rendering phase
            // so the markers draw every frame, in every state (including paused).
            updateSystem.UpdateAt<Game.Sync.Players.RemotePlayerMarkerSystem>(SystemUpdatePhase.Rendering);
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
            // Edge-only refreshes must include their junctions before native network processing.
            updateSystem.UpdateBefore<Game.Sync.Systems.Net.NetJunctionRefreshSystem,
                global::Game.Net.ReferencesSystem>(SystemUpdatePhase.Modification2B);
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
            // ModificationEnd, with the rest of the capture systems: another mod's tool has applied
            // by this point in the frame, and the engine's chunk-change record still says which of
            // its types were written to. It also keeps running while the game is paused, and these
            // mods are used on a paused city as much as a running one.
            updateSystem.UpdateAt<Game.Sync.Systems.Mods.ModStateSyncSystem>(
                SystemUpdatePhase.ModificationEnd);
            // ModificationEnd, after the game's event initialization at Modification2: that pass is
            // what turns a bare disaster event into a placed one (position, radius, duration), and
            // the Created tag it keys on is gone by the next frame. Capturing here reads the
            // resolved disaster, not an empty shell.
            updateSystem.UpdateAt<Game.Sync.Systems.DisasterSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // ModificationEnd, next to disasters: an ignite request only becomes a placed
            // event once the game's own event pass has run, and its Created tag is gone by
            // the next frame. Fires have no disaster-style local suppression - every
            // machine rolls its own ignitions and reports them, both cities converging on
            // the union - so this detector stays on for every role.
            updateSystem.UpdateAt<Game.Sync.Systems.FireSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // ModificationEnd, next to fire sync: marker transitions are detected on a
            // rolling UpdateFrame scan rather than on tool tags, so any phase with live
            // entities would do - sharing the event-sync slot keeps the ordering obvious.
            // Only non-spawnables are watched here; growables stay with their lifecycle
            // system and removals stay with delete sync.
            updateSystem.UpdateAt<Game.Sync.Systems.ServiceBuildingStateSyncSystem>(SystemUpdatePhase.ModificationEnd);
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

            // One line, at the end, rather than a "ready" line per registered system: the thirty
            // of those said nothing a reader could act on, and the only question they answered -
            // "did the mod actually come up?" - is answered better here, with the numbers that
            // decide whether two players can even play together.
            SyncLog.Event(LogTopic.Startup, "Loaded: mod v" + StampedVersion + ", protocol v" +
                ProtocolConstants.ProtocolVersion + ", game v" + UnityEngine.Application.version +
                ", sync systems registered, verbose logging " +
                (Setting != null && Setting.VerboseLogging ? "on" : "off") + ".");
        }

        public void OnDispose()
        {
            SyncLog.Event(LogTopic.Startup, "Unloading.");

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
