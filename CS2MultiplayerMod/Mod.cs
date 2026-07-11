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
        /// Log a chatty, troubleshooting-only line - the per-action sync notices and the
        /// periodic diagnostics. Silent unless "Verbose Logging" is enabled in settings, so
        /// the default log stays quiet and only the important lifecycle/fault lines remain.
        /// </summary>
        public static void Verbose(string message)
        {
            if (Setting != null && Setting.VerboseLogging) log.Info(message);
        }

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

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            // Register settings and the locale sources backing them (and all runtime
            // strings). The game picks the source matching the language the player
            // set in the options — no mod-specific language setting, like vanilla.
            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            // Each language is one embedded locales/<lang>.properties file. EN/DE key
            // parity is enforced by CI over those files (.github/workflows/locale.yml),
            // not at runtime.
            GameManager.instance.localizationManager.AddSource("en-US", new PropertiesLocaleSource(Setting, "en"));
            GameManager.instance.localizationManager.AddSource("de-DE", new PropertiesLocaleSource(Setting, "de"));

            // Persist / load settings to the standard mod settings store.
            AssetDatabase.global.LoadSettings(Name, Setting, new Setting(this));

            // Stand up the multiplayer core (portable session + game logger adapter) and
            // register the ECS system that pumps it once per simulation tick.
            Service = new MultiplayerService(new ColossalModLogger(log));
            log.Info("Multiplayer core initialised. Protocol v" +
                     CS2MultiplayerMod.Core.Protocol.ProtocolConstants.ProtocolVersion +
                     ". Registering sync systems...");

            // UIUpdate, not GameSimulation: the session pump must also run in the main
            // menu (joining from there) and while the game is paused - the options
            // screen pauses the simulation, which previously froze all connection
            // handling exactly while the player was looking at the connect buttons.
            updateSystem.UpdateAt<MultiplayerSystem>(SystemUpdatePhase.UIUpdate);
            // Bindings for the main-menu "Join Game" dialog (UI module in UI/).
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);
            // UIUpdate, not GameSimulation: the GameSimulation phase stops ticking the
            // moment the game is paused (selectedSpeed 0), so a system there can never
            // observe a pause to replicate it, nor apply a remote pause once stopped -
            // pause/play and speed changes never synced. UIUpdate runs every frame in
            // every state, so the simulation-speed channel (and the rest of the city
            // state) now stays in sync even while a player is paused. Channel capture is
            // gated to ~1 Hz internally, so the render-rate phase adds no extra traffic.
            updateSystem.UpdateAt<Game.Sync.Systems.CityStateSyncSystem>(SystemUpdatePhase.UIUpdate);
            // Also UIUpdate: publishing the local camera focus must keep going while a
            // player is paused (so partners still see where they are), and GameSimulation
            // barely ticked it - the live log showed ~1 position sent per 30 s.
            updateSystem.UpdateAt<Game.Sync.Players.PlayerCursorSyncSystem>(SystemUpdatePhase.UIUpdate);
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
            updateSystem.UpdateAt<Game.Sync.Systems.DeleteSyncSystem>(SystemUpdatePhase.ModificationEnd);
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
            // Realization must run at ToolUpdate: definition entities are consumed at
            // Modification1 and their Updated tag is stripped at Cleanup, so a definition
            // spawned at ModificationEnd is never realized (see SyncRealizeSystem).
            updateSystem.UpdateAt<Game.Sync.Systems.SyncRealizeSystem>(SystemUpdatePhase.ToolUpdate);
            // After ToolOutputBarrier: tools record their definitions through that end-of-phase
            // buffer, so this is the first (and only) slot where they exist as entities but have
            // not been consumed - the gate keeps them out of an armed net commit (see there).
            updateSystem.UpdateAfter<Game.Sync.Systems.DefinitionGateSystem, global::Game.Tools.ToolOutputBarrier>(
                SystemUpdatePhase.ToolUpdate);
            // UIUpdate, not GameSimulation, for the same reason as the session pump:
            // hosting starts from the options screen, which pauses the simulation -
            // at GameSimulation the queued initial world stream for a joining client
            // was never processed while the host sat in the (paused) menu, leaving
            // the client stuck in WaitingForMap forever.
            updateSystem.UpdateAt<Game.Sync.Systems.WorldResyncSystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

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
