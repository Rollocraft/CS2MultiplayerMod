using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Arming a net, route or object commit, plus charging construction, remembering realized spans and
    // nudging the active tool.
    public partial class NetSyncSystem
    {
        private void DiscardStaleTransactionTemps(string why)
        {
            int cleared = ClearTempEntities(ActiveTransactionQuery());
            if (cleared <= 0) return;
            SyncLog.Warn(LogTopic.Nets, "SyncApply: discarded " + cleared +
                " uncommitted Temp(s) - " + why + ".");
        }

        /// <summary>
        /// Arms the isolated net commit for definitions a sibling system created this frame. Call only when
        /// <see cref="CanBuildDefinitions"/>, after <see cref="PrepareDefinitionFrame"/>.
        /// <paramref name="onCommitLost"/> must re-queue the source commands.
        /// </summary>
        public void ArmNetCommit(System.Action onCommitLost, string source) => ArmNetCommit(onCommitLost, null, source);

        /// <summary>Arms one correlated mutation graph; the callback is kept until it drains.</summary>
        public bool ArmNetCommit(System.Action onCommitLost,
            System.Action onCommitComplete, string source)
        {
            if (IsCommitBusy) return false;
            _pendingApply = true;
            _pendingTransactionKind = RemoteToolTransactionKind.Net;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            SyncLog.Trace(LogTopic.Nets, "net " + source + " batch armed");
            return true;
        }

        /// <summary>Arms a route root and its waypoint/segment graph; applied alone on the next quiet ToolUpdate.</summary>
        public bool ArmRouteCommit(System.Action onCommitLost,
            System.Action onCommitComplete, string source)
        {
            if (IsCommitBusy || _applyRoutesSystem == null)
                return false;
            _pendingApply = true;
            _pendingTransactionKind = RemoteToolTransactionKind.Route;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            SyncLog.Trace(LogTopic.Nets, "route " + source + " operation armed");
            return true;
        }

        /// <summary>Arms one object graph; object, owned net and area Temps are consumed together.</summary>
        public bool ArmObjectCommit(System.Action onCommitLost, System.Action onCommitComplete,
            string source, bool rootlessAssetStamp = false,
            List<ArmedOwnerDefinition> ownerDefinitions = null)
        {
            if (IsCommitBusy) return false;
            _pendingApply = true;
            _pendingTransactionKind = rootlessAssetStamp
                ? RemoteToolTransactionKind.AssetStampGraph
                : RemoteToolTransactionKind.ObjectGraph;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            if (ownerDefinitions != null) _pendingOwnerDefinitions.AddRange(ownerDefinitions);
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            SyncLog.Trace(LogTopic.Nets, (rootlessAssetStamp ? "asset stamp " : "object ") + source +
                " operation armed");
            return true;
        }

        private void ChargeCommittedNetConstruction()
        {
            long amount = _committingNetConstructionCharge;
            int courses = _committingNetConstructionChargeCourses;
            _committingNetConstructionCharge = 0;
            _committingNetConstructionChargeCourses = 0;
            if (amount <= 0) return;

            try
            {
                ConstructionCharger.ChargeAmount(EntityManager, amount,
                    "remote net operation (" + courses + " course(s))");
            }
            catch (System.Exception ex)
            {
                // Accounting never destabilizes a committed transaction.
                SyncLog.Warn(LogTopic.Nets, "NetSync: remote net charge failed: " + ex.Message);
            }
        }

        /// <summary>Remembers a realized span so node reduction re-surfacing it is not broadcast back.</summary>
        public void RecordRealizedSpan(Bezier4x3 curve)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            _recentRealizedSpans.Add((curve, now + 10000));
        }

        /// <summary>True when <paramref name="piece"/> is a 3D sub-curve of a recently realized span.</summary>
        public bool WasRecentlyRealized(Bezier4x3 piece)
        {
            for (int i = 0; i < _recentRealizedSpans.Count; i++)
                if (SplitMatch.IsSubCurve3D(piece, _recentRealizedSpans[i].curve)) return true;
            return false;
        }

        private void PruneRecentRealizedSpans()
        {
            if (_recentRealizedSpans.Count == 0 || Mod.Service == null) return;
            long now = Mod.Service.NowMs;
            for (int i = _recentRealizedSpans.Count - 1; i >= 0; i--)
                if (_recentRealizedSpans[i].expiresMs < now) _recentRealizedSpans.RemoveAt(i);
        }

        private static System.Reflection.FieldInfo _forceUpdateField;
        private static bool _forceUpdateFieldResolved;

        /// <summary>
        /// Sets the tool's protected <c>m_ForceUpdate</c> so a still cursor regenerates the preview the
        /// gate removed. A future rename degrades to "preview returns on the next move".
        /// </summary>
        private void TryForceToolUpdate(global::Game.Tools.ToolBaseSystem tool)
        {
            if (!_forceUpdateFieldResolved)
            {
                _forceUpdateFieldResolved = true;
                _forceUpdateField = typeof(global::Game.Tools.ToolBaseSystem).GetField(
                    "m_ForceUpdate",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_forceUpdateField != null) _forceUpdateField.SetValue(tool, true);
        }

        public void ForceActiveToolUpdate()
        {
            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool != null && !(tool is global::Game.Tools.DefaultToolSystem)) TryForceToolUpdate(tool);
        }

        /// <summary>
        /// Applies remote terrain samples through the brush domain only; local previews come back after
        /// ToolOutputBarrier.
        /// </summary>
        public bool CommitAuxiliaryTempsNow()
        {
            if (_applyBrushesSystem == null) return false;
            _applyBrushesSystem.Update();
            return true;
        }
    }
}
