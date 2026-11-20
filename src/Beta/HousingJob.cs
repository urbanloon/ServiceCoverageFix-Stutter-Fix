using Game.Buildings;
using Game.Citizens;
using Game.Economy;
using Game.Net;
using Game.Prefabs;
using Game.Zones;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace ServiceCoverageFix
{
    [BurstCompile]
    public struct HousingJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle m_EntityType;
        [ReadOnly] public BufferTypeHandle<Game.Buildings.Renter> m_RenterType;
        [ReadOnly] public ComponentTypeHandle<Game.Prefabs.PrefabRef> m_PrefabType;
        [ReadOnly] public ComponentLookup<Game.Buildings.Building> m_Buildings;
        [ReadOnly] public ComponentLookup<Game.Prefabs.BuildingData> m_BuildingDatas;
        [ReadOnly] public ComponentLookup<Game.Prefabs.ZoneData> m_ZoneDatas;
        [ReadOnly] public ComponentLookup<Game.Prefabs.ZonePropertiesData> m_ZonePropertiesDatas;
        [ReadOnly] public BufferLookup<Game.Net.ServiceCoverage> m_Coverages;
        public Game.Simulation.PathfindSetupSystem.SetupData m_SetupData;
        [ReadOnly] public ComponentLookup<Game.Buildings.PropertyOnMarket> m_PropertiesOnMarket;
        [ReadOnly] public ComponentLookup<Game.Prefabs.PrefabRef> m_PrefabRefs;
        [ReadOnly] public ComponentLookup<Game.Prefabs.BuildingPropertyData> m_BuildingProperties;
        [ReadOnly] public ComponentLookup<Game.Prefabs.SpawnableBuildingData> m_SpawnableDatas;
        [ReadOnly] public BufferLookup<Game.Net.ResourceAvailability> m_Availabilities;
        [ReadOnly] public BufferLookup<Game.Net.ServiceCoverage> m_ServiceCoverages;
        [ReadOnly] public ComponentLookup<Game.Citizens.Household> m_Households;
        [ReadOnly] public ComponentLookup<Game.Citizens.HomelessHousehold> m_HomelessHouseholds;
        [ReadOnly] public ComponentLookup<Game.Citizens.Citizen> m_Citizens;
        [ReadOnly] public ComponentLookup<Game.Citizens.HealthProblem> m_HealthProblems;
        [ReadOnly] public ComponentLookup<Game.Citizens.Worker> m_Workers;
        [ReadOnly] public ComponentLookup<Game.Citizens.Student> m_Students;
        [ReadOnly] public ComponentLookup<Game.Buildings.CrimeProducer> m_Crimes;
        [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transforms;
        [ReadOnly] public ComponentLookup<Game.Prefabs.Locked> m_Lockeds;
        [ReadOnly] public BufferLookup<Game.City.CityModifier> m_CityModifiers;
        [ReadOnly] public ComponentLookup<Game.Buildings.Park> m_Parks;
        [ReadOnly] public ComponentLookup<Game.Buildings.Abandoned> m_Abandoneds;
        [ReadOnly] public BufferLookup<Game.Citizens.HouseholdCitizen> m_HouseholdCitizens;
        [ReadOnly] public BufferLookup<Game.Economy.Resources> m_ResourcesBufs;
        [ReadOnly] public ComponentLookup<Game.Buildings.ElectricityConsumer> m_ElectricityConsumers;
        [ReadOnly] public ComponentLookup<Game.Buildings.WaterConsumer> m_WaterConsumers;
        [ReadOnly] public ComponentLookup<Game.Buildings.GarbageProducer> m_GarbageProducers;
        [ReadOnly] public ComponentLookup<Game.Buildings.MailProducer> m_MailProducers;
        [ReadOnly] public ComponentLookup<Game.Companies.CompanyData> m_Companies;
        [ReadOnly] public NativeArray<int> m_TaxRates;
        [ReadOnly] public NativeArray<Game.Simulation.AirPollution> m_AirPollutionMap;
        [ReadOnly] public NativeArray<Game.Simulation.GroundPollution> m_PollutionMap;
        [ReadOnly] public NativeArray<Game.Simulation.NoisePollution> m_NoiseMap;
        [ReadOnly] public Game.Simulation.CellMapData<Game.Simulation.TelecomCoverage> m_TelecomCoverages;
        public Game.Prefabs.HealthcareParameterData m_HealthcareParameters;
        public Game.Prefabs.ParkParameterData m_ParkParameters;
        public Game.Prefabs.EducationParameterData m_EducationParameters;
        public Game.Prefabs.EconomyParameterData m_EconomyParameters;
        public Game.Prefabs.TelecomParameterData m_TelecomParameters;
        public Game.Prefabs.GarbageParameterData m_GarbageParameters;
        public Game.Prefabs.PoliceConfigurationData m_PoliceParameters;
        public Game.Prefabs.CitizenHappinessParameterData m_CitizenHappinessParameterData;
        [ReadOnly] public Entity m_City;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(m_EntityType);
            var prefabs = chunk.GetNativeArray(ref m_PrefabType);
            var rentBuffers = chunk.GetBufferAccessor(ref m_RenterType);

            for (int request = 0; request < m_SetupData.Length; request++)
            {
                m_SetupData.GetItem(request, out _, out var seeker);
                var random = seeker.m_RandomSeed.GetRandom(unfilteredChunkIndex);
                var household = seeker.m_SetupQueueTarget.m_Entity;
                if (!m_HouseholdCitizens.TryGetBuffer(household, out var citizens))
                    continue;
                bool hasTempHome = m_HomelessHouseholds.TryGetComponent(household, out var homeless)
                    && homeless.m_TempHome != Entity.Null;
                // Some requests only encounter shelters or occupied buildings.
                bool evaluatedHousehold = false;
                int income = 0;
                bool needsSupport = false;
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var prefab = prefabs[i].m_Prefab;
                    var building = m_Buildings[entity];
                    var road = building.m_RoadEdge;
                    if (road == Entity.Null || !m_Coverages.HasBuffer(road)
                        || !m_BuildingDatas.HasComponent(prefab))
                        continue;

                    if (BuildingUtils.IsHomelessShelterBuilding(entity, ref m_Parks, ref m_Abandoneds))
                    {
                        if (hasTempHome)
                            continue;
                        float coverage = NetUtils.GetServiceCoverage(m_Coverages[road],
                            CoverageService.Police, building.m_CurvePosition);
                        int capacity = BuildingUtils.GetShelterHomelessCapacity(prefab,
                            ref m_BuildingDatas, ref m_BuildingProperties);
                        int occupants = rentBuffers[i].Length;
                        if (occupants < capacity)
                            seeker.FindTargets(entity, 100f * coverage + 1000f * occupants / capacity + 10000f);
                        continue;
                    }

                    int askingRent = m_PropertiesOnMarket[entity].m_AskingRent;
                    int totalUnits = 1, homes = 0, workUnits = 0;
                    if (m_BuildingProperties.TryGetComponent(prefab, out var property))
                    {
                        totalUnits = property.CountProperties();
                        homes = property.CountProperties(AreaType.Residential);
                        workUnits = property.CountProperties(AreaType.Commercial)
                            + property.CountProperties(AreaType.Industrial);
                    }
                    var renters = rentBuffers[i];
                    int occupantCount = renters.Length;
                    if (occupantCount >= totalUnits)
                        continue;
                    int companyCount = 0;
                    if (workUnits != 0)
                    {
                        for (int r = 0; r < occupantCount; r++)
                            if (m_Companies.HasComponent(renters[r]))
                                companyCount++;
                    }
                    if (occupantCount - companyCount >= homes)
                        continue;

                    if (!evaluatedHousehold)
                    {
                        income = EconomyUtils.GetHouseholdIncome(citizens, ref m_Workers, ref m_Citizens,
                            ref m_HealthProblems, ref m_EconomyParameters, m_TaxRates);
                        needsSupport = CitizenUtils.IsHouseholdNeedSupport(citizens, ref m_Citizens, ref m_Students);
                        evaluatedHousehold = true;
                    }
                    var zone = m_SpawnableDatas[prefab].m_ZonePrefab;
                    if (!m_ZonePropertiesDatas.TryGetComponent(zone, out var zoneProperties))
                        continue;
                    var density = PropertyUtils.GetZoneDensity(m_ZoneDatas[zone], zoneProperties);
                    float rentShare = density == ZoneDensity.Low ? .5f : density == ZoneDensity.Medium ? .7f : 1f;
                    if (!needsSupport && askingRent > income * rentShare)
                        continue;

                    float score = PropertyUtils.GetPropertyScore(entity, household, citizens, ref m_PrefabRefs,
                        ref m_BuildingProperties, ref m_Buildings, ref m_BuildingDatas, ref m_Households,
                        ref m_Citizens, ref m_Students, ref m_Workers, ref m_SpawnableDatas, ref m_Crimes,
                        ref m_ServiceCoverages, ref m_Lockeds, ref m_ElectricityConsumers, ref m_WaterConsumers,
                        ref m_GarbageProducers, ref m_MailProducers, ref m_Transforms, ref m_Abandoneds,
                        ref m_Parks, ref m_Availabilities, m_TaxRates, m_PollutionMap, m_AirPollutionMap,
                        m_NoiseMap, m_TelecomCoverages, m_CityModifiers[m_City],
                        m_HealthcareParameters.m_HealthcareServicePrefab, m_ParkParameters.m_ParkServicePrefab,
                        m_EducationParameters.m_EducationServicePrefab, m_TelecomParameters.m_TelecomServicePrefab,
                        m_GarbageParameters.m_GarbageServicePrefab, m_PoliceParameters.m_PoliceServicePrefab,
                        m_CitizenHappinessParameterData, m_GarbageParameters);
                    float cost = -score + 1000f * occupantCount / totalUnits + random.NextInt(500);
                    seeker.FindTargets(entity, cost);
                }
            }
        }
    }
}
