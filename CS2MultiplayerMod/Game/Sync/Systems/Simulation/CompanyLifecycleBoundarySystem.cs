using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Just before the move-away executor: strips local closure and property-seeking proposals on a
    /// client. State lives in CompanyStatsSyncSystem; this is only its ordering point.
    /// </summary>
    public sealed partial class CompanyLifecycleBoundarySystem : GameSystemBase
    {
        private CompanyStatsSyncSystem _companies;

        protected override void OnCreate()
        {
            base.OnCreate();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
        }

        /// <summary>The executor's own interval: proposals wait for it anyway.</summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("CompanyStats.Lifecycle"))
            {
                if (_companies != null) _companies.CancelClientLifecycleDecisions();
            }
        }
    }
}
