using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The per-roster trace lines that make a mismatch legible in the log, and the small shared
    // helpers the capture path folds ids and clamps wire values with. Change detection itself
    // lives in CaptureProbe.cs, which never builds the object it would otherwise hash.
    public partial class ResidentialOccupancySyncSystem
    {
        private static int HashId(int hash, ulong id)
        {
            unchecked
            {
                hash = (hash ^ (int)id) * 16777619;
                return (hash ^ (int)(id >> 32)) * 16777619;
            }
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceSentRoster(Entity propertyEntity, OccupancyProperty property)
        {
            int hash = TraceRosterHash(property);
            int previous;
            if (_traceSentRosterHashes.TryGetValue(propertyEntity, out previous) &&
                previous == hash) return;
            bool first = !_traceSentRosterHashes.ContainsKey(propertyEntity);
            _traceSentRosterHashes[propertyEntity] = hash;
            if (first && property.Households.Length == 0) return;
            LogRosterTrace("SENT", property);
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceReceivedRoster(OccupancyProperty property)
        {
            int hash = TraceRosterHash(property);
            int previous;
            if (_traceReceivedRosterHashes.TryGetValue(property.Identity, out previous) &&
                previous == hash) return;
            bool first = !_traceReceivedRosterHashes.ContainsKey(property.Identity);
            _traceReceivedRosterHashes[property.Identity] = hash;
            if (first && property.Households.Length == 0) return;
            LogRosterTrace("RECEIVED", property);
        }

        private static int TraceRosterHash(OccupancyProperty property)
        {
            unchecked
            {
                int hash = property.Households != null ? property.Households.Length : 0;
                if (property.Households == null) return hash;
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    hash = HashId(hash, household.HouseholdId);
                    hash = hash * 397 ^ (household.Departing ? 1 : 0);
                    hash = hash * 397 ^ household.Rent;
                    hash = hash * 397 ^ household.SalaryLastDay;
                    hash = hash * 397 ^ (int)household.ShoppedValuePerDay;
                    hash = hash * 397 ^ household.MoneySpentOnBuildingLevelingLastDay;
                    hash = hash * 397 + (household.Citizens != null
                        ? household.Citizens.Length : 0);
                    if (household.Citizens != null)
                        for (int c = 0; c < household.Citizens.Length; c++)
                            hash = HashId(hash, household.Citizens[c].CitizenId);
                    hash = hash * 397 + (household.OwnedVehicles != null
                        ? household.OwnedVehicles.Length : 0);
                    if (household.OwnedVehicles != null)
                        for (int v = 0; v < household.OwnedVehicles.Length; v++)
                            hash = hash * 397 ^ household.OwnedVehicles[v].GetHashCode();
                }
                return hash;
            }
        }

        private static void LogRosterTrace(string stage, OccupancyProperty property)
        {
            var roster = new StringBuilder();
            for (int i = 0; i < property.Households.Length; i++)
            {
                if (i != 0) roster.Append(", ");
                OccupancyHousehold household = property.Households[i];
                roster.Append("0x").Append(household.HouseholdId.ToString("X16"))
                    .Append("/").Append(household.Citizens != null
                        ? household.Citizens.Length : 0).Append(" people/")
                    .Append(household.OwnedVehicles != null
                        ? household.OwnedVehicles.Length : 0).Append(" vehicles")
                    .Append("/rent=").Append(household.Rent)
                    .Append("/income=").Append(household.SalaryLastDay)
                    .Append("/upkeep=")
                    .Append(Math.Abs((long)household.MoneySpentOnBuildingLevelingLastDay))
                    .Append("/resourceCost=").Append(household.ShoppedValuePerDay)
                    .Append("/savings=").Append(household.Savings)
                    .Append("/money=").Append(household.Money);
                if (household.Departing) roster.Append("/departing");
            }
            SyncLog.Detail(LogTopic.Residential, stage + " house='" + property.PrefabName +
                "' anchor=(" + property.AnchorX.ToString("F2") + ", " +
                property.AnchorY.ToString("F2") + ", " + property.AnchorZ.ToString("F2") + ") rev=" +
                property.Revision + " families=" + property.Households.Length + " roster=[" + roster +
                "].");
        }

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;
    }
}
