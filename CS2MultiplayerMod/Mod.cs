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

        /// <summary>The game logger behind <see cref="Game.Diagnostics.SyncLog"/>; log through SyncLog.</summary>
        public static ILog log = LogManager.GetLogger(Name).SetShowsErrorsInUI(false);

        public static Setting Setting;

        /// <summary>
        /// Game locale id -> <c>locales/&lt;lang&gt;.properties</c>. The ids must match the game's exactly
        /// (Simplified Chinese is <c>zh-HANS</c>).
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

        /// <summary>The live multiplayer bridge, pumped by <see cref="MultiplayerSystem"/>.</summary>
        public static MultiplayerService Service;

        /// <summary>
        /// The published version, hotfix suffix included, from the informational version stamped out of
        /// Properties/PublishConfiguration.xml; the assembly version stays 1.0.0.0. Peers see
        /// <see cref="CompatibilityVersion"/>.
        /// </summary>
        internal static string Version => _version ?? (_version = ReadVersion());

        /// <summary>
        /// The numeric release part of <see cref="Version"/>, so "0.1.6.1h1" meets "0.1.6.1": a hotfix changes
        /// nothing peers must agree on. A different release is refused unless the host ignores compatibility.
        /// </summary>
        internal static string CompatibilityVersion =>
            _compatibilityVersion ?? (_compatibilityVersion = ReleasePart(Version));

        /// <summary>
        /// A local build's stamp (empty when published), to confirm the game runs what was just compiled.
        /// Never on the wire.
        /// </summary>
        internal static string BuildStamp => _buildStamp ?? (_buildStamp = ReadBuildStamp());

        /// <summary>The options-screen version: plain when released; local builds add stamp and protocol.</summary>
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
            // Drop a "+<...>" suffix; the result is compared and read as a version.
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
            // The flight log must be recording before anything else can fail.
            FlightRecorder.Start(StampedVersion);

            // Inbox backpressure warnings are pipeline faults, never gated.
            Game.Sync.Infrastructure.SyncInbox.LogWarn =
                delegate(string message) { SyncLog.Warn(LogTopic.Pipeline, message); };

            // Also where the Steam relay backend sits, when this copy of the game has one.
            string modFolder = null;
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                SyncLog.Detail(LogTopic.Startup, "Loaded from " + asset.path + ".");
                modFolder = System.IO.Path.GetDirectoryName(asset.path);
            }

            // The game picks the locale source matching its language; no mod language setting.
            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            // One embedded locales/<lang>.properties per game locale id; unlisted languages fall back to English.
            foreach (var locale in LocaleSources)
                GameManager.instance.localizationManager.AddSource(
                    locale.Key, new PropertiesLocaleSource(Setting, locale.Value));

            // Persist / load settings to the standard mod settings store.
            AssetDatabase.global.LoadSettings(Name, Setting, new Setting(this));

            // Core logs through the same logger via ColossalModLogger.
            IModLogger coreLog = ColossalModLogger.Instance;

            // Relay availability is decided once; without Steam only direct connections exist.
            SteamRelayBootstrap.Register(coreLog, modFolder);

            // Needs the backend above: it is what knows the platform account's name.
            Setting.ApplyPlatformNamePreset();

            Service = new MultiplayerService(coreLog);

            // The live service owns the clock, recovery state and arbiter.
            Game.Sync.Infrastructure.SyncInbox.Arbitrate = Service.SettleResyncReport;

            // UIUpdate: the pump must run in the main menu and while paused (the options screen pauses).
            updateSystem.UpdateAt<MultiplayerSystem>(SystemUpdatePhase.UIUpdate);
            // Bindings for the main-menu multiplayer screen (UI module in UI/).
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);
            // UIUpdate: GameSimulation stops at speed 0, so pauses could never replicate there. Capture is 1 Hz
            // gated internally.
            updateSystem.UpdateAt<Game.Sync.Systems.CityStateSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Remote milestone popups use the native MilestoneReachedEvent; DevTreeSystem treats it as a reward,
            // so remove the points from our marked event.
            updateSystem.UpdateAfter<Game.Sync.Systems.RemoteMilestoneRewardCorrectionSystem,
                global::Game.City.DevTreeSystem>(SystemUpdatePhase.GameSimulation);
            // Fee events arrive on both sides of ServiceFeeSystem (transit/parking before, utility sales after);
            // clear the client queue at both, channel 24 reinstalls the host records.
            updateSystem.UpdateBefore<Game.Sync.Systems.ServiceFeeIngressBoundarySystem,
                global::Game.Simulation.ServiceFeeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.ServiceFeeEgressBoundarySystem,
                global::Game.Simulation.UtilityFeeSystem>(SystemUpdatePhase.GameSimulation);
            // CityServiceBudgetSystem reads last frame's fee/upkeep records, then rebuilds them locally: install
            // host input before the read, restore host output after, and pin the income/expense slots.
            updateSystem.UpdateBefore<Game.Sync.Systems.ServiceAccountingInputSystem,
                global::Game.Simulation.CityServiceBudgetSystem>(
                SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAfter<Game.Sync.Systems.ServiceAccountingCorrectionSystem,
                global::Game.Simulation.CityServiceBudgetSystem>(
                SystemUpdatePhase.ModificationEnd);
            // Capture the short-lived MovingAway decision before its consumer. Register once: orderings add up.
            updateSystem.UpdateBefore<
                Game.Sync.Systems.ResidentialOccupancyDepartureCaptureSystem,
                global::Game.Simulation.HouseholdMoveAwaySystem>(
                SystemUpdatePhase.GameSimulation);
            // Keep the downloaded world's household contracts before the first native rent pass; one-shot per
            // world.
            updateSystem.UpdateBefore<Game.Sync.Systems.ResidentialOccupancyRentSeedSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // Directly after RentAdjustSystem and before PropertyRenterSystem's payment pass. Same 1024-frame
            // interval, so no full cache scan per tick.
            updateSystem.UpdateAfter<Game.Sync.Systems.PropertyRentSyncSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // After the final daily-economy writers, in household partitions, so every family in a building
            // reads the same host snapshot.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdEconomyCorrectionSystem,
                global::Game.Simulation.RentAdjustSystem>(SystemUpdatePhase.GameSimulation);
            // Income is read earlier (citizen wellbeing, the building's wealth prop), right after the pass that
            // recomputes it from local employment; restore the host value in between.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdIncomeBoundarySystem,
                global::Game.Simulation.HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);
            // Let ResourceBuyerSystem's shoppers run, then correct money and shopped value after the sale books.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialHouseholdPurchaseCorrectionSystem,
                global::Game.Simulation.ResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
            // Fees derive from fulfilled utility quantities: correct after their native writers; demand,
            // connectivity and warnings stay local.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialElectricityFeeCorrectionSystem,
                global::Game.Simulation.DispatchElectricitySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialWaterFeeCorrectionSystem,
                global::Game.Simulation.DispatchWaterSystem>(SystemUpdatePhase.GameSimulation);
            // Strip local closure/seeking proposals before anything acts on them; the proposers keep running for
            // the figures they produce.
            updateSystem.UpdateBefore<Game.Sync.Systems.CompanyLifecycleBoundarySystem,
                global::Game.Simulation.CompanyMoveAwaySystem>(SystemUpdatePhase.GameSimulation);
            // Host capture at the native company cadence; clients hold the accounting calculator.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyStatsSyncSystem,
                global::Game.Simulation.CompanyEconomyStatisticSystem>(
                SystemUpdatePhase.GameSimulation);
            // FindJobSystem's 16-frame cadence, rather than the 2,048-frame accounting partition; also repairs
            // the Employee/Worker graph after local matching.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyStateBoundarySystem,
                global::Game.Simulation.FindJobSystem>(SystemUpdatePhase.GameSimulation);
            // The panel recomputes Production from efficiency factors that these two passes rewrite from local
            // state (stock and rounding; area depletion). Each correction follows its writer at its interval and
            // offset, hence two systems.
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyProcessingBoundarySystem,
                global::Game.Simulation.ProcessingCompanySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Game.Sync.Systems.CompanyExtractorBoundarySystem,
                global::Game.Simulation.ExtractorCompanySystem>(SystemUpdatePhase.GameSimulation);
            // Before PropertyProcessingSystem drains the rent-action queue. The queue persists; the ordering only
            // lets a move-in land in the same tick when the two updates coincide.
            updateSystem.UpdateBefore<Game.Sync.Systems.ResidentialOccupancySyncSystem,
                global::Game.Simulation.PropertyProcessingSystem>(SystemUpdatePhase.GameSimulation);
            // Finalize move-ins right after both renter links exist, before PropertyRenterSystem's payment pass.
            updateSystem.UpdateAfter<Game.Sync.Systems.ResidentialOccupancyFinalizeSystem,
                global::Game.Simulation.PropertyProcessingSystem>(SystemUpdatePhase.GameSimulation);
            // Births, deaths and splits change HouseholdCitizen without RentersUpdated; observe after citizen
            // initialization so a newborn is complete. Later writers are caught next frame.
            updateSystem.UpdateAfter<
                Game.Sync.Systems.ResidentialHouseholdLifecycleObservationSystem,
                global::Game.Citizens.CitizenInitializeSystem>(
                SystemUpdatePhase.GameSimulation);
            // UIUpdate: the camera focus must publish while paused.
            updateSystem.UpdateAt<Game.Sync.Players.PlayerCursorSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Raycast phase, after the tool's input: the only slot where an extra input joins this frame's job,
            // so roads, tracks and pipes can be hovered.
            updateSystem.UpdateAfter<Game.Sync.Players.PlayerHoverRaycastSystem,
                global::Game.Tools.ToolRaycastSystem>(SystemUpdatePhase.Raycast);
            // Rendering: markers draw every frame, paused or not.
            updateSystem.UpdateAt<Game.Sync.Players.RemotePlayerMarkerSystem>(SystemUpdatePhase.Rendering);
            // UIUpdate: policies change while paused, and GameSimulation stops at speed 0. The scan is 1 Hz gated.
            updateSystem.UpdateAt<Game.Sync.Systems.PolicySyncSystem>(SystemUpdatePhase.UIUpdate);
            // ModificationEnd: a tool apply's one-frame Created tags are still alive.
            updateSystem.UpdateAt<Game.Sync.Systems.BuildSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.Net.NetSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // Edge-only refreshes must include their junctions before native network processing.
            updateSystem.UpdateBefore<Game.Sync.Systems.Net.NetJunctionRefreshSystem,
                global::Game.Net.ReferencesSystem>(SystemUpdatePhase.Modification2B);
            updateSystem.UpdateAt<Game.Sync.Systems.DeleteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // After DeleteSyncSystem, so a bulldozed zoned building (a player delete) is told apart from the
            // simulation retiring one.
            updateSystem.UpdateAt<Game.Sync.Systems.GrowableSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // A different net prefab drawn over an edge: Updated, not Created, with a changed PrefabRef.
            updateSystem.UpdateAt<Game.Sync.Systems.NetReplaceSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.ZoneSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.TerrainSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.UpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.MoveSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.NetUpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.AreaSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.RouteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<Game.Sync.Systems.TilePurchaseSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // ModificationEnd: other mods' tools have applied and chunk change versions still show what they
            // wrote. It also runs while paused.
            updateSystem.UpdateAt<Game.Sync.Systems.Mods.ModStateSyncSystem>(
                SystemUpdatePhase.ModificationEnd);
            // After Modification2's event initialization places the disaster; its Created tag is gone next frame.
            updateSystem.UpdateAt<Game.Sync.Systems.DisasterSyncSystem>(SystemUpdatePhase.ModificationEnd);
            // After the game's auto-name initialization fills a new name draw. ModificationEnd also runs while
            // paused and still sees the one-frame Created/Updated tags.
            updateSystem.UpdateAfter<Game.Sync.Systems.NameSyncSystem,
                global::Game.Common.RandomLocalizationInitializeSystem>(SystemUpdatePhase.ModificationEnd);
            // UIUpdate: nodes can be bought while paused, and the host's points snapshot (also UIUpdate) would
            // otherwise refill the buyer's points without the purchase ever replicating.
            updateSystem.UpdateAt<Game.Sync.Systems.DevTreeSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Usable while paused; after SelectedInfoUISystem so the resulting state is captured.
            updateSystem.UpdateAfter<Game.Sync.Systems.VisualCustomizationSyncSystem,
                global::Game.UI.InGame.SelectedInfoUISystem>(SystemUpdatePhase.UIUpdate);
            // Realization runs at ToolUpdate: definitions are consumed at Modification1 and lose Updated at
            // Cleanup (see SyncRealizeSystem). Stranded movers are repaired first, before the default tool can
            // select one; one-shot per load, frame-budgeted.
            updateSystem.UpdateBefore<Game.Sync.Systems.WorldRepairSystem>(
                SystemUpdatePhase.ToolUpdate);
            // Finish remote terrain readback before a local tool previews against stale heights.
            updateSystem.UpdateBefore<Game.Sync.Systems.TerrainReadbackBarrierSystem>(
                SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Game.Sync.Systems.SyncRealizeSystem>(SystemUpdatePhase.ToolUpdate);
            // After the tool decided, before ToolOutputSystem consumes the graph: the only frame that serializes
            // it.
            updateSystem.UpdateBefore<Game.Sync.Systems.ObjectToolApplyCaptureSystem,
                global::Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
            // After ToolOutputBarrier: the only slot where tool definitions exist unconsumed.
            updateSystem.UpdateAfter<Game.Sync.Systems.DefinitionGateSystem, global::Game.Tools.ToolOutputBarrier>(
                SystemUpdatePhase.ToolUpdate);
            // Before owner resolution, which removes a sub-element's owner description resolved or not.
            updateSystem.UpdateBefore<Game.Sync.Systems.OwnerDefinitionSnapshotSystem,
                global::Game.Tools.FindOwnersSystem2>(SystemUpdatePhase.Modification2B);
            // UIUpdate: hosting starts from the paused options screen, and a joiner's initial world must still
            // stream.
            updateSystem.UpdateAt<Game.Sync.Systems.WorldResyncSystem>(SystemUpdatePhase.UIUpdate);

            // One startup line with the numbers that decide whether two players can play together.
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
