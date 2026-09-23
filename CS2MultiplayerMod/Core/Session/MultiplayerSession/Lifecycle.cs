using System;
using System.Net.Sockets;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        /// <summary>How long <see cref="StopWithNotice"/> may block for the farewell to go out.</summary>
        private const int GracefulCloseTimeoutMs = 750;

        public void StartHost(MultiplayerConfig config)
        {
            if (Role != SessionRole.None) throw new InvalidOperationException("A session is already active.");

            // Nothing may escape after Role is set, or the session is stuck half-started.
            try
            {
                StartHostCore(config);
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to host", ex));
            }
        }

        private void StartHostCore(MultiplayerConfig config)
        {
            if (config.Transport == TransportMode.SteamRelay)
            {
                StartRelayHost(config);
                return;
            }

            // Allowed but warned: private games over a forwarded port are the main use case.
            if (!config.LanOnly && string.IsNullOrEmpty(config.Password))
                _log.Warn(LogTopic.Session,
                    "Hosting PUBLICLY with NO PASSWORD: anyone who can reach port " + config.Port +
                    " can join and receive the city. Setting a password is strongly recommended.");

            _config = config;
            LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
            LocalPlayerId = HostPlayerId;
            Role = SessionRole.Host;

            EncryptionActive = false;
            _certificate = null;
            if (config.UseEncryption)
            {
                _certificate = TlsCertificate.TryCreateEphemeral(out string certError);
                if (_certificate == null)
                {
                    if (config.LanOnly)
                    {
                        _log.Warn(LogTopic.Session, "TLS unavailable on this runtime (" + certError +
                            "); continuing without TLS because the session is LAN-only. " +
                            "Clients must disable encryption too.");
                    }
                    else
                    {
                        Fault("Cannot host publicly: TLS is unavailable on this runtime (" + certError + ").");
                        return;
                    }
                }
                else
                {
                    EncryptionActive = true;
                }
            }

            if (!config.LanOnly)
                _log.Warn(LogTopic.Session,
                    "PUBLIC HOSTING ENABLED: your machine accepts connections from the internet " +
                    "on port " + config.Port + ". Keep the password strong and private.");

            var server = new TcpServerTransport(_log);
            _transport = server;
            try
            {
                server.Start(config.Port, config.LanOnly, _certificate);

                // LAN-only needs no forward; hosting never waits on it.
                if (!config.LanOnly)
                    _portForward = PortForward.Begin(_log, config.Port);

                SetStatus(SessionStatus.Connected, "Hosting on port " + config.Port +
                          (config.LanOnly ? " (LAN-only" : " (PUBLIC") +
                          (EncryptionActive ? ", TLS)" : ", PLAINTEXT)"));
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to host", ex));
            }
        }

        /// <summary>
        /// Relay host: nothing listens here, and the relay authenticates and encrypts every connection, so
        /// the direct path's exposure warnings and TLS do not apply.
        /// </summary>
        private void StartRelayHost(MultiplayerConfig config)
        {
            IRelayProvider relay = RelayProvider.Current;
            if (!RelayProvider.IsAvailable)
            {
                Fault("Cannot host over the Steam relay: " + RelayProvider.UnavailableReason +
                      " Switch the host connection to Direct and share your address and port instead.");
                return;
            }

            _config = config;
            LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
            LocalPlayerId = HostPlayerId;
            Role = SessionRole.Host;
            EncryptionActive = true;
            _certificate = null;

            try
            {
                _transport = relay.CreateHost(_log);
                SetStatus(SessionStatus.Connected, "Hosting over the Steam relay (join code " +
                                                   relay.LocalJoinCode + ")");
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to host over the Steam relay", ex));
            }
        }

        public void Join(MultiplayerConfig config)
        {
            if (Role != SessionRole.None) throw new InvalidOperationException("A session is already active.");

            // As in StartHost: a throw after Role is set becomes a clean Fault.
            try
            {
                if (config.Transport == TransportMode.SteamRelay)
                {
                    JoinOverRelay(config);
                    return;
                }

                _config = config;
                LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
                Role = SessionRole.Client;
                _challengeAnswered = false;
                _awaitingHostApproval = false;
                _hostSimulationSync = true;
                EncryptionActive = config.UseEncryption;

                var client = new TcpClientTransport(_log);
                _transport = client;
                SetStatus(SessionStatus.Connecting, "Connecting to " + config.HostAddress + ":" + config.Port +
                                                    (config.UseEncryption ? " (TLS)" : " (PLAINTEXT)"));
                client.Connect(config.HostAddress, config.Port, config.UseEncryption);
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to start joining", ex));
            }
        }

        private void JoinOverRelay(MultiplayerConfig config)
        {
            IRelayProvider relay = RelayProvider.Current;
            if (!RelayProvider.IsAvailable)
            {
                Fault("Cannot join over the Steam relay: " + RelayProvider.UnavailableReason +
                      " Ask the host for an address and port and use Direct Connection instead.");
                return;
            }

            if (string.IsNullOrEmpty(config.JoinCode))
            {
                Fault("Enter the host's join code first. They can read it off their Host screen, " +
                      "or switch to Direct Connection to join by address and port instead.");
                return;
            }

            // A short number parses as an id and would fail later as a relay timeout.
            if (!RelayProvider.LooksLikeJoinCode(config.JoinCode))
            {
                Fault("'" + config.JoinCode + "' is not a valid join code. A join code is 17 digits - " +
                      "check you copied all of it, or switch to Direct Connection to use an address and port.");
                return;
            }

            _config = config;
            LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
            Role = SessionRole.Client;
            _challengeAnswered = false;
            _awaitingHostApproval = false;
            _hostSimulationSync = true;
            EncryptionActive = true;

            _transport = relay.CreateClient(_log, config.JoinCode);
            SetStatus(SessionStatus.Connecting, "Connecting to " + config.JoinCode + " over the Steam relay");
        }

        private static string DescribeStartupFailure(string prefix, Exception ex)
        {
            return prefix + (ex is SocketException socket ? " [" + socket.SocketErrorCode + "]" : "") +
                   ": " + ex.Message;
        }

        /// <summary>
        /// Ends the session because this machine is leaving the shared city: sends peers a notice that it
        /// ended normally and flushes it before the world goes away.
        /// </summary>
        public void StopWithNotice(string reason)
        {
            if (Role == SessionRole.None) { Stop(); return; }

            if (Role == SessionRole.Host && Status == SessionStatus.Connected)
                BroadcastToAll(new DisconnectNoticeMessage(reason, graceful: true), ConnectionId.None);

            if (_transport != null)
            {
                try { _transport.ShutdownAfterFlush(GracefulCloseTimeoutMs); }
                catch (Exception ex) { _log.Warn(LogTopic.Session, "Graceful close failed (" + ex.Message + "); closing now."); }
            }

            Stop();
        }

        public void Stop() => Stop("Stopped");

        /// <summary>
        /// Local teardown that keeps a remote close reason for observers, so the game layer can say why the
        /// host world is closing.
        /// </summary>
        private void Stop(string detail)
        {
            if (_transport != null)
            {
                _transport.Shutdown();
                _transport.Dispose();
                _transport = null;
            }

            if (_certificate != null)
            {
                try { _certificate.Dispose(); } catch { /* ignore */ }
                _certificate = null;
            }

            if (_portForward != null)
            {
                try { _portForward.Dispose(); } catch { /* the router can expire it instead */ }
                _portForward = null;
            }

            _peers.Clear();
            _administrativeRemovals.Clear();
            _puntedConnections.Clear();
            _localCloseReasons.Clear();
            _hostBannedAddresses.Clear();
            _blobs.Clear();
            ClearOutgoingBlobs();
            _completedBlobTransfers.Clear();
            _blobTransferIds.Clear();
            ClearBlobProgress();
            _outgoingBlobActive = false;
            _outgoingBlobTotal = 0;
            _outgoingBlobSent = 0;
            Role = SessionRole.None;
            LocalPlayerId = 0;
            _nextPlayerId = HostPlayerId + 1;
            _awaitingHostApproval = false;
            _hostSimulationSync = true;
            EncryptionActive = false;
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            SetStatus(SessionStatus.Offline,
                string.IsNullOrWhiteSpace(detail) ? "The connection to the host closed." : detail);
        }
    }
}
