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
        /// Send a blob to a single peer - auto-ships map to just-joined client
        /// without re-sending to everyone already in the session.
        /// </summary>
        public void SendBlobTo(ConnectionId target, string channel, byte[] data) =>
            ChunkAndSend(channel, 0, data, target);

        /// <summary>Send an epoch-tagged blob to one peer.</summary>
        public void SendBlobTo(ConnectionId target, string channel, long transferId, byte[] data) =>
            ChunkAndSend(channel, transferId, data, target);

        private void ChunkAndSend(string channel, long transferId, byte[] data, ConnectionId target)
        {
            if (_transport == null || Status != SessionStatus.Connected || data == null) return;

            // Blobs flow host → client only; a client has no business streaming one.
            if (Role != SessionRole.Host)
            {
                _log.Warn("Ignoring outgoing blob '" + channel + "': only the host streams blobs.");
                return;
            }
            if ((transferId > 0 && (!_worldSyncSuspended || transferId != _worldSyncEpoch)) ||
                (_worldSyncSuspended && transferId == 0))
            {
                _log.Warn("Ignoring outgoing blob '" + channel + "' transfer " + transferId +
                          ": it does not match the active world-sync epoch.");
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

                var message = new BlobChunkMessage(channel, transferId, total, last, chunk);
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
            if (chunk.TransferId < 0 ||
                (_worldSyncSuspended && chunk.TransferId != _worldSyncEpoch) ||
                (!_worldSyncSuspended && chunk.TransferId != 0))
            {
                _log.Warn("Dropping blob '" + (chunk.Channel ?? "<null>") + "' transfer " +
                          chunk.TransferId + ": it does not match active world-sync epoch " +
                          (_worldSyncSuspended ? _worldSyncEpoch.ToString() : "none") + ".");
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
                _blobTransferIds.Remove(chunk.Channel);
                ClearBlobProgress();
                return;
            }

            BlobReassembler reassembler;
            long activeTransferId;
            if (_blobs.TryGetValue(chunk.Channel, out reassembler) &&
                (!_blobTransferIds.TryGetValue(chunk.Channel, out activeTransferId) ||
                 activeTransferId != chunk.TransferId))
            {
                _log.Warn("Replacing incomplete blob '" + chunk.Channel + "' transfer " +
                          activeTransferId + " with transfer " + chunk.TransferId + ".");
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                reassembler = null;
            }
            if (!_blobs.TryGetValue(chunk.Channel, out reassembler))
            {
                if (_blobs.Count >= MaxActiveBlobs)
                {
                    _log.Warn("[security] Dropping blob '" + chunk.Channel + "': too many active transfers.");
                    return;
                }
                reassembler = new BlobReassembler(chunk.TotalBytes, nowUnixMs);
                _blobs[chunk.Channel] = reassembler;
                _blobTransferIds[chunk.Channel] = chunk.TransferId;
                _log.Info("Receiving blob '" + chunk.Channel + "' transfer " + chunk.TransferId +
                          ": expecting " + chunk.TotalBytes + " bytes.");
            }

            try
            {
                reassembler.Append(chunk.TotalBytes, chunk.Data, nowUnixMs);

                IncomingBlobChannel = chunk.Channel;
                IncomingBlobTransferId = chunk.TransferId;
                IncomingBlobReceived = reassembler.ReceivedBytes;
                IncomingBlobTotal = reassembler.ExpectedBytes;

                if (!chunk.Last) return;

                // Completion verifies ReceivedBytes == TotalBytes exactly; a short or
                // overlong transfer never reaches the game layer.
                byte[] data = reassembler.Complete();
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
                ClearBlobProgress();
                NotifyBlob(chunk.Channel, chunk.TransferId, data);
            }
            catch (ProtocolException ex)
            {
                _log.Warn("[security] Dropping blob '" + chunk.Channel + "': " + ex.Message);
                _blobs.Remove(chunk.Channel);
                _blobTransferIds.Remove(chunk.Channel);
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
                _blobTransferIds.Remove(channel);
            }
            ClearBlobProgress();
        }

        private void ClearBlobProgress()
        {
            IncomingBlobChannel = null;
            IncomingBlobTransferId = 0;
            IncomingBlobReceived = 0;
            IncomingBlobTotal = 0;
        }

    }
}
