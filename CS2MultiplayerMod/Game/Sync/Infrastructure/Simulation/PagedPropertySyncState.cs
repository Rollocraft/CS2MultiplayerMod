using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class PropertySyncLimits
    {
        public const int UpdatePartitions = 16;
        public const int MaxIncomingPages = 8;
        public const int MaxPumpPages = 2;
        public const int MaxCachedProperties = 131072;
        public const int MaxPendingIdentities = 4096;
        public const int MaxPriorityEntries = 2048;
        public const int MaxPropertiesObservedPerUpdate = 256;
        public const long ResolveRetryMs = 5000;
        public const long StatsIntervalMs = 30000;
    }

    internal class PendingPropertyTiming
    {
        public uint SweepId;
        public long ExpiresMs;
        public long NextAttemptMs;
    }

    internal class PendingPropertyState<T> : PendingPropertyTiming
    {
        public T Entry;
    }

    /// <summary>Shared page, cache, retry and observation state of the property sync systems.</summary>
    internal sealed class PagedPropertySyncState<TPage, TCache, TPending, THost, TPriority>
        where TPending : PendingPropertyTiming
    {
        public readonly ConcurrentQueue<TPage> Incoming = new ConcurrentQueue<TPage>();
        public readonly Dictionary<Entity, TCache> Cache = new Dictionary<Entity, TCache>();
        public readonly Dictionary<PropertyIdentity, TPending> Pending =
            new Dictionary<PropertyIdentity, TPending>();
        public readonly ConcurrentQueue<PropertyIdentity> PendingOrder =
            new ConcurrentQueue<PropertyIdentity>();
        public readonly Dictionary<Entity, THost> HostObserved = new Dictionary<Entity, THost>();
        public readonly Dictionary<PropertyIdentity, TPriority> Priority =
            new Dictionary<PropertyIdentity, TPriority>();
        public readonly ConcurrentQueue<PropertyIdentity> PriorityOrder =
            new ConcurrentQueue<PropertyIdentity>();
        public readonly PropertyPartitions CachedPartitions = new PropertyPartitions();
        public readonly PropertyPartitions HostPartitions = new PropertyPartitions();
        public long NextPendingPumpMs;
        public uint SweepId;
        public int NextPage;
        public bool SweepIntact;

        /// <summary>
        /// Track page order within the host's sweep. With <paramref name="rejectOlder"/> a page of an
        /// older sweep returns false and leaves the tracking untouched.
        /// </summary>
        public bool NotePage(uint sweepId, int pageIndex, bool rejectOlder = false)
        {
            if (sweepId != SweepId)
            {
                if (rejectOlder && SweepId != 0 && unchecked((int)(sweepId - SweepId)) <= 0) return false;
                SweepId = sweepId;
                NextPage = 0;
                SweepIntact = pageIndex == 0;
            }
            if (pageIndex != NextPage) SweepIntact = false;
            if (pageIndex >= NextPage) NextPage = pageIndex + 1;
            return true;
        }

        /// <summary>Only a sweep that arrived without a gap may prune: a missing page says nothing about its buildings.</summary>
        public bool CompletesSweep(uint sweepId, int pageIndex) =>
            SweepIntact && sweepId == SweepId && pageIndex + 1 == NextPage;

        public void ResetSweep()
        {
            SweepId = 0;
            NextPage = 0;
            SweepIntact = false;
        }

        public void ClearPending()
        {
            Pending.Clear();
            while (PendingOrder.TryDequeue(out _)) { }
            NextPendingPumpMs = 0;
        }

        public int DropIncoming()
        {
            if (Incoming.IsEmpty) return 0;
            int dropped = 0;
            lock (Incoming)
                while (Incoming.TryDequeue(out _)) dropped++;
            return dropped;
        }

        public bool RetryDue(long now) => Pending.Count > 0 && now >= NextPendingPumpMs;

        public void ScheduleRetry(long at)
        {
            if (NextPendingPumpMs == 0 || at < NextPendingPumpMs) NextPendingPumpMs = at;
        }

        /// <summary>Hold an unresolved entry for retry. False when the pending bound is full.</summary>
        public bool Hold(PropertyIdentity identity, TPending pending, uint sweepId, long now,
            long timeoutMs)
        {
            if (Pending.Count >= PropertySyncLimits.MaxPendingIdentities) return false;
            pending.SweepId = sweepId;
            pending.ExpiresMs = now + timeoutMs;
            pending.NextAttemptMs = now + PropertySyncLimits.ResolveRetryMs;
            Pending[identity] = pending;
            PendingOrder.Enqueue(identity);
            ScheduleRetry(pending.NextAttemptMs);
            return true;
        }

        public void RetryPending(long now, int budget, Func<TPending, bool> apply, Action expired) =>
            PropertyRetryPump.Pump(Pending, PendingOrder, now, budget, apply, expired);

        public bool Prioritize(PropertyIdentity identity, TPriority value, int capacity,
            out int dropped)
        {
            dropped = 0;
            if (Priority.ContainsKey(identity))
            {
                Priority[identity] = value;
                return false;
            }
            while (Priority.Count >= capacity &&
                   PriorityOrder.TryDequeue(out PropertyIdentity oldest))
                if (Priority.Remove(oldest)) dropped++;
            if (Priority.Count >= capacity) { dropped++; return false; }
            Priority[identity] = value;
            PriorityOrder.Enqueue(identity);
            return true;
        }

        public int Enqueue(TPage page)
        {
            int dropped = 0;
            lock (Incoming)
            {
                Incoming.Enqueue(page);
                while (Incoming.Count > PropertySyncLimits.MaxIncomingPages &&
                       Incoming.TryDequeue(out _)) dropped++;
            }
            return dropped;
        }

        public int PumpPages(int budget, Action<TPage> apply)
        {
            int pages = 0;
            while (pages < budget && Incoming.TryDequeue(out TPage page))
            {
                pages++;
                apply(page);
            }
            return pages;
        }
    }

    internal sealed class PropertyPartitions
    {
        public readonly List<Entity>[] Buckets =
            new List<Entity>[PropertySyncLimits.UpdatePartitions];
        public readonly HashSet<Entity>[] Members =
            new HashSet<Entity>[PropertySyncLimits.UpdatePartitions];
        public readonly int[] Cursor = new int[PropertySyncLimits.UpdatePartitions];
        public readonly bool[] Initialized = new bool[PropertySyncLimits.UpdatePartitions];

        public PropertyPartitions()
        {
            for (int i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new List<Entity>();
                Members[i] = new HashSet<Entity>();
            }
        }

        public void Add(int bucket, Entity entity)
        {
            if (Members[bucket].Add(entity)) Buckets[bucket].Add(entity);
        }
    }

    /// <summary>A bounded ordered list whose membership set also suppresses duplicates.</summary>
    internal static class BoundedUniquePropertyList
    {
        public static void Enqueue<T>(List<T> items, HashSet<T> members, T item, int capacity)
        {
            if (!members.Add(item)) return;
            items.Add(item);
            if (items.Count <= capacity) return;
            members.Remove(items[0]);
            items.RemoveAt(0);
        }
    }

    internal static class PropertyRetryPump
    {
        public static void Pump<T>(Dictionary<PropertyIdentity, T> pending,
            ConcurrentQueue<PropertyIdentity> order, long now, int budget,
            Func<T, bool> apply, Action expired) where T : PendingPropertyTiming
        {
            // Inspect each identity at most once, including small not-yet-due queues.
            int count = Math.Min(budget, order.Count);
            while (count-- > 0 && order.TryDequeue(out PropertyIdentity identity))
            {
                if (!pending.TryGetValue(identity, out T value)) continue;
                if (value.ExpiresMs <= now)
                {
                    pending.Remove(identity);
                    expired();
                }
                else if (value.NextAttemptMs > now) order.Enqueue(identity);
                else if (apply(value)) pending.Remove(identity);
                else
                {
                    value.NextAttemptMs = now + PropertySyncLimits.ResolveRetryMs;
                    order.Enqueue(identity);
                }
            }
        }
    }
}
