using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        /// <summary>
        /// Send a large named byte stream to all peers, split into chunks. Used by map
        /// sync to ship the host's savegame so both players start on the same city.
        /// </summary>
        public void SendBlob(string channel, byte[] data) => ChunkAndSend(channel, data, ConnectionId.None);

        /// <summary>
        /// Send a blob to a single peer - auto-ships map to just-joined client
        /// without re-sending to everyone already in the session.
        /// </summary>
        public void SendBlobTo(ConnectionId target, string channel, byte[] data) => ChunkAndSend(channel, data, target);

        private void ChunkAndSend(string channel, byte[] data, ConnectionId target)
        {
            if (_transport == null || Status != SessionStatus.Connected || data == null) return;

            // Blobs flow host → client only; a client has no business streaming one.
            if (Role != SessionRole.Host)
            {
                _log.Warn("Ignoring outgoing blob '" + channel + "': only the host streams blobs.");
                return;
            }

            int total = data.Length;
            int chunkBytes = ProtocolConstants.BlobChunkBytes;
            int chunkCount = (total + chunkBytes - 1) / chunkBytes;
            _log.Info("Sending blob '" + channel + "': " + total + " bytes in " + chunkCount + " chunk(s) to " +
                      (target.IsNone ? "all peers" : target.ToString()) + ".");

            int offset = 0;
            do
            {
                int size = total - offset;
                if (size > chunkBytes) size = chunkBytes;

                var chunk = new byte[size];
                Array.Copy(data, offset, chunk, 0, size);
                offset += size;
                bool last = offset >= total;

                var message = new BlobChunkMessage(channel, total, last, chunk);
                if (!target.IsNone)
                    SendTo(target, message);
                else
                    BroadcastToAll(message, ConnectionId.None);
            }
            while (offset < total);

            // The loop above is non-blocking, so by now the whole blob sits in the send
            // queue and barely any has gone out — snapshot that backlog as the "to send"
            // total so Update() can report drain progress to the host.
            _outgoingBlobTotal = _transport.PendingSendBytes;
            _outgoingBlobSent = 0;
            _outgoingBlobActive = _outgoingBlobTotal > 0;

            _log.Info("Finished queueing blob '" + channel + "' (" + total + " bytes, " +
                      chunkCount + " chunk(s)) to " + (target.IsNone ? "all peers" : target.ToString()) + ".");
        }

        private void HandleBlobChunk(ConnectionId from, Peer peer, BlobChunkMessage chunk, long nowUnixMs)
        {
            // Blobs flow host → client only. The "map" channel is auto-LOADED as a
            // savegame on arrival, so accepting blobs from clients would let any joiner
            // replace the host's running city.
            if (Role == SessionRole.Host)
            {
                Punt(from, peer, "client attempted to stream a blob", "BlobChunk");
                return;
            }

            // Only channels the game layer registered are expected — and each carries
            // its own size ceiling (a savegame cap is far below the 512 MiB of old).
            int maxBytes;
            if (string.IsNullOrEmpty(chunk.Channel) ||
                !_allowedBlobChannels.TryGetValue(chunk.Channel, out maxBytes))
            {
                _log.Warn("[security] Dropping blob chunk on unregistered channel '" +
                          (chunk.Channel ?? "<null>") + "'.");
                return;
            }

            if (chunk.TotalBytes <= 0 || chunk.TotalBytes > maxBytes)
            {
                _log.Warn("[security] Dropping blob '" + chunk.Channel + "': announced " +
                          chunk.TotalBytes + " bytes is outside (0, " + maxBytes + "].");
                _blobs.Remove(chunk.Channel);
                ClearBlobProgress();
                return;
            }

            BlobReassembler reassembler;
            if (!_blobs.TryGetValue(chunk.Channel, out reassembler))
            {
                if (_blobs.Count >= MaxActiveBlobs)
                {
                    _log.Warn("[security] Dropping blob '" + chunk.Channel + "': too many active transfers.");
                    return;
                }
                reassembler = new BlobReassembler(chunk.TotalBytes, nowUnixMs);
                _blobs[chunk.Channel] = reassembler;
                _log.Info("Receiving blob '" + chunk.Channel + "': expecting " + chunk.TotalBytes + " bytes.");
            }

            try
            {
                reassembler.Append(chunk.TotalBytes, chunk.Data, nowUnixMs);

                IncomingBlobChannel = chunk.Channel;
                IncomingBlobReceived = reassembler.ReceivedBytes;
                IncomingBlobTotal = reassembler.ExpectedBytes;

                if (!chunk.Last) return;

                // Completion verifies ReceivedBytes == TotalBytes exactly; a short or
                // overlong transfer never reaches the game layer.
                byte[] data = reassembler.Complete();
                _blobs.Remove(chunk.Channel);
                ClearBlobProgress();
                NotifyBlob(chunk.Channel, data);
            }
            catch (ProtocolException ex)
            {
                _log.Warn("[security] Dropping blob '" + chunk.Channel + "': " + ex.Message);
                _blobs.Remove(chunk.Channel);
                ClearBlobProgress();
            }
        }

        /// <summary>Abandon transfers that stopped making progress (sender died or stalls on purpose).</summary>
        private void SweepStalledBlobs(long nowUnixMs)
        {
            if (_blobs.Count == 0 || nowUnixMs - _lastBlobSweepMs < 5000) return;
            _lastBlobSweepMs = nowUnixMs;

            List<string> stalled = null;
            foreach (var pair in _blobs)
                if (nowUnixMs - pair.Value.LastChunkAtMs > BlobStallTimeoutMs)
                    (stalled ?? (stalled = new List<string>())).Add(pair.Key);

            if (stalled == null) return;
            foreach (string channel in stalled)
            {
                _log.Warn("Abandoning stalled blob '" + channel + "' (no chunk for " +
                          (BlobStallTimeoutMs / 1000) + " s).");
                _blobs.Remove(channel);
            }
            ClearBlobProgress();
        }

        private void ClearBlobProgress()
        {
            IncomingBlobChannel = null;
            IncomingBlobReceived = 0;
            IncomingBlobTotal = 0;
        }

    }
}
