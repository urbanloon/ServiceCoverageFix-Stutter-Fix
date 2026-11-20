using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ServiceCoverageFix
{
    // The scheduling bridge verifies this field layout against SetupResourceSellerJob.
    [BurstCompile]
    internal struct SellerJob : IJobChunk
    {
#pragma warning disable CS0649 // The validated scheduling bridge copies the stock job fields into this layout.
        [ReadOnly] public EntityTypeHandle m_EntityType;
        [ReadOnly] public BufferTypeHandle<OwnedVehicle> m_OwnedVehicles;
        [ReadOnly] public ComponentLookup<IndustrialProcessData> m_IndustrialProcessDatas;
        [ReadOnly] public ComponentLookup<ServiceAvailable> m_ServiceAvailables;
        [ReadOnly] public ComponentLookup<ServiceCompanyData> m_ServiceCompanies;
        [ReadOnly] public ComponentLookup<ResourceData> m_ResourceDatas;
        [ReadOnly] public ResourcePrefabs m_ResourcePrefabs;
        [ReadOnly] public ComponentLookup<StorageCompanyData> m_StorageCompanyDatas;
        [ReadOnly] public ComponentLookup<PropertyRenter> m_PropertyRenters;
        [ReadOnly] public BufferLookup<Resources> m_Resources;
        [ReadOnly] public BufferLookup<TradeCost> m_TradeCosts;
        [ReadOnly] public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
        [ReadOnly] public ComponentLookup<Game.Buildings.CargoTransportStation> m_CargoTransportStations;
        [ReadOnly] public BufferTypeHandle<StorageTransferRequest> m_StorageTransferRequestType;
        [ReadOnly] public ComponentLookup<TransportCompanyData> m_TransportCompanyDatas;
        [ReadOnly] public BufferTypeHandle<TripNeeded> m_TripNeededType;
        [ReadOnly] public ComponentLookup<PrefabRef> m_Prefabs;
        [ReadOnly] public ComponentLookup<Building> m_Buildings;
        [ReadOnly] public ComponentLookup<Game.Vehicles.DeliveryTruck> m_DeliveryTrucks;
        [ReadOnly] public BufferLookup<GuestVehicle> m_GuestVehicleBufs;
        [ReadOnly] public BufferLookup<LayoutElement> m_LayoutElementBufs;
        [ReadOnly] public BufferTypeHandle<InstalledUpgrade> m_InstalledUpgradeType;
        [ReadOnly] public RandomSeed m_RandomSeed;
        public PathfindSetupSystem.SetupData m_SetupData;
#pragma warning restore CS0649

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            NativeArray<Entity> sellers = chunk.GetNativeArray(m_EntityType);
            if (sellers.Length == 0 || m_SetupData.Length == 0) return;

            BufferAccessor<StorageTransferRequest> transfers = default;
            BufferAccessor<InstalledUpgrade> upgrades = default;
            BufferAccessor<TripNeeded> trips = default;
            var vehicles = chunk.GetBufferAccessor(ref m_OwnedVehicles);

            Entity first = sellers[0];
            bool haveTraits = false;
            bool outside = false, cargo = false, service = false, building = false, renter = false;
            var random = m_RandomSeed.GetRandom(unfilteredChunkIndex);

            for (int request = 0; request < m_SetupData.Length; request++)
            {
                m_SetupData.GetItem(request, out Entity buyer, out var seeker);
                var target = seeker.m_SetupQueueTarget;
                var flags = target.m_Flags;
                bool requireTransport = (flags & SetupTargetFlags.RequireTransport) != 0;
                if (requireTransport && vehicles.Length == 0) continue;
                if (sellers.Length == 1 && first.Equals(buyer)) continue;
                // HasComponent is structural. Resolve once, after whole-request exclusions.
                if (!haveTraits)
                {
                    transfers = chunk.GetBufferAccessor(ref m_StorageTransferRequestType);
                    upgrades = chunk.GetBufferAccessor(ref m_InstalledUpgradeType);
                    trips = chunk.GetBufferAccessor(ref m_TripNeededType);
                    outside = m_OutsideConnections.HasComponent(first);
                    cargo = m_CargoTransportStations.HasComponent(first);
                    service = m_ServiceAvailables.HasComponent(first);
                    building = m_Buildings.HasComponent(first);
                    renter = m_PropertyRenters.HasComponent(first);
                    haveTraits = true;
                }
                bool upkeep = (flags & SetupTargetFlags.BuildingUpkeep) != 0;
                var resource = target.m_Resource;
                int amount = target.m_Value;
                bool office = EconomyUtils.IsOfficeResource(resource);

                // Only outside connections use the resource weight in the stock eligibility and inventory tests.
                bool weightless = outside && EconomyUtils.GetWeight(resource, m_ResourcePrefabs, ref m_ResourceDatas) == 0f;

                // Preserve the stock helper's floating-point evaluation, including NaN payloads.
                // Every eligible service row in this request uses the same read-only resource prefab.
                float marketPrice = 0f;
                bool haveMarketPrice = false;
                for (int row = 0; row < sellers.Length; row++)
                {
                    Entity seller = sellers[row];
                    if (seller.Equals(buyer)) continue;
                    Entity prefab = m_Prefabs[seller].m_Prefab;
                    bool storage = !cargo && !outside && m_StorageCompanyDatas.HasComponent(prefab);
                    bool industrial = !service && !storage && m_IndustrialProcessDatas.HasComponent(prefab);
                    if (building && BuildingUtils.CheckOption(m_Buildings[seller], BuildingOption.Inactive)) continue;
                    if ((service || industrial) && (!renter || m_PropertyRenters[seller].m_Property == Entity.Null)) continue;

                    bool sellsOutput = (industrial && (office || (flags & SetupTargetFlags.Industrial) != 0))
                        || (service && (flags & SetupTargetFlags.Commercial) != 0);
                    if (!(sellsOutput && (m_IndustrialProcessDatas[prefab].m_Output.m_Resource & resource) != 0))
                    {
                        if ((flags & SetupTargetFlags.Import) == 0 || !(outside || cargo || storage)) continue;
                        var storageData = m_StorageCompanyDatas[prefab];
                        if (upgrades.Length != 0)
                            UpgradeUtils.CombineStats(ref storageData, upgrades[row], ref seeker.m_PrefabRef, ref m_StorageCompanyDatas);
                        if ((storageData.m_StoredResources & resource) == 0 && !(outside && weightless)) continue;
                    }

                    if (!outside && upkeep && trips.Length != 0 && trips[row].Length > 0) continue;
                    int available = amount;
                    if (!(outside && weightless))
                    {
                        int reserved = VehicleUtils.GetAllBuyingResourcesTrucks(seller, resource, ref m_DeliveryTrucks, ref m_GuestVehicleBufs, ref m_LayoutElementBufs);
                        available = EconomyUtils.GetResources(resource, m_Resources[seller]) - reserved;
                        if (available <= 0 || (!outside && available < amount / 2)) continue;
                    }

                    float cost = 0f;
                    if (service)
                    {
                        int remaining = m_ServiceAvailables[seller].m_ServiceAvailable;
                        int capacity = math.max(1, m_ServiceCompanies[prefab].m_MaxService);
                        float fraction = (float)remaining / capacity;
                        float multiplier = EconomyUtils.GetServicePriceMultiplier(remaining, capacity);
                        if (!haveMarketPrice)
                        {
                            marketPrice = EconomyUtils.GetMarketPrice(resource, m_ResourcePrefabs, ref m_ResourceDatas);
                            haveMarketPrice = true;
                        }
                        float buy = m_TradeCosts.HasBuffer(seller) ? EconomyUtils.GetTradeCost(resource, m_TradeCosts[seller]).m_BuyCost : 0f;
                        float amountCost = (marketPrice * (multiplier - 1f) + buy - marketPrice * 0.5f) * amount;
                        float shortage = 100f * math.saturate(1f - 2f * fraction);
                        cost = (amountCost + shortage) * seeker.m_PathfindParameters.m_Weights.money;
                    }
                    else
                    {
                        cost += 100f * (1f - math.min(1f, (float)available * 1f / amount));
                        if (cargo)
                        {
                            if (requireTransport && (!m_TransportCompanyDatas.TryGetComponent(prefab, out var company)
                                || vehicles[row].Length >= company.m_MaxTransports)) continue;
                            if (trips.Length != 0 && trips[row].Length >= ResourcePathfindSetup.kCargoStationMaxTripNeededQueue) continue;
                            cost += ResourcePathfindSetup.kCargoStationAmountBasedPenalty * amount;
                            int queued = transfers.Length != 0 ? transfers[row].Length : 0;
                            cost += ResourcePathfindSetup.kCargoStationPerRequestPenalty * queued;
                        }
                        if (outside)
                        {
                            cost += ResourcePathfindSetup.kOutsideConnectionAmountBasedPenalty * amount;
                            if (upkeep) cost += random.NextInt(300);
                        }
                        if (m_TradeCosts.HasBuffer(seller))
                            cost += EconomyUtils.GetTradeCost(resource, m_TradeCosts[seller]).m_BuyCost * amount * 0.01f;
                    }
                    seeker.FindTargets(seller, cost);
                }
            }
        }
    }
}
