using System;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// A property by prefab name plus world anchor, shared by rent, occupancy, company and growable sync;
    /// entity ids are machine-local.
    /// </summary>
    public struct PropertyIdentity : IEquatable<PropertyIdentity>
    {
        public readonly string PrefabName;
        public readonly float AnchorX;
        public readonly float AnchorY;
        public readonly float AnchorZ;

        public PropertyIdentity(string prefabName, float anchorX, float anchorY, float anchorZ)
        {
            PrefabName = prefabName;
            AnchorX = anchorX;
            AnchorY = anchorY;
            AnchorZ = anchorZ;
        }

        public bool Equals(PropertyIdentity other) =>
            string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal) &&
            AnchorX.Equals(other.AnchorX) && AnchorY.Equals(other.AnchorY) &&
            AnchorZ.Equals(other.AnchorZ);

        public override bool Equals(object obj) => obj is PropertyIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PrefabName != null ? PrefabName.GetHashCode() : 0;
                hash = hash * 397 ^ AnchorX.GetHashCode();
                hash = hash * 397 ^ AnchorY.GetHashCode();
                return hash * 397 ^ AnchorZ.GetHashCode();
            }
        }
    }
}
