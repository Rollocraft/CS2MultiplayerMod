using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// A third-party struct as an ordered list of leaves plus the reflection to read and write them.
    /// Not a memory copy: field offsets and embedded <see cref="Entity"/> values are process-local.
    /// </summary>
    internal sealed class ModFieldPlan
    {
        /// <summary>How deep a struct may nest before it is refused rather than walked further.</summary>
        private const int MaxDepth = 8;

        private const BindingFlags Fields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private FieldInfo[][] _paths;

        /// <summary>The leaf kinds, in the same order as <see cref="_paths"/>.</summary>
        public ModValueKind[] Kinds { get; private set; }
        public string[] FieldPaths { get; private set; }

        public int Count => Kinds.Length;

        /// <summary>True when at least one leaf is a reference that has to be translated.</summary>
        public bool HasReferences { get; private set; }

        /// <summary>Flattens <paramref name="type"/>, or gives a one-phrase reason for the catalogue listing.</summary>
        public static bool TryBuild(Type type, out ModFieldPlan plan, out string reason)
        {
            plan = null;
            reason = null;

            var paths = new List<FieldInfo[]>();
            var kinds = new List<ModValueKind>();
            var path = new List<FieldInfo>();

            if (!Walk(type, path, paths, kinds, 0, ref reason)) return false;

            if (kinds.Count > ModTypeDescriptor.MaxLeaves)
            {
                reason = "has " + kinds.Count + " fields";
                return false;
            }

            plan = new ModFieldPlan
            {
                _paths = paths.ToArray(),
                Kinds = kinds.ToArray(),
                FieldPaths = new string[paths.Count],
            };
            for (int i = 0; i < plan.Kinds.Length; i++)
            {
                FieldInfo[] fields = plan._paths[i];
                var names = new string[fields.Length];
                for (int j = 0; j < fields.Length; j++) names[j] = fields[j].Name;
                plan.FieldPaths[i] = string.Join(".", names);
                if (plan.Kinds[i] == ModValueKind.EntityRef) plan.HasReferences = true;
            }
            return true;
        }

        private static bool Walk(Type type, List<FieldInfo> path, List<FieldInfo[]> paths,
            List<ModValueKind> kinds, int depth, ref string reason)
        {
            if (depth > MaxDepth)
            {
                reason = "nests structs more than " + MaxDepth + " deep";
                return false;
            }

            FieldInfo[] fields = type.GetFields(Fields);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic) continue;

                Type fieldType = field.FieldType;
                path.Add(field);
                try
                {
                    if (TryScalarKind(fieldType, out ModValueKind kind))
                    {
                        paths.Add(path.ToArray());
                        kinds.Add(kind);
                        continue;
                    }

                    if (!fieldType.IsValueType || fieldType.IsPointer)
                    {
                        reason = "field " + field.Name + " is a " + fieldType.Name;
                        return false;
                    }

                    // A fixed buffer compiles to a struct with one field for the first element; walking it would
                    // carry one byte of sixteen. Fixed strings are handled above.
                    if (field.IsDefined(typeof(FixedBufferAttribute), false))
                    {
                        reason = "field " + field.Name + " is a fixed buffer";
                        return false;
                    }

                    if (!Walk(fieldType, path, paths, kinds, depth + 1, ref reason)) return false;
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
            }
            return true;
        }

        private static bool TryScalarKind(Type type, out ModValueKind kind)
        {
            if (type == typeof(Entity)) { kind = ModValueKind.EntityRef; return true; }

            if (type.IsEnum) type = Enum.GetUnderlyingType(type);

            if (type == typeof(bool)) { kind = ModValueKind.Bool; return true; }
            if (type == typeof(sbyte)) { kind = ModValueKind.I8; return true; }
            if (type == typeof(byte)) { kind = ModValueKind.U8; return true; }
            if (type == typeof(short)) { kind = ModValueKind.I16; return true; }
            if (type == typeof(ushort)) { kind = ModValueKind.U16; return true; }
            if (type == typeof(char)) { kind = ModValueKind.U16; return true; }
            if (type == typeof(int)) { kind = ModValueKind.I32; return true; }
            if (type == typeof(uint)) { kind = ModValueKind.U32; return true; }
            if (type == typeof(long)) { kind = ModValueKind.I64; return true; }
            if (type == typeof(ulong)) { kind = ModValueKind.U64; return true; }
            if (type == typeof(float)) { kind = ModValueKind.F32; return true; }
            if (type == typeof(double)) { kind = ModValueKind.F64; return true; }

            if (IsFixedString(type)) { kind = ModValueKind.Text; return true; }

            kind = default(ModValueKind);
            return false;
        }

        /// <summary>The engine's fixed strings, by their interfaces rather than a list of sizes.</summary>
        private static bool IsFixedString(Type type)
        {
            return typeof(IUTF8Bytes).IsAssignableFrom(type) &&
                   typeof(INativeList<byte>).IsAssignableFrom(type);
        }

        /// <summary>Reads one boxed value's leaves into <paramref name="destination"/>.</summary>
        public void Read(object boxed, ModLeaf[] destination, int offset, Func<Entity, ModEntityRef> translate)
        {
            for (int i = 0; i < _paths.Length; i++)
            {
                object value = boxed;
                FieldInfo[] path = _paths[i];
                for (int step = 0; step < path.Length; step++) value = path[step].GetValue(value);
                destination[offset + i] = ToLeaf(Kinds[i], value, translate);
            }
        }

        /// <summary>Writes leaves back into a boxed value and returns it (structs box by copy).</summary>
        public object Write(object boxed, ModLeaf[] source, int offset, Func<ModEntityRef, Entity> translate)
        {
            for (int i = 0; i < _paths.Length; i++)
            {
                FieldInfo[] path = _paths[i];
                object value = FromLeaf(Kinds[i], path[path.Length - 1].FieldType, source[offset + i], translate);
                boxed = SetPath(boxed, path, 0, value);
            }
            return boxed;
        }

        private static object SetPath(object owner, FieldInfo[] path, int index, object value)
        {
            FieldInfo field = path[index];
            if (index == path.Length - 1)
            {
                field.SetValue(owner, value);
                return owner;
            }

            // A boxed struct's nested struct is a copy: read out, write, put back.
            object child = field.GetValue(owner);
            child = SetPath(child, path, index + 1, value);
            field.SetValue(owner, child);
            return owner;
        }

        private static ModLeaf ToLeaf(ModValueKind kind, object value, Func<Entity, ModEntityRef> translate)
        {
            switch (kind)
            {
                case ModValueKind.EntityRef:
                    return ModLeaf.FromReference(translate((Entity)value));
                case ModValueKind.Text:
                    return ModLeaf.FromText(value == null ? string.Empty : value.ToString());
                case ModValueKind.F32:
                    return ModLeaf.FromReal((float)value);
                case ModValueKind.F64:
                    return ModLeaf.FromReal((double)value);
                case ModValueKind.Bool:
                    return ModLeaf.FromInteger((bool)value ? 1 : 0);
                default:
                    return ModLeaf.FromInteger(ToInteger(value));
            }
        }

        private static long ToInteger(object value)
        {
            if (value is ulong) return unchecked((long)(ulong)value);
            if (value.GetType().IsEnum)
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()));
            if (value is ulong) return unchecked((long)(ulong)value);
            return Convert.ToInt64(value);
        }

        private static object FromLeaf(ModValueKind kind, Type fieldType, ModLeaf leaf,
            Func<ModEntityRef, Entity> translate)
        {
            switch (kind)
            {
                case ModValueKind.EntityRef:
                    return translate(leaf.Reference);
                case ModValueKind.Text:
                    return MakeFixedString(fieldType, leaf.Text);
                case ModValueKind.F32:
                    return (float)leaf.Real;
                case ModValueKind.F64:
                    return leaf.Real;
                case ModValueKind.Bool:
                    return leaf.Integer != 0;
            }

            Type target = fieldType.IsEnum ? Enum.GetUnderlyingType(fieldType) : fieldType;
            object scalar;
            if (target == typeof(sbyte)) scalar = unchecked((sbyte)leaf.Integer);
            else if (target == typeof(byte)) scalar = unchecked((byte)leaf.Integer);
            else if (target == typeof(short)) scalar = unchecked((short)leaf.Integer);
            else if (target == typeof(ushort)) scalar = unchecked((ushort)leaf.Integer);
            else if (target == typeof(char)) scalar = unchecked((char)leaf.Integer);
            else if (target == typeof(int)) scalar = unchecked((int)leaf.Integer);
            else if (target == typeof(uint)) scalar = unchecked((uint)leaf.Integer);
            else if (target == typeof(long)) scalar = leaf.Integer;
            else if (target == typeof(ulong)) scalar = unchecked((ulong)leaf.Integer);
            else scalar = Convert.ChangeType(leaf.Integer, target);

            return fieldType.IsEnum ? Enum.ToObject(fieldType, scalar) : scalar;
        }

        /// <summary>Truncates rather than throws: one long name must not strand the whole carrier.</summary>
        private static object MakeFixedString(Type type, string text)
        {
            if (text == null) text = string.Empty;
            while (true)
            {
                try
                {
                    return Activator.CreateInstance(type, new object[] { text });
                }
                catch (Exception)
                {
                    if (text.Length == 0) return Activator.CreateInstance(type);
                    text = text.Substring(0, text.Length / 2);
                }
            }
        }
    }
}
