using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>Who occupies each commercial, industrial and office building, its figures and held goods.</summary>
    internal sealed class CompanyStatsStateChannel : PagedPropertyChannel<CompanyStatsSnapshot>
    {
        public const byte Id = 22;

        public CompanyStatsStateChannel(CompanyStatsSyncSystem runtime)
            : base(Id, runtime, CompanyStatsSnapshot.Read, LogTopic.Commercial, "CompanyStats") { }
    }
}
