#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System;
using System.Collections.Generic;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Unity.Rendering
{
    // Survives entity destruction so that destroyed instances can be reported.
    internal struct SurfaceCacheTrackedInstance : ICleanupComponentData
    {
    }

    // Survives group entity destruction so that a disappearing LOD group re-pushes its renderers.
    internal struct SurfaceCacheTrackedLODGroup : ICleanupComponentData
    {
    }

    // Detects new, changed and destroyed renderable entities and pushes records to the SurfaceCacheEntityChangeBridge.
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(EntitiesGraphicsSystem))]
    internal partial class SurfaceCacheMeshInstanceProducerSystem : SystemBase
    {
        struct ChunkHandles
        {
            public EntityTypeHandle Entity;
            public ComponentTypeHandle<MaterialMeshInfo> MaterialMeshInfo;
            public ComponentTypeHandle<GlobalIlluminationMeshLod> GlobalIlluminationMeshLod;
            public ComponentTypeHandle<LocalToWorld> LocalToWorld;
            public ComponentTypeHandle<DisableRendering> DisableRendering;
            public ComponentTypeHandle<Disabled> Disabled;
            public ComponentTypeHandle<MeshLODComponent> LODGroupMembership;
            public ComponentTypeHandle<ExcludedFromGlobalIllumination> ExcludedFromGlobalIllumination;
            public SharedComponentTypeHandle<RenderMeshArray> RenderMeshArray;
            public SharedComponentTypeHandle<RenderFilterSettings> RenderFilterSettings;
            public ComponentLookup<MeshLODGroupComponent> LODGroupLookup;
            public ComponentLookup<GlobalIlluminationLOD> GlobalIlluminationLODLookup;
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBounds;
            public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;
            public ComponentTypeHandle<SurfaceCacheExtracted> Extracted;
        }

        static readonly ProfilerMarker k_ProductionMarker = new("SurfaceCache.EntityChangeProduction");
        static readonly ProfilerMarker k_CullingMarker = new("SurfaceCache.EntityCulling");

        EntityQuery m_UntrackedQuery;
        EntityQuery m_TrackedQuery;
        EntityQuery m_DestroyedQuery;
        EntityQuery m_MissingTransformQuery;
        EntityQuery m_DeformedQuery;
        EntityQuery m_AllTrackedQuery;
        EntityQuery m_ChangedMeshLodQuery;
        EntityQuery m_MaterialOverrideQuery;
        EntityQuery m_LODGroupQuery;
        EntityQuery m_NewLODGroupQuery;
        EntityQuery m_RemovedLODGroupQuery;
        EntityQuery m_AllTrackedLODGroupQuery;
        EntityQuery m_CulledQuery;
        EntityQuery m_ExtractedQuery;
        ComponentTypeSet m_TrackingComponents;
        EntitiesGraphicsSystem m_RendererSystem;
        uint m_LastConsumerVersion;
        SurfaceCacheRenderMeshArrayTable m_RenderMeshArrayTable;
        NativeList<ArchetypeChunk> m_FullRecordChunks;
        NativeList<v128> m_FullRecordPushMasks;
        NativeList<ArchetypeChunk> m_MovedChunks;
        NativeList<ArchetypeChunk> m_CullingCandidateChunks;
        bool m_MayHaveCulledEntities;
        SurfaceCacheSubMeshMaterial[] m_MaterialScratch = new SurfaceCacheSubMeshMaterial[8];
        int m_MaterialScratchCount;

        struct MaterialOverrideType
        {
            public ComponentType Type;
            public int NameId;
            public int FloatCount;
        }

        MaterialOverrideType[] m_MaterialOverrideTypes;
        DynamicComponentTypeHandle[] m_MaterialOverrideHandles;
        NativeArray<float>[] m_ChunkOverrideValues;
        int[] m_ChunkOverrideTypes;
        SurfaceCacheMaterialOverride[] m_OverrideScratch;
        bool m_HasMaterialOverrideChunks;

        // Entities only reports changes per chunk, and every LOD group shares an archetype, so one group changing
        // marks them all. The previous selection per group is what narrows that back down to the groups that moved.
        NativeHashMap<Entity, int> m_LastGlobalIlluminationLOD;

        internal const int k_ParallelTransformThreshold = 4096;

        const int k_NoGlobalIlluminationLOD = -1;
        const int k_NoLODGroupComponent = -2;

        protected override void OnCreate()
        {
            m_RenderMeshArrayTable = SurfaceCacheRenderMeshArrayTable.Create();

            // Hidden entities are kept in the queries and reported with Visible=false, so every liveness transition produces a record.
            const EntityQueryOptions options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState;

            // WithAbsent: with IgnoreComponentEnabledState, WithNone on enableable components stops excluding archetypes.
            m_UntrackedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MaterialMeshInfo, LocalToWorld>()
                .WithAbsent<SurfaceCacheTrackedInstance, DeformedMeshIndex>()
                .WithOptions(options)
                .Build(this);

            m_TrackedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MaterialMeshInfo, LocalToWorld, SurfaceCacheTrackedInstance>()
                .WithOptions(options)
                .Build(this);

            m_DestroyedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedInstance>()
                .WithAbsent<MaterialMeshInfo>()
                .WithOptions(options)
                .Build(this);

            m_MissingTransformQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedInstance, MaterialMeshInfo>()
                .WithAbsent<LocalToWorld>()
                .WithOptions(options)
                .Build(this);

            m_DeformedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedInstance, DeformedMeshIndex>()
                .WithOptions(options)
                .Build(this);

            m_AllTrackedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedInstance>()
                .WithOptions(options)
                .Build(this);

            m_ChangedMeshLodQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MaterialMeshInfo, LocalToWorld, SurfaceCacheTrackedInstance, GlobalIlluminationMeshLod>()
                .WithOptions(options)
                .Build(this);
            m_ChangedMeshLodQuery.SetChangedVersionFilter(ComponentType.ReadOnly<GlobalIlluminationMeshLod>());

            // Both components are read, so either one appearing, disappearing or changing affects the group's renderers.
            m_LODGroupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAny<MeshLODGroupComponent, GlobalIlluminationLOD>()
                .WithOptions(options)
                .Build(this);

            m_NewLODGroupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAny<MeshLODGroupComponent, GlobalIlluminationLOD>()
                .WithAbsent<SurfaceCacheTrackedLODGroup>()
                .WithOptions(options)
                .Build(this);

            m_RemovedLODGroupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedLODGroup>()
                .WithAbsent<MeshLODGroupComponent, GlobalIlluminationLOD>()
                .WithOptions(options)
                .Build(this);

            m_AllTrackedLODGroupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SurfaceCacheTrackedLODGroup>()
                .WithOptions(options)
                .Build(this);

            m_CulledQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalToWorld, SurfaceCacheTrackedInstance>()
                .WithPresent<MaterialMeshInfo>()
                .WithDisabled<SurfaceCacheExtracted>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                .Build(this);

            m_ExtractedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalToWorld, SurfaceCacheTrackedInstance, SurfaceCacheExtracted>()
                .WithPresent<MaterialMeshInfo>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                .Build(this);

            m_TrackingComponents = new ComponentTypeSet(
                ComponentType.ReadWrite<SurfaceCacheTrackedInstance>(),
                ComponentType.ReadWrite<SurfaceCacheExtracted>());

            m_LastGlobalIlluminationLOD = new NativeHashMap<Entity, int>(64, Allocator.Persistent);

            m_MaterialOverrideTypes = FindMaterialOverrideTypes();
            m_MaterialOverrideHandles = new DynamicComponentTypeHandle[m_MaterialOverrideTypes.Length];
            m_ChunkOverrideValues = new NativeArray<float>[m_MaterialOverrideTypes.Length];
            m_ChunkOverrideTypes = new int[m_MaterialOverrideTypes.Length];
            m_OverrideScratch = new SurfaceCacheMaterialOverride[m_MaterialOverrideTypes.Length];

            if (m_MaterialOverrideTypes.Length != 0)
            {
                var overrideTypes = new NativeList<ComponentType>(m_MaterialOverrideTypes.Length, Allocator.Temp);
                foreach (var overrideType in m_MaterialOverrideTypes)
                    overrideTypes.Add(overrideType.Type);

                m_MaterialOverrideQuery = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<MaterialMeshInfo, LocalToWorld>()
                    .WithAny(ref overrideTypes)
                    .WithOptions(options)
                    .Build(this);
            }

            Enabled = EntitiesGraphicsSystem.EntitiesGraphicsEnabled;
        }

        // Only float to float4 values reach a GI material. The "unity_" properties are engine data, not material values.
        static MaterialOverrideType[] FindMaterialOverrideTypes()
        {
            var types = new List<MaterialOverrideType>();
            foreach (var mapping in EntitiesGraphicsSystem.GetMaterialPropertyTypes())
            {
                var type = mapping.Key;
                if (!type.IsValueType || !typeof(IComponentData).IsAssignableFrom(type))
                    continue;

                string propertyName = mapping.Value.Name;
                int size = mapping.Value.SizeCPU;
                if (propertyName.StartsWith("unity_", StringComparison.Ordinal) || size == 0 || size > UnsafeUtility.SizeOf<Vector4>() || size % sizeof(float) != 0)
                    continue;

                types.Add(new MaterialOverrideType
                {
                    Type = ComponentType.ReadOnly(type),
                    NameId = Shader.PropertyToID(propertyName),
                    FloatCount = size / sizeof(float),
                });
            }

            return types.ToArray();
        }

        protected override void OnDestroy()
        {
            UntrackAll();

            if (m_LastGlobalIlluminationLOD.IsCreated)
                m_LastGlobalIlluminationLOD.Dispose();

            SurfaceCacheEntityChangeBridge.CompleteTransformJob();
            if (m_FullRecordChunks.IsCreated)
                m_FullRecordChunks.Dispose();
            if (m_FullRecordPushMasks.IsCreated)
                m_FullRecordPushMasks.Dispose();
            if (m_MovedChunks.IsCreated)
                m_MovedChunks.Dispose();
            if (m_CullingCandidateChunks.IsCreated)
                m_CullingCandidateChunks.Dispose();
            m_RenderMeshArrayTable.Dispose();
        }

        protected override void OnUpdate()
        {
            using var _ = k_ProductionMarker.Auto();

            SurfaceCacheEntityChangeBridge.CompleteTransformJob();

            if (!SurfaceCacheEntityChangeBridge.IsConsumerPresent)
            {
                UntrackAll();
                return;
            }

            CompleteDependency();
            SurfaceCacheEntityChangeBridge.BeginProducerPass();

            bool fullResync = SurfaceCacheEntityChangeBridge.ConsumerVersion != m_LastConsumerVersion;
            m_LastConsumerVersion = SurfaceCacheEntityChangeBridge.ConsumerVersion;

            if (m_RendererSystem == null)
                m_RendererSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

            PushDestroyed();

            // Makes structural changes (group tracking), so it must run before the type handles are created.
            using var changedLODGroups = new NativeHashSet<Entity>(16, Allocator.TempJob);
            GatherChangedLODGroups(changedLODGroups);

            // Before any resolution, so records never index a stale entry.
            m_RenderMeshArrayTable.Update(EntityManager);

            var handles = new ChunkHandles
            {
                Entity = GetEntityTypeHandle(),
                MaterialMeshInfo = GetComponentTypeHandle<MaterialMeshInfo>(true),
                GlobalIlluminationMeshLod = GetComponentTypeHandle<GlobalIlluminationMeshLod>(true),
                LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true),
                DisableRendering = GetComponentTypeHandle<DisableRendering>(true),
                Disabled = GetComponentTypeHandle<Disabled>(true),
                LODGroupMembership = GetComponentTypeHandle<MeshLODComponent>(true),
                ExcludedFromGlobalIllumination = GetComponentTypeHandle<ExcludedFromGlobalIllumination>(true),
                RenderMeshArray = GetSharedComponentTypeHandle<RenderMeshArray>(),
                RenderFilterSettings = GetSharedComponentTypeHandle<RenderFilterSettings>(),
                LODGroupLookup = GetComponentLookup<MeshLODGroupComponent>(true),
                GlobalIlluminationLODLookup = GetComponentLookup<GlobalIlluminationLOD>(true),
                WorldRenderBounds = GetComponentTypeHandle<WorldRenderBounds>(true),
                ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                Extracted = GetComponentTypeHandle<SurfaceCacheExtracted>(),
            };

            m_HasMaterialOverrideChunks = m_MaterialOverrideTypes.Length != 0 && !m_MaterialOverrideQuery.IsEmptyIgnoreFilter;
            if (m_HasMaterialOverrideChunks)
            {
                for (int i = 0; i < m_MaterialOverrideTypes.Length; i++)
                    m_MaterialOverrideHandles[i] = GetDynamicComponentTypeHandle(m_MaterialOverrideTypes[i].Type);
            }

            if (m_FullRecordChunks.IsCreated)
            {
                m_FullRecordChunks.Clear();
                m_FullRecordPushMasks.Clear();
            }
            if (m_MovedChunks.IsCreated)
                m_MovedChunks.Clear();
            if (m_CullingCandidateChunks.IsCreated)
                m_CullingCandidateChunks.Clear();

            using var cullingObservers = CopyCullingObservers();
            bool cullingActive = cullingObservers.Length != 0;

            bool checkCulling = cullingActive || m_MayHaveCulledEntities;

            int movedCount = ClassifyTrackedChunks(fullResync, changedLODGroups, checkCulling, ref handles);

            bool hasUntracked = !m_UntrackedQuery.IsEmptyIgnoreFilter;
            if (hasUntracked)
            {
                foreach (var chunk in m_UntrackedQuery.ToArchetypeChunkArray(Allocator.Temp))
                    AddFullRecordChunk(chunk, SurfaceCacheEntityCulling.FirstBits(chunk.Count));
            }

            if (checkCulling && m_CullingCandidateChunks.IsCreated)
                AddCullingChanges(cullingObservers, ref handles);

            using var culledUntrackedEntities = new NativeList<Entity>(Allocator.TempJob);
            if (m_FullRecordChunks.IsCreated)
                PushFullRecords(cullingObservers, culledUntrackedEntities, ref handles);

            if (hasUntracked)
            {
                EntityManager.AddComponent(m_UntrackedQuery, m_TrackingComponents);
                if (!culledUntrackedEntities.IsEmpty)
                {
                    new SurfaceCacheMarkCulledJob
                    {
                        Entities = culledUntrackedEntities.AsArray(),
                        ExtractedLookup = GetComponentLookup<SurfaceCacheExtracted>(),
                    }.Run();
                }

                handles.Entity = GetEntityTypeHandle();
                handles.LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true);
            }

            m_MayHaveCulledEntities = cullingActive;
            int trackedChunkCount = m_TrackedQuery.CalculateChunkCountWithoutFiltering();
            SurfaceCacheEntityChangeBridge.ReportCullingStatistics(
                World.SequenceNumber,
                m_TrackedQuery.CalculateEntityCountWithoutFiltering(),
                cullingActive ? m_CulledQuery.CalculateEntityCount() : 0,
                trackedChunkCount,
                cullingActive ? trackedChunkCount - m_ExtractedQuery.CalculateChunkCount() : 0);

            if (movedCount >= k_ParallelTransformThreshold)
            {
                var transformJob = CreateTransformJob(m_MovedChunks, ref handles, movedCount)
                    .Schedule(m_MovedChunks.Length, 1, Dependency);
                SurfaceCacheEntityChangeBridge.SetTransformJob(transformJob);
                JobHandle.ScheduleBatchedJobs();
                Dependency = transformJob;
            }
            else if (movedCount > 0)
            {
                CreateTransformJob(m_MovedChunks, ref handles, movedCount).Run(m_MovedChunks.Length);
            }

            SurfaceCacheEntityChangeBridge.EndProducerPass();
        }

        void PushDestroyed()
        {
            UntrackDestroyed(m_DestroyedQuery);
            UntrackDestroyed(m_MissingTransformQuery);
            UntrackDestroyed(m_DeformedQuery);
        }

        void UntrackAll()
        {
            if (!m_AllTrackedLODGroupQuery.IsEmptyIgnoreFilter)
                EntityManager.RemoveComponent<SurfaceCacheTrackedLODGroup>(m_AllTrackedLODGroupQuery);

            SurfaceCacheEntityChangeBridge.RemoveCullingStatistics(World.SequenceNumber);

            if (m_LastGlobalIlluminationLOD.IsCreated)
                m_LastGlobalIlluminationLOD.Clear();

            if (m_AllTrackedQuery.IsEmptyIgnoreFilter)
                return;

            if (SurfaceCacheEntityChangeBridge.IsConsumerPresent)
            {
                var tracked = m_AllTrackedQuery.ToEntityArray(Allocator.Temp);
                foreach (var entity in tracked)
                {
                    SurfaceCacheEntityChangeBridge.PushDestroyed(entity);
                }
            }

            EntityManager.RemoveComponent(m_AllTrackedQuery, m_TrackingComponents);
        }

        void UntrackDestroyed(EntityQuery query)
        {
            if (query.IsEmptyIgnoreFilter)
                return;

            var destroyed = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in destroyed)
            {
                SurfaceCacheEntityChangeBridge.PushDestroyed(entity);
            }

            EntityManager.RemoveComponent(query, m_TrackingComponents);
        }

        // Changes to a LOD group entity never mark its renderers' chunks as changed, so this collects the groups whose
        // GI LOD selection changed and their renderers are re-pushed. A destroyed group is still found through its
        // SurfaceCacheTrackedLODGroup cleanup component, and adding or removing a group component moves the group to
        // another chunk, which DidOrderChange catches.
        void GatherChangedLODGroups(NativeHashSet<Entity> changedGroups)
        {
            if (!m_RemovedLODGroupQuery.IsEmptyIgnoreFilter)
            {
                // Collected before the tracking component goes, which is what lets a destroyed group entity die.
                var removed = m_RemovedLODGroupQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < removed.Length; i++)
                {
                    changedGroups.Add(removed[i]);
                    m_LastGlobalIlluminationLOD.Remove(removed[i]);
                }

                EntityManager.RemoveComponent<SurfaceCacheTrackedLODGroup>(m_RemovedLODGroupQuery);
            }

            if (!m_NewLODGroupQuery.IsEmptyIgnoreFilter)
                EntityManager.AddComponent<SurfaceCacheTrackedLODGroup>(m_NewLODGroupQuery);

            if (m_LODGroupQuery.IsEmptyIgnoreFilter)
                return;

            // Adding or removing either component is a structural change, so the order version covers existence;
            // only the selected level is read as a value.
            var entityHandle = GetEntityTypeHandle();
            var groupHandle = GetComponentTypeHandle<MeshLODGroupComponent>(true);
            var giLodHandle = GetComponentTypeHandle<GlobalIlluminationLOD>(true);
            var chunks = m_LODGroupQuery.ToArchetypeChunkArray(Allocator.Temp);
            foreach (var chunk in chunks)
            {
                bool hasGiLod = chunk.Has(ref giLodHandle);
                if (!chunk.DidOrderChange(LastSystemVersion)
                    && !(hasGiLod && chunk.DidChange(ref giLodHandle, LastSystemVersion)))
                    continue;

                // A group that lost MeshLODGroupComponent makes its renderers contribute whatever the selection
                // says, so the two absences have to be told apart.
                bool hasGroup = chunk.Has(ref groupHandle);
                int chunkState = hasGroup ? k_NoGlobalIlluminationLOD : k_NoLODGroupComponent;

                var entities = chunk.GetNativeArray(entityHandle);
                var giLods = hasGroup && hasGiLod ? chunk.GetNativeArray(ref giLodHandle) : default;

                for (int i = 0; i < entities.Length; i++)
                {
                    // An out-of-range value selects the same thing as a missing component, so it must not be
                    // recorded verbatim: a hand-authored value could collide with the k_NoLODGroupComponent sentinel.
                    int selected = giLods.IsCreated && GlobalIlluminationLODSelection.IsValidLevel(giLods[i].Value)
                        ? giLods[i].Value
                        : chunkState;
                    if (m_LastGlobalIlluminationLOD.TryGetValue(entities[i], out int previous) && previous == selected)
                        continue;

                    changedGroups.Add(entities[i]);
                    m_LastGlobalIlluminationLOD[entities[i]] = selected;
                }
            }
        }

        int ClassifyTrackedChunks(bool fullResync, NativeHashSet<Entity> changedLODGroups, bool checkCulling, ref ChunkHandles handles)
        {
            int movedCount = 0;
            var changedMeshLodChunks = GatherChangedMeshLodChunks(fullResync);
            var changedOverrideChunks = GatherChangedMaterialOverrideChunks(fullResync);
            var chunks = m_TrackedQuery.ToArchetypeChunkArray(Allocator.Temp);
            foreach (var chunk in chunks)
            {
                if ((changedMeshLodChunks.IsCreated && changedMeshLodChunks.Contains(chunk))
                    || (changedOverrideChunks.IsCreated && changedOverrideChunks.Contains(chunk))
                    || SurfaceCacheTransformChangeJob.NeedsFullRecords(
                        chunk, ref handles.MaterialMeshInfo, ref handles.LODGroupMembership, handles.RenderMeshArray,
                        handles.RenderFilterSettings, LastSystemVersion, fullResync, changedLODGroups))
                {
                    AddFullRecordChunk(chunk, SurfaceCacheEntityCulling.FirstBits(chunk.Count));
                    continue;
                }

                if (chunk.DidChange(ref handles.LocalToWorld, LastSystemVersion))
                {
                    AddChunk(ref m_MovedChunks, chunk);
                    movedCount += chunk.Count;
                }

                if (checkCulling)
                    AddChunk(ref m_CullingCandidateChunks, chunk);
            }

            if (changedMeshLodChunks.IsCreated)
                changedMeshLodChunks.Dispose();
            if (changedOverrideChunks.IsCreated)
                changedOverrideChunks.Dispose();

            return movedCount;
        }

        NativeHashSet<ArchetypeChunk> GatherChangedMaterialOverrideChunks(bool fullResync)
        {
            if (fullResync || !m_HasMaterialOverrideChunks)
                return default;

            NativeHashSet<ArchetypeChunk> changed = default;
            foreach (var chunk in m_MaterialOverrideQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                if (!DidMaterialOverridesChange(chunk))
                    continue;

                if (!changed.IsCreated)
                    changed = new NativeHashSet<ArchetypeChunk>(16, Allocator.Temp);
                changed.Add(chunk);
            }

            return changed;
        }

        bool DidMaterialOverridesChange(in ArchetypeChunk chunk)
        {
            for (int i = 0; i < m_MaterialOverrideHandles.Length; i++)
            {
                if (chunk.Has(ref m_MaterialOverrideHandles[i]) && chunk.DidChange(ref m_MaterialOverrideHandles[i], LastSystemVersion))
                    return true;
            }

            return false;
        }

        bool HasMaterialOverrides(in ArchetypeChunk chunk)
        {
            if (!m_HasMaterialOverrideChunks)
                return false;

            for (int i = 0; i < m_MaterialOverrideHandles.Length; i++)
            {
                if (chunk.Has(ref m_MaterialOverrideHandles[i]))
                    return true;
            }

            return false;
        }

        NativeHashSet<ArchetypeChunk> GatherChangedMeshLodChunks(bool fullResync)
        {
            if (fullResync || m_ChangedMeshLodQuery.IsEmptyIgnoreFilter)
                return default;

            var chunks = m_ChangedMeshLodQuery.ToArchetypeChunkArray(Allocator.Temp);
            if (chunks.Length == 0)
                return default;

            var changed = new NativeHashSet<ArchetypeChunk>(chunks.Length, Allocator.Temp);
            foreach (var chunk in chunks)
                changed.Add(chunk);

            return changed;
        }

        void AddFullRecordChunk(in ArchetypeChunk chunk, in v128 pushMask)
        {
            AddChunk(ref m_FullRecordChunks, chunk);
            if (!m_FullRecordPushMasks.IsCreated)
                m_FullRecordPushMasks = new NativeList<v128>(Allocator.Persistent);

            m_FullRecordPushMasks.Add(pushMask);
        }

        static NativeArray<SurfaceCacheCullingObserver> CopyCullingObservers()
        {
            var source = SurfaceCacheEntityChangeBridge.CullingObservers;
            var observers = new NativeArray<SurfaceCacheCullingObserver>(source.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < source.Length; i++)
                observers[i] = source[i];

            return observers;
        }

        void AddCullingChanges(NativeArray<SurfaceCacheCullingObserver> cullingObservers, ref ChunkHandles handles)
        {
            using var _ = k_CullingMarker.Auto();

            var candidates = m_CullingCandidateChunks.AsArray();
            using var changedMasks = new NativeArray<v128>(candidates.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new SurfaceCacheCullingChangeJob
            {
                Chunks = candidates,
                Observers = cullingObservers,
                WorldRenderBoundsHandle = handles.WorldRenderBounds,
                ChunkWorldRenderBoundsHandle = handles.ChunkWorldRenderBounds,
                ExtractedHandle = handles.Extracted,
                ChangedMasks = changedMasks,
            }.Schedule(candidates.Length, 16).Complete();

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!SurfaceCacheEntityCulling.IsEmpty(changedMasks[i]))
                    AddFullRecordChunk(candidates[i], changedMasks[i]);
            }
        }

        static void AddChunk(ref NativeList<ArchetypeChunk> chunks, in ArchetypeChunk chunk)
        {
            if (!chunks.IsCreated)
                chunks = new NativeList<ArchetypeChunk>(Allocator.Persistent);

            chunks.Add(chunk);
        }

        SurfaceCacheTransformChangeJob CreateTransformJob(NativeList<ArchetypeChunk> movedChunks, ref ChunkHandles handles, int movedCount)
        {
            var transformChanged = SurfaceCacheEntityChangeBridge.AcquireTransformChangedList();

            int upperBound = transformChanged.Length + movedCount;
            if (transformChanged.Capacity < upperBound)
                transformChanged.Capacity = upperBound;

            return new SurfaceCacheTransformChangeJob
            {
                Chunks = movedChunks.AsArray(),
                EntityHandle = handles.Entity,
                LocalToWorldHandle = handles.LocalToWorld,
                TransformChanged = transformChanged.AsParallelWriter(),
            };
        }

        void PushFullRecords(
            NativeArray<SurfaceCacheCullingObserver> cullingObservers,
            NativeList<Entity> culledUntrackedEntities,
            ref ChunkHandles handles)
        {
            var chunks = m_FullRecordChunks;
            var pushMasks = m_FullRecordPushMasks;
            if (chunks.IsEmpty)
                return;

            if (!SurfaceCacheEntityChangeBridge.TryAcquireChangeWriter(out var writer))
            {
                for (int i = 0; i < chunks.Length; i++)
                    PushFullRecords(chunks[i], pushMasks[i], cullingObservers, culledUntrackedEntities, ref handles);
                return;
            }

            var fallbackChunkIndices = new NativeList<int>(Allocator.TempJob);
            var chunkHasMaterialOverrides = new NativeArray<bool>(chunks.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < chunks.Length; i++)
                chunkHasMaterialOverrides[i] = HasMaterialOverrides(chunks[i]);

            new SurfaceCacheFullRecordJob
            {
                Chunks = chunks.AsArray(),
                PushMasks = pushMasks.AsArray(),
                EntityHandle = handles.Entity,
                MaterialMeshInfoHandle = handles.MaterialMeshInfo,
                GlobalIlluminationMeshLodHandle = handles.GlobalIlluminationMeshLod,
                LocalToWorldHandle = handles.LocalToWorld,
                DisableRenderingHandle = handles.DisableRendering,
                DisabledHandle = handles.Disabled,
                LODGroupMembershipHandle = handles.LODGroupMembership,
                ExcludedFromGlobalIlluminationHandle = handles.ExcludedFromGlobalIllumination,
                RenderMeshArrayHandle = handles.RenderMeshArray,
                RenderFilterSettingsHandle = handles.RenderFilterSettings,
                LODGroupLookup = handles.LODGroupLookup,
                GlobalIlluminationLODLookup = handles.GlobalIlluminationLODLookup,
                RenderMeshArrays = m_RenderMeshArrayTable.Entries,
                WorldRenderBoundsHandle = handles.WorldRenderBounds,
                ChunkWorldRenderBoundsHandle = handles.ChunkWorldRenderBounds,
                CullingObservers = cullingObservers,
                ExtractedHandle = handles.Extracted,
                CulledUntrackedEntities = culledUntrackedEntities,
                Writer = writer,
                ChunkHasMaterialOverrides = chunkHasMaterialOverrides,
                FallbackChunkIndices = fallbackChunkIndices,
            }.Run();

            foreach (int index in fallbackChunkIndices)
                PushFullRecords(chunks[index], pushMasks[index], cullingObservers, culledUntrackedEntities, ref handles);
            fallbackChunkIndices.Dispose();
            chunkHasMaterialOverrides.Dispose();
        }

        void PushFullRecords(
            in ArchetypeChunk chunk,
            in v128 pushMask,
            NativeArray<SurfaceCacheCullingObserver> cullingObservers,
            NativeList<Entity> culledUntrackedEntities,
            ref ChunkHandles handles)
        {
            int renderMeshArrayIndex = chunk.GetSharedComponentIndex(handles.RenderMeshArray);
            SurfaceCacheRenderMeshArrayTable.Entry entry = default;
            bool hasEntry = renderMeshArrayIndex != -1
                && m_RenderMeshArrayTable.Entries.TryGetValue(renderMeshArrayIndex, out entry);
            uint renderingLayerMask = chunk.Has(handles.RenderFilterSettings)
                ? chunk.GetSharedComponent(handles.RenderFilterSettings).RenderingLayerMask
                : RenderFilterSettings.Default.RenderingLayerMask;
            bool chunkIsHidden = chunk.Has(ref handles.DisableRendering) || chunk.Has(ref handles.Disabled);
            bool chunkHasLODGroupMembership = chunk.Has(ref handles.LODGroupMembership);
            bool chunkIsExcluded = chunk.Has(ref handles.ExcludedFromGlobalIllumination);

            var entities = chunk.GetNativeArray(handles.Entity);
            var materialMeshInfos = chunk.GetNativeArray(ref handles.MaterialMeshInfo);
            var giMeshLods = chunk.Has(ref handles.GlobalIlluminationMeshLod) ? chunk.GetNativeArray(ref handles.GlobalIlluminationMeshLod) : default;
            var localToWorlds = chunk.GetNativeArray(ref handles.LocalToWorld);
            var lodGroupMemberships = chunkHasLODGroupMembership ? chunk.GetNativeArray(ref handles.LODGroupMembership) : default;
            v128 culled = SurfaceCacheEntityCulling.RecordCulledState(
                chunk, pushMask, ref handles.WorldRenderBounds, ref handles.ChunkWorldRenderBounds, ref handles.Extracted,
                cullingObservers, entities, culledUntrackedEntities);
            int chunkOverrideTypeCount = GatherChunkOverrideValues(chunk);

            for (int i = 0; i < chunk.Count; i++)
            {
                if (!SurfaceCacheEntityCulling.IsBitSet(pushMask, i))
                    continue;

                bool inGlobalIlluminationLod = !chunkIsExcluded
                    && (!chunkHasLODGroupMembership || GlobalIlluminationLODSelection.IsInGlobalIlluminationLOD(
                        lodGroupMemberships[i], handles.LODGroupLookup, handles.GlobalIlluminationLODLookup));
                var record = new SurfaceCacheEntityInstanceRecord
                {
                    Key = entities[i],
                    MeshLod = giMeshLods.IsCreated ? giMeshLods[i].Value : 0,
                    LocalToWorld = localToWorlds[i].Value,
                    RenderingLayerMask = renderingLayerMask,
                    Visible = !chunkIsHidden
                        && inGlobalIlluminationLod
                        && !SurfaceCacheEntityCulling.IsBitSet(culled, i)
                        && chunk.IsComponentEnabled(ref handles.MaterialMeshInfo, i),
                };

                ResolveMeshAndMaterials(materialMeshInfos[i], hasEntry, entry, ref record);
                ReadOverrides(i, chunkOverrideTypeCount);
                SurfaceCacheEntityChangeBridge.PushChanged(
                    record,
                    new ReadOnlySpan<SurfaceCacheSubMeshMaterial>(m_MaterialScratch, 0, m_MaterialScratchCount),
                    new ReadOnlySpan<SurfaceCacheMaterialOverride>(m_OverrideScratch, 0, chunkOverrideTypeCount));
            }
        }

        int GatherChunkOverrideValues(in ArchetypeChunk chunk)
        {
            int count = 0;
            if (!m_HasMaterialOverrideChunks)
                return count;

            for (int i = 0; i < m_MaterialOverrideTypes.Length; i++)
            {
                if (!chunk.Has(ref m_MaterialOverrideHandles[i]))
                    continue;

                m_ChunkOverrideValues[count] = chunk.GetDynamicComponentDataArrayReinterpret<float>(
                    ref m_MaterialOverrideHandles[i], m_MaterialOverrideTypes[i].FloatCount * sizeof(float));
                m_ChunkOverrideTypes[count] = i;
                count++;
            }

            return count;
        }

        void ReadOverrides(int entityIndex, int chunkOverrideTypeCount)
        {
            for (int i = 0; i < chunkOverrideTypeCount; i++)
            {
                var type = m_MaterialOverrideTypes[m_ChunkOverrideTypes[i]];
                var values = m_ChunkOverrideValues[i];
                var value = Vector4.zero;
                for (int component = 0; component < type.FloatCount; component++)
                    value[component] = values[entityIndex * type.FloatCount + component];

                m_OverrideScratch[i] = new SurfaceCacheMaterialOverride { NameId = type.NameId, Value = value };
            }
        }

        void ResolveMeshAndMaterials(
            in MaterialMeshInfo materialMeshInfo,
            bool hasEntry,
            in SurfaceCacheRenderMeshArrayTable.Entry entry,
            ref SurfaceCacheEntityInstanceRecord record)
        {
            m_MaterialScratchCount = 0;

            EntityId mesh;
            if (materialMeshInfo.IsRuntimeMesh)
            {
                // Not carried by a RenderMeshArray, so the side table cannot resolve these.
                var runtimeMesh = m_RendererSystem?.GetMesh(materialMeshInfo.MeshID);
                mesh = runtimeMesh != null ? runtimeMesh.GetEntityId() : EntityId.None;
            }
            else if (hasEntry)
            {
                mesh = SurfaceCacheRenderMeshArrayTable.GetMesh(entry, materialMeshInfo);
            }
            else
            {
                mesh = EntityId.None;
            }

            record.Mesh = mesh;
            if (mesh == EntityId.None)
                return;

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                if (!hasEntry)
                    return;

                var range = materialMeshInfo.MaterialMeshIndexRange;
                if (range.length <= 0 || (uint)range.start >= (uint)entry.MaterialMeshIndices.Length)
                    return;

                // Entries referencing a mesh other than the range's first are ignored, matching GetMesh.
                int firstMeshIndex = entry.MaterialMeshIndices[range.start].MeshIndex;
                int end = range.start + range.length;
                if (end > entry.MaterialMeshIndices.Length)
                    end = entry.MaterialMeshIndices.Length;

                for (int i = range.start; i < end; i++)
                {
                    var materialMeshIndex = entry.MaterialMeshIndices[i];
                    if (materialMeshIndex.MeshIndex != firstMeshIndex)
                        continue;

                    var material = (uint)materialMeshIndex.MaterialIndex < (uint)entry.Materials.Length
                        ? entry.Materials[materialMeshIndex.MaterialIndex]
                        : EntityId.None;

                    AddScratchMaterial(materialMeshIndex.SubMeshIndex, material);
                }
            }
            else
            {
                EntityId material;
                if (materialMeshInfo.IsRuntimeMaterial)
                {
                    var runtimeMaterial = m_RendererSystem?.GetMaterial(materialMeshInfo.MaterialID);
                    material = runtimeMaterial != null ? runtimeMaterial.GetEntityId() : EntityId.None;
                }
                else if (hasEntry)
                {
                    material = SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, materialMeshInfo);
                }
                else
                {
                    material = EntityId.None;
                }

                AddScratchMaterial(materialMeshInfo.SubMesh, material);
            }
        }

        void AddScratchMaterial(int subMeshIndex, EntityId material)
        {
            if (m_MaterialScratchCount == m_MaterialScratch.Length)
                Array.Resize(ref m_MaterialScratch, m_MaterialScratch.Length * 2);

            m_MaterialScratch[m_MaterialScratchCount++] = new SurfaceCacheSubMeshMaterial
            {
                SubMeshIndex = subMeshIndex,
                Material = material,
            };
        }
    }
}

#endif
