using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>A disabled preview must not hide the live network from another preview's EdgeIterator.</summary>
    internal sealed class NetPreviewVisibility
    {
        private readonly HashSet<Entity> _originals = new HashSet<Entity>();

        public void Suspend(EntityManager em, Entity preview)
        {
            if (!em.Exists(preview) || !em.HasComponent<Temp>(preview)) return;
            Entity original = em.GetComponentData<Temp>(preview).m_Original;
            if (!IsLiveOriginal(em, original) || !em.HasComponent<Hidden>(original)) return;
            if (!em.HasComponent<Node>(original) && !em.HasComponent<Edge>(original) &&
                !em.HasComponent<Lane>(original)) return;

            _originals.Add(original);
            em.RemoveComponent<Hidden>(original);
            em.AddComponent<BatchesUpdated>(original);
        }

        public void Restore(EntityManager em, List<Entity> previews)
        {
            // Only surviving previews reclaim visibility.
            for (int i = 0; i < previews.Count; i++)
            {
                Entity preview = previews[i];
                if (!em.Exists(preview) || em.HasComponent<Deleted>(preview) ||
                    !em.HasComponent<Temp>(preview)) continue;
                Entity original = em.GetComponentData<Temp>(preview).m_Original;
                if (!_originals.Contains(original) || !IsLiveOriginal(em, original)) continue;
                em.AddComponent<Hidden>(original);
                em.AddComponent<BatchesUpdated>(original);
            }
            _originals.Clear();
        }

        // Clearing the preview leaves its originals visible; release must not hide them again.
        public void Forget() => _originals.Clear();

        private static bool IsLiveOriginal(EntityManager em, Entity original) =>
            original != Entity.Null && em.Exists(original) &&
            !em.HasComponent<Deleted>(original) && !em.HasComponent<Temp>(original);
    }
}