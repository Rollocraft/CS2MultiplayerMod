namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Answer to <see cref="HandshakeChallenge"/>: protocol, builds, sorted sync-relevant
    /// <see cref="DlcList"/>, the other live mods in <see cref="ModManifest"/> and <see cref="PasswordProof"/> =
    /// HMAC-SHA256(password, nonce | channel binding).
    /// </summary>
    public sealed class HandshakeRequest : INetMessage
    {
        public int ProtocolVersion;
        public string ModVersion;
        public string GameVersion;
        public string PlayerName;
        public byte[] PasswordProof;
        public string[] DlcList;
        /// <summary>Source commit of the sender's build; logged, never compared.</summary>
        public string BuildId;
        /// <summary>Sorted <see cref="ModManifestEntry"/> lines for every other live mod.</summary>
        public string[] ModManifest;

        public HandshakeRequest() { }

        public HandshakeRequest(int protocolVersion, string modVersion, string gameVersion,
                                string playerName, byte[] passwordProof, string[] dlcList = null,
                                string buildId = null, string[] modManifest = null)
        {
            ProtocolVersion = protocolVersion;
            ModVersion = modVersion;
            GameVersion = gameVersion;
            PlayerName = playerName;
            PasswordProof = passwordProof ?? System.Array.Empty<byte>();
            DlcList = dlcList ?? System.Array.Empty<string>();
            BuildId = buildId ?? string.Empty;
            ModManifest = modManifest ?? System.Array.Empty<string>();
        }

        public MessageType Type => MessageType.HandshakeRequest;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(ProtocolVersion);
            writer.WriteString(ModVersion);
            writer.WriteString(GameVersion);
            writer.WriteString(PlayerName);
            writer.WriteInt(PasswordProof != null ? PasswordProof.Length : 0);
            if (PasswordProof != null && PasswordProof.Length > 0)
                writer.WriteBytes(PasswordProof, 0, PasswordProof.Length);

            int dlcCount = DlcList != null ? DlcList.Length : 0;
            if (dlcCount > ProtocolConstants.MaxDlcEntries) dlcCount = ProtocolConstants.MaxDlcEntries;
            writer.WriteInt(dlcCount);
            for (int i = 0; i < dlcCount; i++)
                writer.WriteString(DlcList[i] ?? string.Empty);

            writer.WriteString(BuildId ?? string.Empty);
            int modCount = ModManifest != null ? ModManifest.Length : 0;
            if (modCount > ProtocolConstants.MaxModManifestEntries) modCount = ProtocolConstants.MaxModManifestEntries;
            writer.WriteInt(modCount);
            for (int i = 0; i < modCount; i++)
                writer.WriteString(ModManifest[i] ?? string.Empty);
        }

        public void Read(NetworkReader reader)
        {
            ProtocolVersion = reader.ReadInt();
            ModVersion = reader.ReadString();
            GameVersion = reader.ReadString();
            PlayerName = reader.ReadString();
            int length = reader.ReadInt();
            if (length < 0 || length > 64)
                throw new ProtocolException("Implausible proof length: " + length + ".");
            PasswordProof = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();

            int dlcCount = reader.ReadInt();
            if (dlcCount < 0 || dlcCount > ProtocolConstants.MaxDlcEntries)
                throw new ProtocolException("Implausible DLC count: " + dlcCount + ".");
            DlcList = dlcCount > 0 ? new string[dlcCount] : System.Array.Empty<string>();
            for (int i = 0; i < dlcCount; i++)
            {
                // DLC names reach reject messages and logs.
                DlcList[i] = WireGuard.SanitizeText(reader.ReadString(), ProtocolConstants.MaxDlcNameLength);
            }

            BuildId = WireGuard.SanitizeText(reader.ReadString(), ProtocolConstants.MaxBuildIdLength);
            int modCount = reader.ReadInt();
            if (modCount < 0 || modCount > ProtocolConstants.MaxModManifestEntries)
                throw new ProtocolException("Implausible mod count: " + modCount + ".");
            ModManifest = modCount > 0 ? new string[modCount] : System.Array.Empty<string>();
            for (int i = 0; i < modCount; i++)
                ModManifest[i] = WireGuard.SanitizeText(reader.ReadString(),
                    ProtocolConstants.MaxModManifestEntryLength);
        }
    }
}
