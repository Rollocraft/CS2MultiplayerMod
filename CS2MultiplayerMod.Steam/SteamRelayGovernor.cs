using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Choosing how fast to push bytes at a peer. Steam's relay reports its own view of link
    // quality and ping, and the rate is walked up while that looks healthy and backed off when it
    // does not - a world transfer saturating the link is the case that matters.
    public sealed partial class SteamRelayTransport
    {
        /// <summary>
        /// Which path the traffic is taking. The first thing to read when a transfer
        /// disappoints: a relayed route explains a rate the uplink could beat on its own.
        /// </summary>
        private static string RouteOf(Endpoint endpoint)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out info))
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
        /// Drop to a measured rate and stop reacting for a while. The clamp bounds how much
        /// one reading may take away, so a pessimistic sample costs some throughput rather
        /// than the transfer.
        /// </summary>
        private void Backoff(Endpoint endpoint, int target)
        {
            int least = (int)(endpoint.SendRate * MaxSingleBackoff);
            int rate = Math.Max(SendRateFloorBytesPerSecond, Math.Max(least, Math.Min(endpoint.SendRate, target)));

            // The rate known to hold is the one that was flowing when the path complained,
            // shaded down - not the one just cut to. Setting it to the cut made every
            // backoff permanent: the fast climb only runs below SafeRate, so a connection
            // that backed off once crawled upward in single steps for the rest of the
            // session, and the idle clamp then handed that crawl to the next transfer as
            // its starting rate. Shading is what still walks a repeatedly congested
            // estimate downwards instead of oscillating around a level it never carried.
            endpoint.SafeRate = Math.Max(
                rate, (int)(Math.Min(endpoint.SafeRate, endpoint.SendRate) * SafeRateShare));
            endpoint.HoldTicks = BackoffHoldTicks;
            endpoint.Strikes = 0;
            if (rate != endpoint.SendRate) ApplySendRate(endpoint, rate);
        }

        /// <summary>
        /// Pin the connection's paced rate. Min and max are set together because Steam
        /// documents them that way: nothing estimates the bandwidth for us, so a min above
        /// what the path carries is a floor the sender cannot come down from.
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
        /// Congestion control for the rate Steam will not work out on its own: climb while
        /// the path is quiet, fall back when it complains twice running, and probe upwards
        /// from there a step at a time. <see cref="Endpoint.SafeRate"/> remembers what held,
        /// so the expensive overshoot happens once per session rather than once per cycle -
        /// and recovering to it is the fast climb, which is why <see cref="Backoff"/> must
        /// not collapse the two together.
        ///
        /// Two different things throttle this path and only one of them shows up as delay.
        /// A queue that fills raises the ping within a second. A rate limiter - Valve's
        /// relays police their traffic - just discards the excess, so the ping stays flat
        /// at 60 ms while the peer receives half of what was sent. Watching delay alone is
        /// blind to that, which is how a run once sat at 2400 KB/s paced, 48% received and
        /// 180 KB/s of actual progress for three minutes.
        ///
        /// So loss cuts the rate too, and it sizes its own cut: the share the peer received
        /// of what went out is the share of the wire rate that fits, and multiplying gives
        /// the limit directly instead of feeling for it 25% at a time. Both readings
        /// describe a window several seconds old, hence the hold after every cut - without
        /// it the same congestion is punished repeatedly and the rate walks to the floor.
        /// </summary>
        private void Govern()
        {
            if (_govern.ElapsedMilliseconds < GovernIntervalMs) return;
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
                        SteamNetworkingSockets.GetConnectionRealTimeStatus(
                            endpoint.Handle, ref status, 0, ref lanes);
                    }
                    catch (Exception) { continue; }

                    long outstanding = endpoint.QueuedBytes +
                                       status.m_cbPendingReliable + status.m_cbSentUnackedReliable;
                    bool bulk = outstanding >= BulkBacklogBytes;
                    if (bulk) endpoint.BeginBulk();
                    long goodput = endpoint.MeasureGoodput(outstanding, GovernIntervalMs);

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

                    // Steam reports no ping until traffic has flowed; folding that in would
                    // pin the baseline at zero and read every real ping as congestion. The
                    // floor then creeps up so a path that genuinely got slower re-baselines.
                    bool pingKnown = status.m_nPing > 0;
                    if (pingKnown)
                    {
                        // Rises a millisecond at a time so a plateau of mild congestion
                        // cannot quietly become this connection's idea of normal.
                        if (status.m_nPing < endpoint.PingFloorMs) endpoint.PingFloorMs = status.m_nPing;
                        else endpoint.PingFloorMs++;
                    }

                    int pingBudget = endpoint.PingFloorMs +
                                     Math.Max(CongestedPingExcessMs, endpoint.PingFloorMs / 2);
                    float quality = status.m_flConnectionQualityRemote; // negative until the peer reports

                    bool queueing = pingKnown && status.m_nPing > pingBudget;
                    bool losing = quality >= 0f && quality < HealthyRemoteQuality;

                    if (endpoint.HoldTicks > 0)
                    {
                        endpoint.HoldTicks--;
                    }
                    else if (queueing || losing)
                    {
                        if (++endpoint.Strikes >= StrikesBeforeBackoff)
                        {
                            // What the peer actually received is what the path will carry,
                            // so fall straight to it rather than stepping down and
                            // overshooting. A queue that fills has no such measurement
                            // behind it and only says "less than this".
                            float wire = status.m_flOutBytesPerSec;
                            float carrying = wire > 0f ? wire : endpoint.SendRate;
                            Backoff(endpoint, losing
                                ? (int)(carrying * quality * 0.95f)
                                : (int)(endpoint.SendRate * 0.75f));
                        }
                    }
                    else
                    {
                        endpoint.Strikes = 0;

                        // Below what already held, climb back to it; above it, feel the way
                        // up one step at a time.
                        int rate = endpoint.SendRate;
                        int next = rate < endpoint.SafeRate
                            ? Math.Min(endpoint.SafeRate, rate + Math.Max(rate / 6, SendRateStepBytesPerSecond))
                            : Math.Min(SendRateCeilingBytesPerSecond, rate + SendRateStepBytesPerSecond);
                        if (next != rate) ApplySendRate(endpoint, next);
                    }

                    if (!report) continue;
                    _log.Detail(LogTopic.Transport, "Relay " + endpoint.Id + " sending: " +
                        (outstanding / 1024) + " KB left at " + (goodput / 1024) + " KB/s (paced " +
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
