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
        /// <summary>
        /// How long <see cref="StopWithNotice"/> may block the game thread waiting for the
        /// farewell to reach the peers. Long enough for a small message on a live socket,
        /// short enough that a wedged connection cannot noticeably delay the shutdown the
        /// player asked for.
        /// </summary>
        private const int GracefulCloseTimeoutMs = 750;


        public void StartHost(MultiplayerConfig config)
        {
            if (Role != SessionRole.None) throw new InvalidOperationException("A session is already active.");

            // Nothing below may escape: an exception thrown after Role is set would
            // leave a half-started session ("a session is already active" forever) —
            // exactly what happened when TLS setup crashed on the game's runtime.
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

            // Public exposure without a password lets anyone who finds the port walk
            // into the city. Said loudly, but allowed — private games with trusted
            // friends over a forwarded port are this mod's main use case.
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
                string certError;
                _certificate = TlsCertificate.TryCreateEphemeral(out certError);
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

                // A LAN-only session is reachable without the router's help, so there is
                // nothing to ask for. Hosting never waits on the answer: the listener is
                // already accepting, and a forward only adds reach from outside.
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
        /// Host over the relay. Nothing listens on this machine, so the exposure warnings
        /// and the TLS setup that guard the direct path have nothing to protect here: the
        /// relay authenticates and encrypts every connection itself, and a peer can only
        /// arrive by knowing the join code.
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

            // Same containment as StartHost: a throw after Role is set must become a
            // clean Fault (which resets the session), never a stuck half-join.
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

            // Checked here rather than left to the dial: a short number still parses as an
            // id and would fail much later as an unexplained relay timeout.
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
            EncryptionActive = true;

            _transport = relay.CreateClient(_log, config.JoinCode);
            SetStatus(SessionStatus.Connecting, "Connecting to " + config.JoinCode + " over the Steam relay");
        }

        private static string DescribeStartupFailure(string prefix, Exception ex)
        {
            var socket = ex as SocketException;
            return prefix + (socket != null ? " [" + socket.SocketErrorCode + "]" : "") +
                   ": " + ex.Message;
        }

        /// <summary>
        /// End the session because this machine is leaving the shared city (the player quit
        /// the game, returned to the main menu, or loaded another world).
        ///
        /// A plain <see cref="Stop"/> drops the sockets, which peers only ever see as an
        /// anonymous "remote closed". A host owes its clients better than that: the notice
        /// says the session ended normally, and the flush is what actually gets it onto the
        /// wire before the process (or the world) goes away.
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

        public void Stop()
        {
            Stop("Stopped");
        }

        /// <summary>
        /// Tear down locally while preserving a remote close reason for observers. The
        /// public no-argument Stop keeps its historical "Stopped" detail; clients which
        /// lose their host use this overload so the game layer can explain why it is
        /// closing the downloaded host world.
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
            _hostBannedAddresses.Clear();
            IsLobbyLocked = false;
            _blobs.Clear();
            _blobTransferIds.Clear();
            ClearBlobProgress();
            _outgoingBlobActive = false;
            _outgoingBlobTotal = 0;
            _outgoingBlobSent = 0;
            Role = SessionRole.None;
            LocalPlayerId = 0;
            _nextPlayerId = HostPlayerId + 1;
            _awaitingHostApproval = false;
            EncryptionActive = false;
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            SetStatus(SessionStatus.Offline,
                string.IsNullOrWhiteSpace(detail) ? "The connection to the host closed." : detail);
        }

    }
}
