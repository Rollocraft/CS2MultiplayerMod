using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CS2MultiplayerMod.Core.Protocol;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // The wire itself: length-prefixed frames out through Steam's send queue, and frames in,
    // reassembled per peer and handed up as transport events.
    public sealed partial class SteamRelayTransport
    {
        // ---- sending --------------------------------------------------------------

        /// <summary>
        /// Queue a payload. Frames go into the endpoint's outbox and are handed to Steam as
        /// fast as it will take them, which is the whole point: the session hands a world
        /// over as ~200 chunks in a single frame, and a Steam connection's send buffer is
        /// bounded, so pushing them all straight in earns k_EResultLimitExceeded and drops
        /// the peer. TCP never showed this because its transport queues without limit.
        /// </summary>
        public void Send(ConnectionId target, byte[] payload)
        {
            if (payload == null) return;

            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(target.Value, out endpoint)) return;
            }

            // Header carries the whole payload's length so the receiver can rejoin the
            // frames; Steam delivers them reliably and in order, so no other bookkeeping
            // is needed on the wire.
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
            byte[] frame;
            while (endpoint.TryPeek(out frame))
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
                // NoNagle: every frame we hand over is already a complete unit, so there is
                // nothing to coalesce and the delay would only sit between pumps.
                long messageNumber;
                EResult result = SteamNetworkingSockets.SendMessageToConnection(
                    endpoint.Handle, pin.AddrOfPinnedObject(), (uint)frame.Length,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, out messageNumber);

                if (result == EResult.k_EResultOK) return SendOutcome.Sent;

                // Not a failure: the connection is healthy and simply has as much queued as
                // it will hold. Everything else means the peer is gone or the message was
                // rejected, and a half-delivered payload can never be completed.
                if (result == EResult.k_EResultLimitExceeded) return SendOutcome.Backpressure;

                _log.Warn("Steam relay send to " + endpoint.Id + " failed (" + result + "); dropping the connection. " +
                          DescribeConnection(endpoint));
                Close(endpoint, "relay send failed: " + result, linger: false);
                return SendOutcome.Failed;
            }
            catch (Exception ex)
            {
                _log.Warn("Steam relay send to " + endpoint.Id + " threw (" + ex.Message + "); dropping the connection.");
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
                // Drain the outbox first: a world transfer is carried entirely by these
                // per-frame pumps once the initial burst filled Steam's buffer.
                PumpAllSends();
                Govern();
                Receive();
            }

            int count = 0;
            TransportEvent evt;
            while (_events.TryDequeue(out evt))
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
                    _log.Warn("Steam relay receive failed: " + ex.Message);
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
                        Accept(message.m_conn.m_HSteamNetConnection, frame);
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

        /// <summary>Rejoin frames into whole payloads and publish each completed one.</summary>
        private void Accept(uint handle, byte[] frame)
        {
            Endpoint endpoint = Find(handle);
            if (endpoint == null) return;

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
                        Enqueue(TransportEvent.Data(endpoint.Id, Array.Empty<byte>()));
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
                    Enqueue(TransportEvent.Data(endpoint.Id, payload));
                }
            }
        }
    }
}
