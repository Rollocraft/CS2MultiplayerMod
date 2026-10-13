using System.Collections.Generic;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Pending free nodes and their exact generator inputs for one realize batch.</summary>
    internal sealed class NetBatchNodes
    {
        private struct Entry
        {
            public CoursePos Position;
            public uint RequiredLayers, ConnectLayers;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public void Add(CoursePos position, uint requiredLayers, uint connectLayers)
        {
            if (position.m_Entity != Entity.Null ||
                (position.m_Flags & CoursePosFlags.DisableMerge) != 0) return;
            _entries.Add(new Entry { Position = position,
                RequiredLayers = requiredLayers, ConnectLayers = connectLayers });
        }

        public bool TryFind(float3 position, uint flags, uint requiredLayers, uint connectLayers,
            float horizontalTolerance, float verticalTolerance, out CoursePos? shared)
        {
            shared = null;
            if (((CoursePosFlags)flags & CoursePosFlags.DisableMerge) != 0) return false;
            float best = float.MaxValue;
            foreach (Entry entry in _entries)
            {
                if ((requiredLayers & entry.ConnectLayers) != requiredLayers &&
                    (entry.RequiredLayers & connectLayers) != entry.RequiredLayers) continue;
                float3 candidate = entry.Position.m_Position;
                float distance = math.distancesq(candidate.xz, position.xz);
                float dy = math.abs(candidate.y - position.y);
                if (distance >= horizontalTolerance * horizontalTolerance || dy > verticalTolerance)
                    continue;
                float score = distance + dy * dy;
                if (score >= best) continue;
                best = score;
                shared = entry.Position;
            }
            return shared.HasValue;
        }

        public static CoursePos Merge(CoursePos position, CoursePos? shared)
        {
            if (!shared.HasValue || position.m_Entity != Entity.Null ||
                (position.m_Flags & CoursePosFlags.DisableMerge) != 0) return position;
            CoursePos node = shared.Value;
            // Nodes merge only on exact positions; copy position and height inputs.
            position.m_Position = node.m_Position;
            position.m_Elevation = node.m_Elevation;
            position.m_Flags = (position.m_Flags & ~CoursePosFlags.FreeHeight) |
                               (node.m_Flags & CoursePosFlags.FreeHeight);
            return position;
        }
    }
}
