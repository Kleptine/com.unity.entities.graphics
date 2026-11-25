using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.Rendering
{
    [WorldSystemFilter(WorldSystemFilterFlags.EntitySceneOptimizations)]
    [UpdateBefore(typeof(FreezeStaticLODObjects))]
    partial class FreezeStaticGlobalIlluminationLOD : SystemBase
    {
        EntityQueryMask m_StrippedByFreeze;

        /// <inheritdoc/>
        protected override void OnCreate()
        {
            // The same predicate FreezeStaticLODObjects applies: Static, and not skipped as Disabled or Prefab.
            m_StrippedByFreeze = GetEntityQuery(ComponentType.ReadOnly<Static>()).GetEntityQueryMask();
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            CompleteDependency();

            var lodGroups = GetComponentLookup<MeshLODGroupComponent>(true);
            var globalIlluminationLODs = GetComponentLookup<GlobalIlluminationLOD>(true);

            var excluded = new NativeList<Entity>(Allocator.Temp);
            foreach (var (lodGroupMembership, entity) in SystemAPI.Query<RefRO<MeshLODComponent>>()
                         .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                         .WithEntityAccess())
            {
                bool rendererIsStripped = m_StrippedByFreeze.MatchesIgnoreFilter(entity);
                bool groupIsStripped = m_StrippedByFreeze.MatchesIgnoreFilter(lodGroupMembership.ValueRO.Group);
                if (!rendererIsStripped && !groupIsStripped)
                    continue;

                if (!GlobalIlluminationLODSelection.IsInGlobalIlluminationLOD(lodGroupMembership.ValueRO, lodGroups, globalIlluminationLODs))
                    excluded.Add(entity);
            }

            EntityManager.AddComponent<ExcludedFromGlobalIllumination>(excluded.AsArray());
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.EntitySceneOptimizations)]
    [UpdateAfter(typeof(LODRequirementsUpdateSystem))]
    partial class FreezeStaticLODObjects : SystemBase
    {
        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            var group = GetEntityQuery(
                new EntityQueryDesc
                {
                    Any = new ComponentType[] { typeof(MeshLODGroupComponent), typeof(MeshLODComponent), typeof(LODGroupWorldReferencePoint) },
                    All = new ComponentType[] { typeof(Static) }
                });

            EntityManager.RemoveComponent(group, new ComponentTypeSet(typeof(MeshLODGroupComponent), typeof(MeshLODComponent), typeof(LODGroupWorldReferencePoint)));
        }
    }
}
