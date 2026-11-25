using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    /// <summary>
    /// Represents an LOD group.
    /// </summary>
    /// <remarks>
    /// Each MeshLODGroupComponent contains multiple MeshLODComponents and can also have multiple child groups.
    /// </remarks>
    public struct MeshLODGroupComponent : IComponentData
    {
        /// <summary>
        /// The LOD parent group.
        /// </summary>
        public Entity    ParentGroup;

        /// <summary>
        /// The LOD mask.
        /// </summary>
        /// <remarks>
        /// Each bit matches with one of the 8 possible LOD levels.
        /// </remarks>
        public int       ParentMask;

        /// <summary>
        /// The LOD distances for the four closest LODS.
        /// </summary>
        public float4    LODDistances0;

        /// <summary>
        /// The LOD distances for the four furthest LODS.
        /// </summary>
        public float4    LODDistances1;

        /// <summary>
        /// The local reference point which Entities Graphics uses to calculate the distance from the camera to the LOD group.
        /// </summary>
        public float3    LocalReferencePoint;
    }

    /// <summary>
    /// Selects which LOD level of a LOD group contributes to global illumination.
    /// </summary>
    /// <remarks>
    /// This component sits on the same entity as <see cref="MeshLODGroupComponent"/>. A group that does not
    /// contribute to global illumination does not have this component at all.
    /// </remarks>
    public struct GlobalIlluminationLOD : IComponentData
    {
        /// <summary>
        /// The index of the LOD level that contributes to global illumination.
        /// </summary>
        public int Value;
    }

    /// <summary>
    /// Selects which Mesh LOD level of the entity's mesh contributes to global illumination.
    /// </summary>
    /// <remarks>
    /// Baking adds this component to entities whose mesh has more than one Mesh LOD level, with the value of
    /// <see cref="UnityEngine.MeshRenderer.globalIlluminationMeshLod"/>. An entity without it uses level 0.
    /// A value at or beyond the mesh's Mesh LOD count selects the last level.
    /// </remarks>
    public struct GlobalIlluminationMeshLod : IComponentData
    {
        /// <summary>
        /// The index of the Mesh LOD level that contributes to global illumination.
        /// </summary>
        public int Value;
    }

    internal struct ExcludedFromGlobalIllumination : IComponentData
    {
    }

    /// <summary>
    /// An unmanaged component that represents a world reference point to use for LOD group.
    /// </summary>
    internal struct LODGroupWorldReferencePoint : IComponentData
    {
        /// <summary>
        /// The world-space x, y, and z position of the reference point.
        /// </summary>
        public float3 Value;
    }

    internal struct SkipLODGroupWorldReferencePointUpdate : IComponentData
    {

    }

    /// <summary>
    /// A component that stores data for managing Level of Detail (LOD) for LODGroups.
    /// This is only used in the context of LODGroup and not Mesh LOD.
    /// </summary>
    public struct MeshLODComponent : IComponentData
    {
        /// <summary>
        /// The LOD group entity.
        /// </summary>
        public Entity   Group;

        /// <summary>
        /// The LOD parent group. This is used internally to optimize the LOD system.
        /// </summary>
        public Entity   ParentGroup;

        /// <summary>
        /// The LOD mask.
        /// </summary>
        public int      LODMask;
    }

    internal struct MeshLodInfoComponent : IComponentData
    {
        public int lodCount;
        public float lodSlope;
        public float lodBias;
    }

    internal static class GlobalIlluminationLODSelection
    {
        const int k_MaximumLODLevels = 8;

        public static bool IsValidLevel(int level)
        {
            return (uint)level < k_MaximumLODLevels;
        }

        public static bool IsInGlobalIlluminationLOD(
            in MeshLODComponent lodGroupMembership,
            in ComponentLookup<MeshLODGroupComponent> lodGroups,
            in ComponentLookup<GlobalIlluminationLOD> globalIlluminationLODs)
        {
            if (!lodGroups.HasComponent(lodGroupMembership.Group))
                return true;

            if (!globalIlluminationLODs.TryGetComponent(lodGroupMembership.Group, out var globalIlluminationLOD))
                return false;

            if (!IsValidLevel(globalIlluminationLOD.Value))
                return false;

            return (lodGroupMembership.LODMask & (1 << globalIlluminationLOD.Value)) != 0;
        }
    }
}
