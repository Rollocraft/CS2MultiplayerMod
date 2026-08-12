namespace CS2MultiplayerMod.Core.Protocol
{
    public static class ProtocolConstants
    {
        /// <summary>
        /// Wire-format version. Bump when message layout changes to refuse handshake on mismatch.
        /// Current v36 adds names: command id 26 carries what a street, district, transport line or
        /// building is called, and city-state channel 17 carries the city's own name. A typed name is
        /// replicated as text; an untouched entity's name is a draw from its prefab's name list, made
        /// from a per-machine seed, so the draw itself has to travel or the same new road ends up
        /// with a different name on each machine. v35 carries
        /// the host of an owned relocation. Relocating an installed upgrade or
        /// sub-building from a building's upgrade list moves an owned entity, which no peer can
        /// look up as a free-standing object; the host's prefab and position identify it the same
        /// way an upgrade placement does. v34 normalizes the sender-local Permanent creation flag out of native object
        /// transactions, so every peer commits the batch through the same isolated apply lifecycle
        /// instead of retrying an impossible condition and dropping the edit. It also treats an
        /// empty DLC set as "owns no DLC", not as a compatibility wildcard. v33 carries each
        /// endpoint's committed elevation on a geometry-only net placement,
        /// so a span replayed without captured tool intent keeps the height it was built at instead
        /// of being re-derived against the receiver's surface (over water: the lakebed).
        /// v32 sends a stamped intersection as the inputs its tool had - asset-stamp prefab,
        /// one control point, and the tool seed - so the receiver regenerates the graph with the
        /// game's own generator. Replaying the finished definitions could not preserve the stamp's
        /// internal junctions: courses share a node only on an exact endpoint-position match.
        /// v31 carries source/destination net attachment anchors and stable object identity
        /// with relocation commands, so roadside objects and road-connected buildings re-run their
        /// complete move lifecycle on every peer. v30 carries zoning-block geometry and portable
        /// cell-state metadata so zoning is
        /// mapped by world position when road-generated block layouts differ between peers. v29
        /// marks a disconnect notice as graceful, so a host that simply left the game
        /// ends the session cleanly on every client instead of reporting a connection error.
        /// v28 carries the placing tool's random seed and control-point elevation with
        /// upgrade and relocation commands, so the receiver can re-run the game's own definition
        /// generator against its own geometry instead of resolving the sender's finished batch.
        /// v27 adds natural-disaster events: one command per disaster start, carrying the
        /// place/size/duration/strength the game resolves at creation, with timings as frame counts
        /// relative to the receiver's own clock. v26 adds a host-approval gate: after the automatic checks pass the host may
        /// hold a join and send a HandshakePending marker, admitting the player only once the
        /// host accepts by hand. v25 carries owner-buffer paths for building-owned objects, networks, and areas
        /// so rebuild, relocation, and upgrade removal target the same structural child without
        /// relying only on nearby geometry. v24 carries prefab-local placeholder-building
        /// attachments so a specialized facility, its visible building, and its draggable
        /// extractor/storage area remain one atomic object-tool operation. v23 carries
        /// owner-qualified extractor/storage area
        /// snapshots so draggable facility borders can be updated or repaired independently of
        /// their building. v22 adds a host disconnect notice so kicked players receive a clear
        /// reason before their connection closes. v21 carries complete transport-route topology:
        /// connected stop/owner identities,
        /// closed-route state, and stable route-number identity for edits/deletes. v20 carries
        /// rootless asset-stamp object-tool graphs so prebuilt intersections
        /// retain their exact shared-node topology and commit atomically. v19 makes world
        /// replacement an epoch-scoped pause/load/resume transaction and tags blob chunks with
        /// their transfer epoch. v18 adds entity visual-customization
        /// and savegame color-palette commands.
        /// v17 carries atomic native object-tool definition batches, portable owned-net connector
        /// identities, and host-authoritative tree stage/growth correction. v16 carries
        /// object random seed/tree age and service-upgrade random seed. v15
        /// carries native net-course endpoint/split/elevation intent and the source frame delta for
        /// deterministic terrain-brush replay. v14 replaced the single-stroke
        /// TerrainBrushCommand with a batched one carrying the terraforming tool prefab and each
        /// sample's complete applied brush state. v13 added the
        /// attach kind + parent node position to ObjectPlacementCommand, so net objects (roundabout
        /// islands) reattach on the receiver.
        /// See <see cref="Messages.HandshakeRequest"/> and version notes in doc/internals.
        /// </summary>
        public const int ProtocolVersion = 36;

        /// <summary>
        /// Hard cap on a single payload, guarding against corrupt length prefixes.
        /// This is the transport-level ceiling; each message type has a far smaller
        /// cap enforced by <see cref="MessageCodec"/>.
        /// </summary>
        public const int MaxPayloadBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Transport envelope cap for one simulation command. Large atomic building transactions
        /// remain below this while individual command codecs enforce their own tighter limits.
        /// </summary>
        public const int MaxSimulationCommandPayloadBytes = 320 * 1024;

        /// <summary>One blob slice on the wire. Also the per-chunk cap on receive.</summary>
        public const int BlobChunkBytes = 256 * 1024;

        /// <summary>Bytes of nonce in a handshake challenge.</summary>
        public const int ChallengeNonceBytes = 32;

        /// <summary>Bytes of an HMAC-SHA256 password proof.</summary>
        public const int PasswordProofBytes = 32;

        /// <summary>Most DLC entries a handshake may carry (the catalogue is ~2 dozen).</summary>
        public const int MaxDlcEntries = 64;

        /// <summary>Length cap for one DLC name in a handshake.</summary>
        public const int MaxDlcNameLength = 64;
    }
}
