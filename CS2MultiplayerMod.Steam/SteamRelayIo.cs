using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Framing: length-prefixed frames out through Steam's send queue, reassembled per peer on the way in.
    public sealed partial class SteamRelayTransport
    {
        // ---- sending --------------------------------------------------------------

        /// <summary>
        /// Queues into the endpoint's outbox, fed to Steam as it takes them: a world arrives as ~200 chunks
        /// in one frame, and overfilling Steam's bounded buffer returns k_EResultLimitExceeded and drops the
        /// peer.
        /// </summary>
        public void Send(ConnectionId target, byte[] payload)
        {
            if (payload == null) return;

            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(target.Value, out endpoint)) return;
            }

            // The header carries the whole payload length; Steam delivers reliably and in order.
            int firstBody = Math.Min(payload.Length, FrameBytes - FrameHeaderBytes);
            var first = new byte[FrameHeaderBytes + firstBody];
            first[0] = (byte)payload.Length;
            first[1] = (byte)(payload.Length >> 8);
            first[2] = (byte)(payload.Length >> 16);
            first[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, first, FrameHeaderBytes, firstBody);
            endpoint.Enqueue(first);

            int offset = firstBody;
            while (offset < payload.Length)
            {
                int size = Math.Min(FrameBytes, payload.Length - offset);
                var frame = new byte[size];
                Buffer.BlockCopy(payload, offset, frame, 0, size);
                endpoint.Enqueue(frame);
                offset += size;
            }

            // Push what fits right now; Poll carries the rest over the coming frames.
            PumpSends(endpoint);
        }

        /// <summary>Hand queued frames to Steam until it pushes back or the outbox empties.</summary>
        private void PumpSends(Endpoint endpoint)
        {
            while (endpoint.TryPeek(out byte[] frame))
            {
                SendOutcome outcome = SendFrame(endpoint, frame);
                if (outcome == SendOutcome.Backpressure) return; // retry next frame
                if (outcome == SendOutcome.Failed) return;       // Close already ran
                endpoint.Commit(frame.Length);
            }
        }

        private void PumpAllSends()
        {
            Endpoint[] open;
            lock (_gate)
            {
                if (_byId.Count == 0) return;
                open = new Endpoint[_byId.Count];
                _byId.Values.CopyTo(open, 0);
            }
            foreach (Endpoint endpoint in open) PumpSends(endpoint);
        }

        private enum SendOutcome
        {
            Sent,

            /// <summary>Steam's buffer is full. The frame stays queued and is retried.</summary>
            Backpressure,

            Failed,
        }

        private SendOutcome SendFrame(Endpoint endpoint, byte[] frame)
        {
            GCHandle pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                // NoNagle: every frame is already a complete unit.
                EResult result = SteamNetworkingSockets.SendMessageToConnection(
                    endpoint.Handle, pin.AddrOfPinnedObject(), (uint)frame.Length,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, out long messageNumber);

                if (result == EResult.k_EResultOK) return SendOutcome.Sent;

                // Full, not failed. Any other error means the peer is gone or the message was refused.
                if (result == EResult.k_EResultLimitExceeded) return SendOutcome.Backpressure;

                _log.Warn(LogTopic.Transport, "Steam relay send to " + endpoint.Id + " failed (" +
                    result + "); dropping the connection. " + DescribeConnection(endpoint));
                Close(endpoint, "relay send failed: " + result, linger: false);
                return SendOutcome.Failed;
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "Steam relay send to " + endpoint.Id + " threw (" +
                    ex.Message + "); dropping the connection.");
                Close(endpoint, "relay send error", linger: false);
                return SendOutcome.Failed;
            }
            finally
            {
                pin.Free();
            }
        }

        // ---- receiving ------------------------------------------------------------

        public int Poll(IList<TransportEvent> sink)
        {
            if (_active)
            {
                // Drain the outbox first: after the initial burst, transfers move only through these pumps.
                PumpAllSends();
                Govern();
                Receive();
            }

            int count = 0;
            while (_events.TryDequeue(out TransportEvent evt))
            {
                sink.Add(evt);
                count++;
            }
            return count;
        }

        private void Receive()
        {
            while (true)
            {
                int received;
                try
                {
                    received = _isHost
                        ? SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _receiveBuffer, ReceiveBatch)
                        : ReceiveOnClientConnection();
                }
                catch (Exception ex)
                {
                    _log.Warn(LogTopic.Transport, "Steam relay receive failed: " + ex.Message);
                    return;
                }

                if (received <= 0) return;

                for (int i = 0; i < received; i++)
                {
                    IntPtr pointer = _receiveBuffer[i];
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        var frame = new byte[message.m_cbSize];
                        if (message.m_cbSize > 0)
                            Marshal.Copy(message.m_pData, frame, 0, message.m_cbSize);
                        Accept(message.m_conn.m_HSteamNetConnection, frame,
                            ArrivalOf(message.m_usecTimeReceived));
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(pointer);
                    }
                }

                if (received < ReceiveBatch) return;
            }
        }

        private int ReceiveOnClientConnection()
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(ConnectionId.Server.Value, out endpoint)) return 0;
            }
            return SteamNetworkingSockets.ReceiveMessagesOnConnection(endpoint.Handle, _receiveBuffer, ReceiveBatch);
        }

        /// <summary>
        /// Steam's arrival time on the mod's event clock. This transport reads only inside Poll on the game
        /// thread, so without it a stalled frame's backlog would meter as one instant. The clocks differ by a
        /// fixed offset; the smallest observed offset is the best estimate.
        /// </summary>
        private long ArrivalOf(SteamNetworkingMicroseconds usecTimeReceived)
        {
            long steamMs = (long)usecTimeReceived / 1000;
            long now = MonotonicClock.NowMs;

            long candidate = now - steamMs;
            if (!_steamClockAligned || candidate < _steamClockOffsetMs)
            {
                _steamClockAligned = true;
                _steamClockOffsetMs = candidate;
            }

            long arrived = steamMs + _steamClockOffsetMs;
            return arrived > now ? now : arrived;
        }

        /// <summary>Rejoin frames into whole payloads and publish each completed one.</summary>
        private void Accept(uint handle, byte[] frame, long receivedAtMs)
        {
            Endpoint endpoint = Find(handle);
            if (endpoint == null) return;
            if (receivedAtMs > endpoint.LastInboundMs) endpoint.LastInboundMs = receivedAtMs;

            int offset = 0;
            while (offset < frame.Length)
            {
                if (endpoint.Incoming == null)
                {
                    if (frame.Length - offset < FrameHeaderBytes)
                    {
                        Close(endpoint, "truncated relay frame header", linger: false);
                        return;
                    }

                    int total = frame[offset]
                                | (frame[offset + 1] << 8)
                                | (frame[offset + 2] << 16)
                                | (frame[offset + 3] << 24);
                    offset += FrameHeaderBytes;

                    if (total < 0 || total > ProtocolConstants.MaxPayloadBytes)
                    {
                        // Refuse to allocate on a peer's say-so.
                        Close(endpoint, "relay payload of " + total + " bytes exceeds the protocol limit", linger: false);
                        return;
                    }

                    if (total == 0)
                    {
                        Enqueue(TransportEvent.Data(endpoint.Id, Array.Empty<byte>(), receivedAtMs));
                        continue;
                    }

                    endpoint.Incoming = new byte[total];
                    endpoint.Filled = 0;
                }

                int wanted = endpoint.Incoming.Length - endpoint.Filled;
                int available = Math.Min(wanted, frame.Length - offset);
                Buffer.BlockCopy(frame, offset, endpoint.Incoming, endpoint.Filled, available);
                endpoint.Filled += available;
                offset += available;

                if (endpoint.Filled == endpoint.Incoming.Length)
                {
                    byte[] payload = endpoint.Incoming;
                    endpoint.Incoming = null;
                    endpoint.Filled = 0;
                    Enqueue(TransportEvent.Data(endpoint.Id, payload, receivedAtMs));
                }
            }
        }
    }
}
