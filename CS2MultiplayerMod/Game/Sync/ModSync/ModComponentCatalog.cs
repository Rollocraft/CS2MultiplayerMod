using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>One discovered type: what it is called, how to read it, and how it travels.</summary>
    internal sealed class ModCatalogEntry
    {
        public Type Type;
        public ModTypeDescriptor Descriptor;
        public ModTypeAccessor Accessor;
    }

    /// <summary>
    /// Every component and buffer type owned by another mod that holds durable state, found from the
    /// engine's type registry. Filters: not declared by the game, engine or this mod, and saved by the
    /// runtime (per-frame previews and overlays are not).
    /// </summary>
    internal sealed class ModComponentCatalog
    {
        private readonly Dictionary<string, ModCatalogEntry> _byKey =
            new Dictionary<string, ModCatalogEntry>(StringComparer.Ordinal);

        /// <summary>Entries in a stable order, so two machines list the same types the same way.</summary>
        public readonly List<ModCatalogEntry> Entries = new List<ModCatalogEntry>();

        /// <summary>Types that belong to another mod but are not replicated, each with its reason.</summary>
        public readonly List<string> Exclusions = new List<string>();

        /// <summary>How many third-party types were seen in total, replicated or not.</summary>
        public int ThirdPartyTypeCount;

        /// <summary>
        /// Registered type count when built. A mod's types register only when its systems first touch
        /// them, so a larger count means the catalogue may be missing a mod.
        /// </summary>
        public int TypeCountAtBuild;

        /// <summary>
        /// Same replicated set as <paramref name="other"/>. The type count moves on any registration, so
        /// the session table is only discarded when this is false.
        /// </summary>
        public bool ReplicatesSameAs(ModComponentCatalog other)
        {
            if (other == null || other.Entries.Count != Entries.Count) return false;
            for (int i = 0; i < Entries.Count; i++)
            {
                ModTypeDescriptor mine = Entries[i].Descriptor;
                ModTypeDescriptor theirs = other.Entries[i].Descriptor;
                if (mine.Key != theirs.Key || mine.Fingerprint != theirs.Fingerprint) return false;
            }
            return true;
        }

        public bool TryGet(string key, out ModCatalogEntry entry) => _byKey.TryGetValue(key, out entry);

        public static ModComponentCatalog Build()
        {
            var catalog = new ModComponentCatalog();
            Assembly self = typeof(ModComponentCatalog).Assembly;

            catalog.TypeCountAtBuild = TypeManager.GetTypeCount();

            // AllTypes reads in place; GetAllTypes copies the whole registry.
            foreach (TypeManager.TypeInfo info in TypeManager.AllTypes)
            {
                Type type = SafeType(info);
                if (type == null) continue;
                if (type.Assembly == self || !IsThirdParty(type.Assembly)) continue;

                catalog.ThirdPartyTypeCount++;

                string key = ModTypeDescriptor.MakeKey(type.Assembly.GetName().Name, type.FullName);
                ModCatalogEntry entry = Accept(type, info, key, out string reason);
                if (entry == null)
                {
                    catalog.Exclusions.Add(Short(type) + " - " + reason);
                    continue;
                }

                if (catalog._byKey.ContainsKey(key)) continue;
                catalog._byKey.Add(key, entry);
                catalog.Entries.Add(entry);
            }

            catalog.Entries.Sort(CompareByKey);
            return catalog;
        }

        private static int CompareByKey(ModCatalogEntry left, ModCatalogEntry right) =>
            string.CompareOrdinal(left.Descriptor.Key, right.Descriptor.Key);

        private static ModCatalogEntry Accept(Type type, TypeManager.TypeInfo info, string key,
            out string reason)
        {
            reason = null;

            if (info.Category != TypeManager.TypeCategory.ComponentData &&
                info.Category != TypeManager.TypeCategory.BufferData)
            {
                reason = "is a " + info.Category;
                return null;
            }

            if (info.TypeIndex.IsManagedType)
            {
                reason = "is a managed component";
                return null;
            }

            // Blob, asset and Unity object references are process-local and cannot be translated.
            if (info.HasBlobAssetRefs) { reason = "holds blob references"; return null; }
            if (info.HasWeakAssetRefs) { reason = "holds asset references"; return null; }
            if (info.HasUnityObjectRefs) { reason = "holds engine object references"; return null; }

            if (ModSyncFeature.RequireDurableTypes && !IsDurable(type))
            {
                reason = "is not written to savegames";
                return null;
            }

            bool isBuffer = info.Category == TypeManager.TypeCategory.BufferData;
            if (!ModFieldPlan.TryBuild(type, out ModFieldPlan plan, out reason)) return null;

            ModTypeKind kind = isBuffer
                ? ModTypeKind.Buffer
                : (plan.Count == 0 ? ModTypeKind.Tag : ModTypeKind.Component);

            if (kind == ModTypeKind.Buffer && plan.Count == 0)
            {
                reason = "is a buffer with no fields";
                return null;
            }

            var descriptor = new ModTypeDescriptor(key, kind, plan.Kinds, plan.FieldPaths);

            ModTypeAccessor accessor;
            try
            {
                accessor = ModTypeAccessor.Create(type, kind, plan, descriptor);
            }
            catch (Exception ex)
            {
                // A generic constraint this assembly cannot satisfy for that type.
                reason = "cannot be accessed (" + ex.GetType().Name + ")";
                return null;
            }

            return new ModCatalogEntry { Type = type, Descriptor = descriptor, Accessor = accessor };
        }

        /// <summary>Saved by the runtime: a mod's state rather than its scratch space.</summary>
        private static bool IsDurable(Type type)
        {
            return typeof(ISerializable).IsAssignableFrom(type) ||
                   typeof(IEmptySerializable).IsAssignableFrom(type) ||
                   typeof(IDefaultSerializable).IsAssignableFrom(type) ||
                   typeof(IStrideSerializable).IsAssignableFrom(type);
        }

        private static Type SafeType(TypeManager.TypeInfo info)
        {
            // Some entries have no resolvable managed type.
            try { return info.Type; }
            catch (Exception) { return null; }
        }

        /// <summary>Not the game, engine or runtime, by assembly-name prefix.</summary>
        private static bool IsThirdParty(Assembly assembly)
        {
            string name;
            try { name = assembly.GetName().Name; }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(name)) return false;

            if (name == "Game" || name.StartsWith("Game.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Colossal", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Unity", StringComparison.Ordinal)) return false;
            if (name.StartsWith("System", StringComparison.Ordinal)) return false;
            if (name == "mscorlib" || name == "netstandard") return false;
            return true;
        }

        private static string Short(Type type) => type.FullName;
    }
}
