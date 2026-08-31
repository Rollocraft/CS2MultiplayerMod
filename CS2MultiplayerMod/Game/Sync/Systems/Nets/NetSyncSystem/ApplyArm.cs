using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // Arming a commit - net, route or object - and the bookkeeping around one: charging
    // construction for what committed, remembering spans just realized so a duplicate is not built
    // from them, and nudging the active tool into producing its output.
    public partial class NetSyncSystem
    {
        private void DiscardStaleTransactionTemps(string why)
        {
            int cleared = ClearTempEntities(ActiveTransactionQuery());
            if (cleared <= 0) return;
            Mod.log.Warn("[MP] SyncApply: discarded " + cleared + " uncommitted Temp(s) - " + why + ".");
            Diagnostics.FlightRecorder.Note("transaction temps discarded=" + cleared + " (" + why + ")");
        }

        /// <summary>
        /// Arm the isolated net-domain commit for definitions a sibling system (delete/replace)
        /// created this frame. They become Temp net entities at the following Modification and
        /// <see cref="RealizePending"/> applies them natively. Only call when
        /// <see cref="CanBuildDefinitions"/> is true (and after
        /// <see cref="PrepareDefinitionFrame"/>). <paramref name="onCommitLost"/> is invoked if the
        /// armed batch never materialises (the apply window expiring) - it must re-queue the batch's
        /// source commands so the work is rebuilt, not lost.
        /// </summary>
        public void ArmNetCommit(System.Action onCommitLost, string source)
        {
            ArmNetCommit(onCommitLost, null, source);
        }

        /// <summary>
        /// Arm one correlated net mutation graph and retain its completion callback until the
        /// committed Temp graph has fully drained.
        /// </summary>
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
            Diagnostics.FlightRecorder.Note("net " + source + " batch armed");
            return true;
        }

        /// <summary>
        /// Arm one route root and its complete waypoint/segment graph. Definitions materialize as
        /// Temps later in the frame; the next quiet ToolUpdate validates and applies only that
        /// isolated route domain.
        /// </summary>
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
            Diagnostics.FlightRecorder.Note("route " + source + " operation armed");
            return true;
        }

        /// <summary>
        /// Arm one object graph. Its Object, owned Node/Edge/Lane/Aggregate, and Area Temps are
        /// validated and consumed together. The source callback is retained until drain completes.
        /// </summary>
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
            Diagnostics.FlightRecorder.Note((rootlessAssetStamp ? "asset stamp " : "object ") +
                                               source + " operation armed");
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
                // Charging is accounting, not geometry. Never destabilize a successfully committed
                // network transaction merely because the money singleton changed unexpectedly.
                Mod.log.Warn("[MP] NetSync: remote net charge failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Record a span this machine just realized from a remote command, so capture-side heuristics
        /// (NetReplaceSync's extension detection) can recognise follow-on local edits of that geometry
        /// - e.g. the game's node reduction merging it into a neighbour - as remote work, not
        /// something to broadcast back.
        /// </summary>
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
        /// Set the tool's protected <c>m_ForceUpdate</c> flag so it regenerates its preview
        /// definitions on its next update even with a motionless cursor - the definition gate removed
        /// the preview, and without this a parked cursor would show none until moved. Runtime access
        /// to the loaded game assembly's own member; a rename in a future patch degrades gracefully
        /// (the preview simply returns on the next cursor move).
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

        /// <summary>
        /// <see cref="DefinitionGateSystem"/>'s hook: after it destroys the tool's buffered
        /// definitions on an armed frame, the tool must regenerate its gesture next update.
        /// </summary>
        public void ForceActiveToolUpdate()
        {
            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool != null && !(tool is global::Game.Tools.DefaultToolSystem)) TryForceToolUpdate(tool);
        }

        /// <summary>
        /// Apply remote terrain samples through the brush domain only. Local brush previews were
        /// Disabled by <see cref="PrepareAuxiliaryTemps"/> and are restored after ToolOutputBarrier.
        /// </summary>
        public bool CommitAuxiliaryTempsNow()
        {
            if (_applyBrushesSystem == null) return false;
            _applyBrushesSystem.Update();
            return true;
        }
    }
}
