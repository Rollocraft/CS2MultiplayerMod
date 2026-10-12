using System;
using System.Runtime.InteropServices;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // The send-rate governor: climb while Steam reports a healthy link, back off when it does not.
    public sealed partial class SteamRelayTransport
    {
        /// <summary>The route in use; a relayed route explains a rate the uplink could beat.</summary>
        private static string RouteOf(Endpoint endpoint)
        {
            try
            {
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out SteamNetConnectionInfo_t info))
                    return "unknown";
                return (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0
                    ? "relayed"
                    : "direct";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private bool SetInt32(ESteamNetworkingConfigValue setting, ESteamNetworkingConfigScope scope,
                              IntPtr scopeObject, int value, string description)
        {
            GCHandle pin = default(GCHandle);
            try
            {
                var boxed = new int[] { value };
                pin = GCHandle.Alloc(boxed, GCHandleType.Pinned);
                bool ok = SteamNetworkingUtils.SetConfigValue(
                    setting,
                    scope,
                    scopeObject,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pin.AddrOfPinnedObject());
                if (!ok)
                    _log.Warn(LogTopic.Transport, "Steam refused the relay " + description +
                        " setting; transfers may be slow.");
                return ok;
            }
            catch (Exception ex)
            {
                // Non-fatal: the transfer still completes, just slower.
                _log.Warn(LogTopic.Transport, "Could not set the relay " + description + " (" +
                    ex.Message + ").");
                return false;
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
            }
        }

        /// <summary>
        /// Drops to a measured rate and holds. The clamp limits what one pessimistic reading can take away.
        /// </summary>
        private void Backoff(Endpoint endpoint, int target)
        {
            int least = (int)(endpoint.SendRate * MaxSingleBackoff);
            int rate = Math.Max(SendRateFloorBytesPerSecond, Math.Max(least, Math.Min(endpoint.SendRate, target)));

            // Known-good is the rate flowing when the path complained, shaded down, not the rate cut to:
            // otherwise every backoff is permanent, since the fast climb only runs below SafeRate. Shading
            // still walks a repeatedly congested estimate down.
            endpoint.SafeRate = Math.Max(
                rate, (int)(Math.Min(endpoint.SafeRate, endpoint.SendRate) * SafeRateShare));
            endpoint.HoldTicks = BackoffHoldTicks;
            endpoint.Strikes = 0;
            if (rate != endpoint.SendRate) ApplySendRate(endpoint, rate);
        }

        /// <summary>
        /// Pins the paced rate by setting min and max together, as Steam documents; nothing estimates
        /// bandwidth for us.
        /// </summary>
        private void ApplySendRate(Endpoint endpoint, int bytesPerSecond)
        {
            var scopeObject = new IntPtr(endpoint.Handle.m_HSteamNetConnection);
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject,
                     bytesPerSecond, "minimum send rate");
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject,
                     bytesPerSecond, "maximum send rate");
            endpoint.SendRate = bytesPerSecond;
        }

        /// <summary>
        /// Congestion control: climb while quiet, fall back after two complaints running, then probe up a
        /// step at a time; <see cref="Endpoint.SafeRate"/> remembers what held, so recovering is the fast
        /// climb (why <see cref="Backoff"/> keeps the two apart). A filling queue raises ping, but relays
        /// police by discarding, leaving ping flat, so loss also cuts: the received share times the wire
        /// rate is the limit. Readings lag, hence the hold after a cut, and a complaint the current rate is
        /// delivering through holds rather than cuts.
        /// </summary>
        private void Govern()
        {
            long intervalMs = _govern.ElapsedMilliseconds;
            if (intervalMs < GovernIntervalMs) return;
            _govern.Restart();

            bool report = _probe.ElapsedMilliseconds >= ThroughputProbeMs;
            if (report) _probe.Restart();

            lock (_gate)
            {
                foreach (var pair in _byId)
                {
                    Endpoint endpoint = pair.Value;
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                    try
                    {
                        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(
                            endpoint.Handle, ref status, 0, ref lanes) != EResult.k_EResultOK)
                            continue;
                    }
                    catch (Exception) { continue; }

                    // Steam holds everything behind a lost segment; a lossy path can complete no message for seconds.
                    if (status.m_flInBytesPerSec >= InboundActivityBytesPerSecond)
                        endpoint.LastInboundMs = MonotonicClock.NowMs;

                    long outstanding = endpoint.QueuedBytes +
                                       status.m_cbPendingReliable + status.m_cbSentUnackedReliable;
                    bool bulk = outstanding >= BulkBacklogBytes;
                    if (bulk) endpoint.BeginBulk();
                    long goodput = endpoint.MeasureGoodput(outstanding, intervalMs);

                    if (!bulk)
                    {
                        string finished = endpoint.FinishBulk();
                        if (finished != null)
                            _log.Detail(LogTopic.Transport, "Relay " + endpoint.Id + " " + finished +
                                " over a " + RouteOf(endpoint) + " route.");

                        int idle = Math.Min(SendRateStartBytesPerSecond, endpoint.SafeRate);
                        if (endpoint.SendRate != idle) ApplySendRate(endpoint, idle);
                        endpoint.HoldTicks = 0;
                        endpoint.Strikes = 0;
                        continue;
                    }

                    // No ping until traffic flows; a zero baseline would read every ping as congestion. The floor creeps
                    // up so a genuinely slower path re-baselines.
                    bool pingKnown = status.m_nPing > 0;
                    if (pingKnown)
                    {
                        // One millisecond at a time, so mild congestion does not become the baseline.
                        if (status.m_nPing < endpoint.PingFloorMs) endpoint.PingFloorMs = status.m_nPing;
                        else endpoint.PingFloorMs++;
                    }

                    int pingBudget = endpoint.PingFloorMs +
                                     Math.Max(CongestedPingExcessMs, endpoint.PingFloorMs / 2);
                    float quality = status.m_flConnectionQualityRemote; // negative until the peer reports

                    bool queueing = pingKnown && status.m_nPing > pingBudget;
                    bool losing = quality >= 0f && quality < HealthyRemoteQuality;
                    bool delivering = RelaySendFeedback.IsDelivering(goodput, endpoint.SendRate, DeliveredShare);
                    // Until the peer reports quality, our own acknowledgements are the only loss signal.
                    bool starving = quality < 0f &&
                                    !RelaySendFeedback.IsDelivering(goodput, endpoint.SendRate, StarvedShare);

                    if (endpoint.HoldTicks > 0)
                    {
                        endpoint.HoldTicks--;
                    }
                    else if ((queueing || losing || starving) && !delivering)
                    {
                        if (++endpoint.Strikes >= StrikesBeforeBackoff)
                        {
                            // What the peer received is what the path carries: fall straight to it. A filling queue only says
                            // "less than this".
                            float wire = status.m_flOutBytesPerSec;
                            float carrying = wire > 0f ? wire : endpoint.SendRate;
                            Backoff(endpoint, losing
                                ? (int)(carrying * quality * 0.95f)
                                : starving
                                    ? (int)Math.Min(goodput, int.MaxValue)
                                    : (int)(endpoint.SendRate * 0.75f));
                        }
                    }
                    else
                    {
                        endpoint.Strikes = 0;

                        // A stale complaint holds, but does not climb. Below the known-good rate climb back to it; above,
                        // step up.
                        if (!queueing && !losing)
                        {
                            int rate = endpoint.SendRate;
                            int next = rate < endpoint.SafeRate
                                ? Math.Min(endpoint.SafeRate, rate + Math.Max(rate / 6, SendRateStepBytesPerSecond))
                                : Math.Min(SendRateCeilingBytesPerSecond, rate + SendRateStepBytesPerSecond);
                            if (next != rate) ApplySendRate(endpoint, next);
                        }
                    }

                    if (!report) continue;
                    _log.Detail(LogTopic.Transport, "Relay " + endpoint.Id + " sending: " +
                        (outstanding / 1024) + " KB buffered at " + (goodput / 1024) + " KB/s (paced " +
                        (endpoint.SendRate / 1024) + " KB/s, held " + (endpoint.SafeRate / 1024) +
                        " KB/s, wire " + ((int)status.m_flOutBytesPerSec / 1024) + " KB/s), ping " +
                        status.m_nPing + " of " + pingBudget + " ms, peer received " +
                        (quality < 0f ? "?" : ((int)(quality * 100)).ToString()) + "%, " +
                        RouteOf(endpoint) + " route.");
                }
            }
        }
    }
}
