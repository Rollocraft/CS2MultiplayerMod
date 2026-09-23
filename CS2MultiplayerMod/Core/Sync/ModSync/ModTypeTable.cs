using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>
    /// The session's replicated types in host order, referenced by index; clients bind to those indices
    /// and report what they cannot bind.
    /// </summary>
    public sealed class ModTypeTable : IModTypeLookup
    {
        /// <summary>Types in one session. Far above anything measured; a table past this is refused.</summary>
        public const int MaxTypes = 512;

        private readonly List<ModTypeDescriptor> _byIndex = new List<ModTypeDescriptor>();
        private readonly Dictionary<string, int> _byKey =
            new Dictionary<string, int>(System.StringComparer.Ordinal);

        public int Count => _byIndex.Count;

        public ModTypeDescriptor ByIndex(int index) => _byIndex[index];

        public bool TryIndexOf(string key, out int index) => _byKey.TryGetValue(key, out index);

        /// <summary>Appends a type and returns its index, or the existing index if it is already in.</summary>
        public int Add(ModTypeDescriptor descriptor)
        {
            if (_byKey.TryGetValue(descriptor.Key, out int existing)) return existing;
            int index = _byIndex.Count;
            _byIndex.Add(descriptor);
            _byKey.Add(descriptor.Key, index);
            return index;
        }

        public IEnumerable<ModTypeDescriptor> All => _byIndex;

        public void Write(NetworkWriter writer)
        {
            writer.WriteShort((short)_byIndex.Count);
            for (int i = 0; i < _byIndex.Count; i++) _byIndex[i].Write(writer);
        }

        public static ModTypeTable Read(NetworkReader reader)
        {
            int count = reader.ReadShort();
            if (count < 0 || count > MaxTypes)
                throw new ProtocolException("Mod type table declares " + count + " types.");

            var table = new ModTypeTable();
            for (int i = 0; i < count; i++)
            {
                ModTypeDescriptor descriptor = ModTypeDescriptor.Read(reader);

                // A duplicate key would shift every later index on one side only.
                if (table._byKey.TryGetValue(descriptor.Key, out int existing))
                    throw new ProtocolException("Mod type table repeats " + descriptor.DisplayName + ".");
                table.Add(descriptor);
            }
            return table;
        }
    }
}
