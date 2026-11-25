#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unity.Entities.Graphics.Tests.SurfaceCache
{
    internal partial class SurfaceCacheProducerTests
    {
        const int k_ParallelCount = 2 * SurfaceCacheMeshInstanceProducerSystem.k_ParallelTransformThreshold;

        World m_World;
        EntityManager m_Manager;
        SurfaceCacheMeshInstanceProducerSystem m_System;
        Mesh m_Mesh;
        Material m_Material;
        bool m_ConsumerRegistered;

        readonly List<SurfaceCacheEntityInstanceRecord> m_Changed = new();
        readonly List<SurfaceCacheSubMeshMaterial> m_ChangedMaterials = new();
        readonly List<SurfaceCacheMaterialOverride> m_ChangedOverrides = new();
        readonly List<SurfaceCacheEntityTransformRecord> m_TransformChanged = new();
        readonly List<EntityId> m_Destroyed = new();

        [SetUp]
        public void SetUp()
        {
            Assume.That(!SurfaceCacheEntityChangeBridge.IsConsumerPresent,
                "These tests require exclusive ownership of the SurfaceCacheEntityChangeBridge.");

            m_World = new World("SurfaceCacheProducerTests");
            m_Manager = m_World.EntityManager;
            m_System = m_World.GetOrCreateSystemManaged<SurfaceCacheMeshInstanceProducerSystem>();
            // Force-enable: the RenderMeshArray paths under test have no GPU dependency.
            m_System.Enabled = true;

            m_Mesh = new Mesh { name = "SurfaceCacheProducerTests Mesh" };
            m_Mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            m_Mesh.triangles = new[] { 0, 1, 2 };
            m_Material = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "SurfaceCacheProducerTests Material" };

            Assert.IsTrue(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
            m_ConsumerRegistered = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_ConsumerRegistered)
            {
                SurfaceCacheEntityChangeBridge.UnregisterConsumer();
                m_ConsumerRegistered = false;
            }

            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();

            Object.DestroyImmediate(m_Mesh);
            Object.DestroyImmediate(m_Material);

            m_Changed.Clear();
            m_ChangedMaterials.Clear();
            m_ChangedOverrides.Clear();
            m_TransformChanged.Clear();
            m_Destroyed.Clear();
        }

        [Test]
        public void NewEntity_FullRecordIsPushed()
        {
            var entity = CreateRenderableEntity();

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A new renderable entity should produce exactly one full record.");
            Assert.AreEqual(0, m_TransformChanged.Count);
            Assert.AreEqual(0, m_Destroyed.Count);

            var record = m_Changed[0];
            Assert.AreEqual(MakeKey(entity), record.Key);
            Assert.AreEqual(m_Mesh.GetEntityId(), record.Mesh, "The record should carry the mesh resolved from the RenderMeshArray.");
            // Assignments are sparse: one entry, for the submesh the MaterialMeshInfo names. Padding out to
            // subMeshCount is the consumer's job because only the resolved Mesh knows that count.
            Assert.AreEqual(1, record.MaterialsCount, "One submesh material assignment should be reported.");
            Assert.AreEqual(0, m_ChangedMaterials[record.MaterialsStart].SubMeshIndex);
            Assert.AreEqual(m_Material.GetEntityId(), m_ChangedMaterials[record.MaterialsStart].Material,
                "The record should carry the material resolved from the RenderMeshArray.");
            Assert.AreEqual((Matrix4x4)float4x4.identity, record.LocalToWorld);
            Assert.IsTrue(record.Visible, "An enabled entity should be reported as visible.");
        }

        [Test]
        public void UnchangedEntity_NothingIsPushed()
        {
            var entity = CreateRenderableEntity();
            UpdateAndDrain();
            Assert.IsTrue(m_Manager.HasComponent<SurfaceCacheTrackedInstance>(entity), "The entity should be tracked after the first update.");

            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count, "An unchanged entity should not produce records.");
            Assert.AreEqual(0, m_TransformChanged.Count);
            Assert.AreEqual(0, m_Destroyed.Count);
        }

        [Test]
        public void TransformChanged_TransformRecordIsPushed()
        {
            var entity = CreateRenderableEntity();
            UpdateAndDrain();

            var newTransform = float4x4.Translate(new float3(1f, 2f, 3f));
            m_Manager.SetComponentData(entity, new LocalToWorld { Value = newTransform });
            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count, "A transform-only change should not produce a full record.");
            Assert.AreEqual(1, m_TransformChanged.Count, "A transform-only change should produce a transform record.");
            Assert.AreEqual(MakeKey(entity), m_TransformChanged[0].Key);
            Assert.AreEqual((Matrix4x4)newTransform, m_TransformChanged[0].LocalToWorld);
        }

        [Test]
        public void TransformChangedAcrossManyChunks_EachEntityReportsOnceWithItsTransform()
        {
            const int count = 2 * SurfaceCacheMeshInstanceProducerSystem.k_ParallelTransformThreshold;
            var entities = new Entity[count];
            for (int i = 0; i < count; i++)
                entities[i] = CreateRenderableEntity();
            UpdateAndDrain();

            for (int i = 0; i < count; i++)
                m_Manager.SetComponentData(entities[i], new LocalToWorld { Value = float4x4.Translate(new float3(i, 0f, 0f)) });
            UpdateAndDrain();

            Assert.Greater(m_Manager.CreateEntityQuery(typeof(LocalToWorld)).CalculateChunkCount(), 1,
                "The entities should span several chunks, so several workers write transform records at once.");
            Assert.AreEqual(count, m_TransformChanged.Count);

            var expected = new Dictionary<EntityId, Matrix4x4>();
            for (int i = 0; i < count; i++)
                expected.Add(MakeKey(entities[i]), (Matrix4x4)float4x4.Translate(new float3(i, 0f, 0f)));

            foreach (var record in m_TransformChanged)
            {
                Assert.IsTrue(expected.Remove(record.Key, out var localToWorld), "Each entity should report exactly once.");
                Assert.AreEqual(localToWorld, record.LocalToWorld);
            }
        }

        [Test]
        public void TransformChangedInSameUpdateAsNewEntity_BothAreReported([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            var added = CreateRenderableEntity();
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "The new entity should produce a full record.");
            Assert.AreEqual(MakeKey(added), m_Changed[0].Key);
            AssertTransformsAre(moved, 1f, "The moves should still produce transform records.");
        }

        [Test]
        public void EarlierSystemStillWritingInstances_ProducerWaitsForIt()
        {
            var entities = CreateRenderableEntities(16);
            UpdateAndDrain();

            m_World.GetOrCreateSystemManaged<PendingMaterialMeshInfoWriterSystem>().Update();
            UpdateAndDrain();

            Assert.AreEqual(entities.Length, m_Changed.Count, "The rewritten instances should produce full records.");
            AssertKeysAre(entities, m_Changed);
        }

        [Test]
        public void TransformChangedTwiceWithoutDrain_RecordsArriveInUpdateOrder([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();
            MoveAll(entities, 2f);
            UpdateAndDrain();

            Assert.AreEqual(2 * count, m_TransformChanged.Count);
            var steps = new Dictionary<EntityId, List<float>>();
            foreach (var record in m_TransformChanged)
            {
                if (!steps.TryGetValue(record.Key, out var list))
                    steps.Add(record.Key, list = new List<float>());
                list.Add(record.LocalToWorld.m13);
            }

            Assert.AreEqual(count, steps.Count);
            foreach (var list in steps.Values)
                CollectionAssert.AreEqual(new[] { 1f, 2f }, list, "The newest transform should be applied last.");
        }

        [Test]
        public void TransformAndFullRecordWithoutDrain_FullRecordSupersedesTransform([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            foreach (var entity in entities)
                m_Manager.SetComponentEnabled<MaterialMeshInfo>(entity, false);
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "The dirtied chunks should produce a full record per instance.");
            Assert.AreEqual(0, m_TransformChanged.Count,
                "A transform record superseded by a full record for the same key should be dropped at drain.");
        }

        [Test]
        public void TransformThenDestroyedWithoutDrain_TransformIsDropped([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            foreach (var entity in entities)
                m_Manager.DestroyEntity(entity);
            UpdateAndDrain();

            Assert.AreEqual(count, m_Destroyed.Count, "Destroying the entities should report them as destroyed.");
            Assert.AreEqual(0, m_TransformChanged.Count,
                "A transform record for a destroyed key should be dropped at drain.");
        }

        [Test]
        public void MaterialMeshInfoDisabled_InvisibleRecordIsPushed()
        {
            var entity = CreateRenderableEntity();
            UpdateAndDrain();

            m_Manager.SetComponentEnabled<MaterialMeshInfo>(entity, false);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Disabling MaterialMeshInfo should produce a full record.");
            Assert.IsFalse(m_Changed[0].Visible, "The record should report the entity as invisible.");
        }

        [Test]
        public void DisableRenderingAdded_InvisibleRecordIsPushed()
        {
            var entity = CreateRenderableEntity();
            UpdateAndDrain();

            m_Manager.AddComponent<DisableRendering>(entity);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Adding DisableRendering should produce a full record.");
            Assert.IsFalse(m_Changed[0].Visible, "The record should report the entity as invisible.");

            m_Manager.RemoveComponent<DisableRendering>(entity);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Removing DisableRendering should produce a full record.");
            Assert.IsTrue(m_Changed[0].Visible, "The record should report the entity as visible again.");
        }

        [Test]
        public void RenderMeshArrayChanged_FullRecordCarriesNewMaterial()
        {
            var entity = CreateRenderableEntity();
            UpdateAndDrain();

            var otherMaterial = new Material(m_Material) { name = "SurfaceCacheProducerTests Other Material" };
            try
            {
                m_Manager.SetSharedComponent(entity, new RenderMeshArray(new[] { otherMaterial }, new[] { m_Mesh }));
                UpdateAndDrain();

                Assert.AreEqual(1, m_Changed.Count, "A RenderMeshArray change should produce a full record.");
                Assert.AreEqual(otherMaterial.GetEntityId(),
                    m_ChangedMaterials[m_Changed[0].MaterialsStart].Material, "The record should carry the new material.");
            }
            finally
            {
                Object.DestroyImmediate(otherMaterial);
            }
        }

        [Test]
        public void MaterialPropertyComponent_Present_RecordCarriesOverride([Values] bool chunkHasRuntimeIds)
        {
            var entity = CreateRenderableEntity();
            var emission = new float4(8f, 0f, 0f, 1f);
            m_Manager.AddComponentData(entity, new URPMaterialPropertyEmissionColor { Value = emission });
            if (chunkHasRuntimeIds)
                CreateRuntimeIdTwin(entity);

            UpdateAndDrain();

            var overrides = OverridesOf(RecordFor(entity));
            Assert.AreEqual(1, overrides.Count, "The record should carry the material property component as an override.");
            Assert.AreEqual(Shader.PropertyToID("_EmissionColor"), overrides[0].NameId);
            Assert.AreEqual((Vector4)emission, overrides[0].Value);
        }

        [Test]
        public void MaterialPropertyComponent_Changed_FullRecordCarriesNewOverride()
        {
            var entity = CreateRenderableEntity();
            m_Manager.AddComponentData(entity, new URPMaterialPropertyEmissionColor { Value = new float4(8f, 0f, 0f, 1f) });
            UpdateAndDrain();

            var emission = new float4(0f, 0f, 8f, 1f);
            m_Manager.SetComponentData(entity, new URPMaterialPropertyEmissionColor { Value = emission });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A material property component change should produce a full record.");
            var overrides = OverridesOf(m_Changed[0]);
            Assert.AreEqual(1, overrides.Count);
            Assert.AreEqual((Vector4)emission, overrides[0].Value, "The record should carry the new override value.");
        }

        [Test]
        public void MaterialPropertyComponent_Missing_RecordCarriesNoOverrides()
        {
            CreateRenderableEntity();

            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed[0].OverridesCount);
        }

        [Test]
        public void MaterialMeshIndexRange_RecordCarriesOneAssignmentPerSubMeshOfTheFirstMesh([Values] bool chunkHasRuntimeIds)
        {
            var otherMesh = new Mesh { name = "SurfaceCacheProducerTests Other Mesh" };
            var secondMaterial = new Material(m_Material) { name = "SurfaceCacheProducerTests Second Material" };
            try
            {
                var indices = new[]
                {
                    new MaterialMeshIndex { MaterialIndex = 0, MeshIndex = 0, SubMeshIndex = 0 },
                    new MaterialMeshIndex { MaterialIndex = 1, MeshIndex = 0, SubMeshIndex = 1 },
                    new MaterialMeshIndex { MaterialIndex = 0, MeshIndex = 1, SubMeshIndex = 0 },
                    new MaterialMeshIndex { MaterialIndex = 2, MeshIndex = 0, SubMeshIndex = 2 },
                };
                var entity = CreateRenderableEntity();
                m_Manager.SetSharedComponent(entity,
                    new RenderMeshArray(new[] { m_Material, secondMaterial }, new[] { m_Mesh, otherMesh }, indices));
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromMaterialMeshIndexRange(0, indices.Length));
                if (chunkHasRuntimeIds)
                    CreateRuntimeIdTwin(entity);
                UpdateAndDrain();

                var record = RecordFor(entity);
                Assert.AreEqual(m_Mesh.GetEntityId(), record.Mesh, "A ranged instance should resolve to the first mesh of its range.");

                var materials = MaterialsOf(record);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, materials.Select(material => material.SubMeshIndex),
                    "Entries for another mesh should be skipped.");
                CollectionAssert.AreEqual(
                    new[] { m_Material.GetEntityId(), secondMaterial.GetEntityId(), EntityId.None },
                    materials.Select(material => material.Material),
                    "An out-of-range material index should resolve to None.");
            }
            finally
            {
                Object.DestroyImmediate(otherMesh);
                Object.DestroyImmediate(secondMaterial);
            }
        }

        [Test]
        public void ChunkWithRuntimeIds_EveryInstanceIsResolved()
        {
            var entities = CreateRenderableEntities(4);
            var chunkmates = CreateRenderableEntities(4);
            foreach (var entity in chunkmates)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            var runtime = CreateRuntimeIdTwin(chunkmates[0]);
            UpdateAndDrain();

            Assert.AreEqual(entities.Length + chunkmates.Length + 1, m_Changed.Count, "Every new instance should produce a full record.");

            var runtimeRecord = RecordFor(runtime);
            Assert.AreEqual(EntityId.None, runtimeRecord.Mesh, "Without Entities Graphics a runtime mesh id cannot be resolved.");
            Assert.AreEqual(0, runtimeRecord.MaterialsCount);
            Assert.IsTrue(runtimeRecord.Visible);

            foreach (var entity in entities.Concat(chunkmates))
                AssertResolvesToTestAssets(RecordFor(entity));
        }

        [Test]
        public void RenderFilterSettings_RecordCarriesRenderingLayerMask([Values] bool chunkHasRuntimeIds)
        {
            const uint renderingLayerMask = 1u << 5;
            var entity = CreateRenderableEntity();
            var filter = RenderFilterSettings.Default;
            filter.RenderingLayerMask = renderingLayerMask;
            m_Manager.AddSharedComponent(entity, filter);
            if (chunkHasRuntimeIds)
                CreateRuntimeIdTwin(entity);
            UpdateAndDrain();

            var record = RecordFor(entity);
            Assert.AreEqual(renderingLayerMask, record.RenderingLayerMask);
            AssertResolvesToTestAssets(record);
        }

        [Test]
        public void GlobalIlluminationMeshLod_Present_RecordCarriesLevel([Values] bool chunkHasRuntimeIds)
        {
            var withoutMeshLod = CreateRenderableEntity();
            var withMeshLod = CreateRenderableEntity();
            m_Manager.AddComponentData(withMeshLod, new GlobalIlluminationMeshLod { Value = 1 });
            if (chunkHasRuntimeIds)
                CreateRuntimeIdTwin(withMeshLod);
            UpdateAndDrain();

            Assert.AreEqual(0, RecordFor(withoutMeshLod).MeshLod, "An entity without the component should use Mesh LOD 0.");
            Assert.AreEqual(1, RecordFor(withMeshLod).MeshLod, "The record should carry the baked GI Mesh LOD.");
        }

        [Test]
        public void GlobalIlluminationMeshLod_Changed_FullRecordIsRepushed()
        {
            var entity = CreateRenderableEntity();
            m_Manager.AddComponentData(entity, new GlobalIlluminationMeshLod { Value = 0 });
            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed[0].MeshLod);

            m_Manager.SetComponentData(entity, new GlobalIlluminationMeshLod { Value = 1 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A GI Mesh LOD change should re-push the entity's record.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.AreEqual(1, m_Changed[0].MeshLod, "The record should reflect the new GI Mesh LOD.");
        }

        [Test]
        public void DisabledAdded_InvisibleRecordIsPushed([Values] bool chunkHasRuntimeIds)
        {
            var entity = CreateRenderableEntity();
            var chunk = chunkHasRuntimeIds ? new[] { entity, CreateRuntimeIdTwin(entity) } : new[] { entity };
            UpdateAndDrain();

            using var entities = new NativeArray<Entity>(chunk, Allocator.Temp);
            m_Manager.AddComponent<Disabled>(entities);
            UpdateAndDrain();

            Assert.AreEqual(chunk.Length, m_Changed.Count, "Adding Disabled should produce a full record.");
            Assert.IsFalse(RecordFor(entity).Visible, "The record should report the entity as invisible.");

            m_Manager.RemoveComponent<Disabled>(entities);
            UpdateAndDrain();

            Assert.AreEqual(chunk.Length, m_Changed.Count, "Removing Disabled should produce a full record.");
            Assert.IsTrue(RecordFor(entity).Visible, "The record should report the entity as visible again.");
        }

        // A query-wide shared component change rewrites whole chunks in place, without bumping their order version.
        [Test]
        public void RenderMeshArraySetOnWholeChunks_FullRecordsCarryNewMaterial([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            var otherMaterial = new Material(m_Material) { name = "SurfaceCacheProducerTests Other Material" };
            try
            {
                using (var query = m_Manager.CreateEntityQuery(typeof(RenderMeshArray)))
                    m_Manager.SetSharedComponent(query, new RenderMeshArray(new[] { otherMaterial }, new[] { m_Mesh }));
                UpdateAndDrain();

                AssertKeysAre(entities, m_Changed);
                foreach (var record in m_Changed)
                    Assert.AreEqual(otherMaterial.GetEntityId(), MaterialsOf(record)[0].Material, "The records should carry the new material.");
            }
            finally
            {
                Object.DestroyImmediate(otherMaterial);
            }
        }

        [Test]
        public void RenderFilterSettingsSetOnWholeChunks_FullRecordsCarryNewRenderingLayerMask([Values(1, k_ParallelCount)] int count)
        {
            const uint renderingLayerMask = 1u << 5;
            var entities = CreateRenderableEntities(count);
            using var query = m_Manager.CreateEntityQuery(typeof(RenderMeshArray));
            m_Manager.AddSharedComponent(query, RenderFilterSettings.Default);
            UpdateAndDrain();

            var filter = RenderFilterSettings.Default;
            filter.RenderingLayerMask = renderingLayerMask;
            m_Manager.SetSharedComponent(query, filter);
            UpdateAndDrain();

            AssertKeysAre(entities, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(renderingLayerMask, record.RenderingLayerMask, "The records should carry the new rendering layer mask.");
        }

        [Test]
        public void InstancesSharingAChunk_EachResolvesItsOwnMaterialMeshInfo([Values] bool chunkHasRuntimeIds)
        {
            var otherMesh = new Mesh { name = "SurfaceCacheProducerTests Other Mesh" };
            var otherMaterial = new Material(m_Material) { name = "SurfaceCacheProducerTests Other Material" };
            try
            {
                var first = CreateRenderableEntity();
                m_Manager.SetSharedComponent(first,
                    new RenderMeshArray(new[] { m_Material, otherMaterial }, new[] { m_Mesh, otherMesh }));
                var second = m_Manager.Instantiate(first);
                m_Manager.SetComponentData(second, MaterialMeshInfo.FromRenderMeshArrayIndices(1, 1));
                var hidden = m_Manager.Instantiate(first);
                m_Manager.SetComponentEnabled<MaterialMeshInfo>(hidden, false);
                if (chunkHasRuntimeIds)
                    CreateRuntimeIdTwin(first);
                UpdateAndDrain();

                var firstRecord = RecordFor(first);
                Assert.AreEqual(m_Mesh.GetEntityId(), firstRecord.Mesh);
                Assert.AreEqual(m_Material.GetEntityId(), MaterialsOf(firstRecord)[0].Material);
                Assert.IsTrue(firstRecord.Visible);

                var secondRecord = RecordFor(second);
                Assert.AreEqual(otherMesh.GetEntityId(), secondRecord.Mesh, "Each instance should resolve its own mesh.");
                Assert.AreEqual(otherMaterial.GetEntityId(), MaterialsOf(secondRecord)[0].Material, "Each instance should resolve its own material.");
                Assert.IsTrue(secondRecord.Visible);

                Assert.IsFalse(RecordFor(hidden).Visible, "Each instance should report its own MaterialMeshInfo enabled state.");
            }
            finally
            {
                Object.DestroyImmediate(otherMesh);
                Object.DestroyImmediate(otherMaterial);
            }
        }

        [Test]
        public void RuntimeIdsWithEntitiesGraphics_ResolveToTheRegisteredAssets()
        {
            Assume.That(EntitiesGraphicsSystem.EntitiesGraphicsEnabled, "Requires Entities Graphics to be supported.");
            var rendererSystem = m_World.GetOrCreateSystemManaged<EntitiesGraphicsSystem>();
            var mesh = rendererSystem.RegisterMesh(m_Mesh);
            var material = rendererSystem.RegisterMaterial(m_Material);
            try
            {
                var entity = CreateRenderableEntity();
                m_Manager.SetComponentData(entity, new MaterialMeshInfo(material, mesh));
                UpdateAndDrain();

                AssertResolvesToTestAssets(RecordFor(entity));
            }
            finally
            {
                rendererSystem.UnregisterMesh(mesh);
                rendererSystem.UnregisterMaterial(material);
            }
        }

        [Test]
        public void EntityDestroyed_DestroyedKeyIsPushedAndTrackingIsRemoved()
        {
            var entity = CreateRenderableEntity();
            var key = MakeKey(entity);
            UpdateAndDrain();

            m_Manager.DestroyEntity(entity);
            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count);
            Assert.AreEqual(1, m_Destroyed.Count, "A destroyed entity should produce a destroyed key.");
            Assert.AreEqual(key, m_Destroyed[0]);

            using var trackedQuery = m_Manager.CreateEntityQuery(typeof(SurfaceCacheTrackedInstance));
            Assert.AreEqual(0, trackedQuery.CalculateEntityCount(), "The cleanup component should be removed so the entity fully dies.");
        }

        [Test]
        public void TrackedEntity_AcquiresDeformedMeshIndex_IsDestroyedAndNotRetracked()
        {
            var entity = CreateRenderableEntity();
            var key = MakeKey(entity);
            UpdateAndDrain();

            m_Manager.AddComponent<DeformedMeshIndex>(entity);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Destroyed.Count, "An entity that becomes deformed should leave the cache; its RenderMeshArray mesh no longer matches what is rendered.");
            Assert.AreEqual(key, m_Destroyed[0]);
            Assert.AreEqual(0, m_Changed.Count, "A deformed entity should not be re-added through the new-entity path.");

            using var trackedQuery = m_Manager.CreateEntityQuery(typeof(SurfaceCacheTrackedInstance));
            Assert.AreEqual(0, trackedQuery.CalculateEntityCount(), "Tracking should be removed from deformed entities.");
        }

        [Test]
        public void TrackedEntity_LosesDeformedMeshIndex_ReentersTracking()
        {
            var entity = CreateRenderableEntity();
            var key = MakeKey(entity);
            m_Manager.AddComponent<DeformedMeshIndex>(entity);
            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count, "A deformed entity should never enter tracking.");

            m_Manager.RemoveComponent<DeformedMeshIndex>(entity);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "An entity that stops being deformed should re-enter through the new-entity path.");
            Assert.AreEqual(key, m_Changed[0].Key);
            Assert.AreEqual(0, m_Destroyed.Count);

            using var trackedQuery = m_Manager.CreateEntityQuery(typeof(SurfaceCacheTrackedInstance));
            Assert.AreEqual(1, trackedQuery.CalculateEntityCount(), "The entity should be tracked again.");
        }

        [Test]
        public void DeformedThenRestoredWithoutDrain_RecordCarriesNewestTransform([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            using var array = new NativeArray<Entity>(entities, Allocator.Temp);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            m_Manager.AddComponent<DeformedMeshIndex>(array);
            m_System.Update();

            m_Manager.RemoveComponent<DeformedMeshIndex>(array);
            m_System.Update();

            MoveAll(entities, 2f);
            UpdateAndDrain();

            AssertSameKeys(entities, m_Destroyed,
                "The keys are reported destroyed as well, which the consumer applies before the records.");
            AssertKeysAre(entities, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(2f, record.LocalToWorld.m13, "A move after the instances were tracked again should fold into their records.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Every move should be superseded by or folded into a record.");
        }

        [Test]
        public void LodGroup_MaskExcludesGlobalIlluminationLod_InvisibleRecordIsPushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out _);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A LOD-managed entity should still produce a full record.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsFalse(m_Changed[0].Visible, "An entity outside the group's GI LOD level should be reported as invisible.");
        }

        [Test]
        public void LodGroup_MaskIncludesGlobalIlluminationLod_VisibleRecordIsPushed()
        {
            CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 1, out _);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(m_Changed[0].Visible, "An entity inside the group's GI LOD level should be reported as visible.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodIsNone_InvisibleRecordIsPushed()
        {
            CreateRenderableEntityInLodGroup(lodMask: 1 << 0, globalIlluminationLod: -1, out _);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(m_Changed[0].Visible, "A group without a GlobalIlluminationLOD component should be excluded.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodIsOutOfRange_InvisibleRecordIsPushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 0, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsTrue(m_Changed[0].Visible);

            // A shift count of 32 wraps onto bit 0, which is the level this renderer is in.
            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 32 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Changing the selection should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsFalse(m_Changed[0].Visible, "A selection above the eight supported levels should contribute nothing.");

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = -1 });
            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count,
                "Every out-of-range selection collapses to the same nothing-selected state, so no re-push.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodRemoved_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 0, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsTrue(m_Changed[0].Visible);

            m_Manager.RemoveComponent<GlobalIlluminationLOD>(group);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Removing the selection should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsFalse(m_Changed[0].Visible, "A group that no longer selects a level should contribute nothing.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "An unchanged group should not keep re-pushing records.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodAdded_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 0, globalIlluminationLod: -1, out var group);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            m_Manager.AddComponentData(group, new GlobalIlluminationLOD { Value = 0 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Adding a selection should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "The record should reflect the new selection.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "An unchanged group should not keep re-pushing records.");
        }

        [Test]
        public void LodGroup_ComponentMissing_VisibleRecordIsPushed()
        {
            var group = m_Manager.CreateEntity();
            var entity = CreateRenderableEntity();
            m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = 1 << 1 });

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(m_Changed[0].Visible, "An entity whose group has no MeshLODGroupComponent should be treated as a normal renderer.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodChanged_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A GI LOD change on the group should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "The record should reflect the new GI LOD selection.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "An unchanged group should not keep re-pushing records.");
        }

        [Test]
        public void LodGroup_LodMaskChanged_FullRecordIsRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            m_Manager.SetComponentData(entity, new MeshLODComponent { Group = group, LODMask = 1 << 0 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "A MeshLODComponent change should re-push the entity's record.");
            Assert.IsTrue(m_Changed[0].Visible, "The record should reflect the new LOD mask.");
        }

        [Test]
        public void LodGroup_ComponentRemoved_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            m_Manager.RemoveComponent<MeshLODGroupComponent>(group);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Removing the group component should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "Without a group component the entity should be treated as a normal renderer.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "A removed group should not keep re-pushing records.");
        }

        [Test]
        public void LodGroup_EntityDestroyed_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            m_Manager.DestroyEntity(group);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Destroying the group entity should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "Without a group the entity should be treated as a normal renderer.");

            using var trackedGroupQuery = m_Manager.CreateEntityQuery(typeof(SurfaceCacheTrackedLODGroup));
            Assert.AreEqual(0, trackedGroupQuery.CalculateEntityCount(), "The group tracking component should be removed so the group entity fully dies.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "A destroyed group should not keep re-pushing records.");
        }

        [Test]
        public void LodGroup_GlobalIlluminationLodChanged_OtherGroupsRenderersAreNotRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            var otherEntity = CreateRenderableEntityInLodGroup(lodMask: 1 << 0, globalIlluminationLod: 0, out _);

            // Dirtiness is decided per chunk, so the two renderers have to land in different chunks for this to
            // tell a narrowed re-push apart from a global one.
            var filter = RenderFilterSettings.Default;
            filter.RenderingLayerMask = 2;
            m_Manager.AddSharedComponent(otherEntity, filter);

            UpdateAndDrain();
            Assert.AreEqual(2, m_Changed.Count, "Both renderers should be pushed on first entry.");

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Only the changed group's renderers should be re-pushed.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
        }

        [Test]
        public void LodGroup_RendererMovedToOtherGroup_RecordFollowsTheNewSelection()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out _);
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible);

            var otherGroup = m_Manager.CreateEntity();
            m_Manager.AddComponentData(otherGroup, new MeshLODGroupComponent());
            m_Manager.AddComponentData(otherGroup, new GlobalIlluminationLOD { Value = 1 });
            m_Manager.SetComponentData(entity, new MeshLODComponent { Group = otherGroup, LODMask = 1 << 1 });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Moving to another group should re-push the entity's record.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "The record should follow the new group's selection.");
        }

        [Test]
        public void LodSelectionChangedAfterAbortedDrain_RenderersArriveOnceWithNewestTransforms([Values(1, k_ParallelCount)] int count)
        {
            var group = CreateLodGroup(globalIlluminationLod: 0);
            var renderers = CreateRenderableEntities(count);
            AddToLodGroup(renderers, group, lodMask: 1 << 0);
            UpdateAndDrain();

            MoveAll(renderers, 1f);
            m_System.Update();
            BeginAndAbortDrain();

            MoveAll(renderers, 2f);
            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "The group's renderers should be re-pushed once each.");
            AssertKeysAre(renderers, m_Changed);
            foreach (var record in m_Changed)
            {
                Assert.IsFalse(record.Visible, "The records should reflect the new GI LOD selection.");
                Assert.AreEqual(2f, record.LocalToWorld.m13, "The records should carry the newest transforms.");
            }

            Assert.AreEqual(0, m_TransformChanged.Count, "Transforms retained by the aborted drain should be superseded by the records.");
        }

        [Test]
        public void TransformThenMovedFullRecordWithoutDrain_RecordKeepsNewestTransform([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            MoveAll(entities, 2f);
            foreach (var entity in entities)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "Each instance should arrive once, as a full record.");
            foreach (var record in m_Changed)
                Assert.AreEqual(2f, record.LocalToWorld.m13, "An older transform must not be folded over a newer full record.");
            Assert.AreEqual(0, m_TransformChanged.Count, "The older transforms should be superseded by the records.");
        }

        [Test]
        public void FoldDuringAbortedDrain_LaterMovesStillFoldIntoTheirRecords([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            var dirtied = CreateRenderableEntities(8);
            foreach (var entity in dirtied)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            MoveAll(dirtied, 1f);
            m_System.Update();

            MoveAll(dirtied, 2f);
            foreach (var entity in dirtied)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();
            BeginAndAbortDrain();

            MoveAll(dirtied, 3f);
            UpdateAndDrain();

            Assert.AreEqual(dirtied.Length, m_Changed.Count, "The retained records should be delivered once each.");
            AssertKeysAre(dirtied, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(3f, record.LocalToWorld.m13, "A move after the aborted drain should fold into the retained record.");
            AssertTransformsAre(moved, 1f, "The clean instances' moves should be kept once each.");
        }

        [Test]
        public void FullRecordThenMoveWithoutDrain_MoveFoldsIntoRecord([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            foreach (var entity in entities)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();

            MoveAll(entities, 1f);
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "Each instance should arrive once, as a full record.");
            foreach (var record in m_Changed)
                Assert.AreEqual(1f, record.LocalToWorld.m13, "A newer transform should be folded into the pending record.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Folded transforms should not also arrive on their own.");
        }

        [Test]
        public void FullRecordMovedThenRepushedWithoutDrain_RecordKeepsNewestTransform(
            [Values(1, k_ParallelCount)] int count,
            [Values] bool moveAfterRepush)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            foreach (var entity in entities)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();

            MoveAll(entities, 1f);
            m_System.Update();

            MoveAll(entities, 2f);
            foreach (var entity in entities)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();

            if (moveAfterRepush)
            {
                MoveAll(entities, 3f);
                m_System.Update();
            }

            Drain();

            float newest = moveAfterRepush ? 3f : 2f;
            Assert.AreEqual(count, m_Changed.Count, "Each instance should arrive once, as a full record.");
            foreach (var record in m_Changed)
                Assert.AreEqual(newest, record.LocalToWorld.m13, "The re-pushed record should drop older moves and fold in newer ones.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Every move should be superseded by or folded into a record.");
        }

        [Test]
        public void FoldKeepsTheOrderOfTransformsItDoesNotFold([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            var dirtied = CreateRenderableEntities(8);
            foreach (var entity in dirtied)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            MoveAll(dirtied, 1f);
            m_System.Update();

            MoveAll(moved, 2f);
            foreach (var entity in dirtied)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            UpdateAndDrain();

            Assert.AreEqual(dirtied.Length, m_Changed.Count, "Only the dirtied instances should arrive as full records.");
            AssertKeysAre(dirtied, m_Changed);
            Assert.AreEqual(2 * count, m_TransformChanged.Count, "Both moves of the clean instances should be kept.");

            var steps = new Dictionary<EntityId, List<float>>();
            foreach (var record in m_TransformChanged)
            {
                if (!steps.TryGetValue(record.Key, out var list))
                    steps.Add(record.Key, list = new List<float>());
                list.Add(record.LocalToWorld.m13);
            }

            Assert.AreEqual(count, steps.Count);
            foreach (var list in steps.Values)
                CollectionAssert.AreEqual(new[] { 1f, 2f }, list, "Kept transforms should stay in update order.");
        }

        [Test]
        public void LodGroupDestroyedAfterAbortedDrain_RenderersArriveOnceWithNewestTransforms([Values(1, k_ParallelCount)] int count)
        {
            var group = CreateLodGroup(globalIlluminationLod: 0);
            var renderers = CreateRenderableEntities(count);
            AddToLodGroup(renderers, group, lodMask: 1 << 1);
            UpdateAndDrain();

            MoveAll(renderers, 1f);
            m_System.Update();
            BeginAndAbortDrain();

            MoveAll(renderers, 2f);
            m_Manager.DestroyEntity(group);
            UpdateAndDrain();

            AssertRepushedVisibleWithNewestTransforms(renderers, 2f);
        }

        [Test]
        public void LodGroupComponentRemovedAfterAbortedDrain_RenderersArriveOnceWithNewestTransforms([Values(1, k_ParallelCount)] int count)
        {
            var group = CreateLodGroup(globalIlluminationLod: 0);
            var renderers = CreateRenderableEntities(count);
            AddToLodGroup(renderers, group, lodMask: 1 << 1);
            UpdateAndDrain();

            MoveAll(renderers, 1f);
            m_System.Update();
            BeginAndAbortDrain();

            MoveAll(renderers, 2f);
            m_Manager.RemoveComponent<MeshLODGroupComponent>(group);
            UpdateAndDrain();

            AssertRepushedVisibleWithNewestTransforms(renderers, 2f);
        }

        [Test]
        public void FullRecordsRetainedByAbortedDrain_LaterMovesFoldIntoThem([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            foreach (var entity in entities)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();
            BeginAndAbortDrain();

            MoveAll(entities, 1f);
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "The retained records should be delivered once each.");
            AssertKeysAre(entities, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(1f, record.LocalToWorld.m13, "A later move should fold into the retained record.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Folded transforms should not also arrive on their own.");
        }

        [Test]
        public void LodSelectionChangedWhileTransformJobInFlight_BothArriveOnTheirOwnPath([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            var group = CreateLodGroup(globalIlluminationLod: 0);
            var renderers = CreateRenderableEntities(8);
            AddToLodGroup(renderers, group, lodMask: 1 << 0);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            m_System.Update();

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            Assert.AreEqual(renderers.Length, m_Changed.Count, "Only the group's renderers should be re-pushed.");
            AssertKeysAre(renderers, m_Changed);
            foreach (var record in m_Changed)
                Assert.IsFalse(record.Visible, "The records should reflect the new GI LOD selection.");
            AssertTransformsAre(moved, 1f, "The moves from the earlier update should still arrive once each.");
        }

        [Test]
        public void LodGroupChangedWhileOtherGroupsRenderersMove_TheyArriveAsTransforms([Values(1, k_ParallelCount)] int count)
        {
            var changedGroup = CreateLodGroup(globalIlluminationLod: 0);
            var changedRenderers = CreateRenderableEntities(8);
            AddToLodGroup(changedRenderers, changedGroup, lodMask: 1 << 0);
            var otherGroup = CreateLodGroup(globalIlluminationLod: 0);
            var movedRenderers = CreateRenderableEntities(count);
            AddToLodGroup(movedRenderers, otherGroup, lodMask: 1 << 0);
            foreach (var entity in movedRenderers)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            UpdateAndDrain();

            MoveAll(movedRenderers, 1f);
            m_Manager.SetComponentData(changedGroup, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            AssertKeysAre(changedRenderers, m_Changed);
            foreach (var record in m_Changed)
                Assert.IsFalse(record.Visible, "The records should reflect the new GI LOD selection.");
            AssertTransformsAre(movedRenderers, 1f, "Renderers of an unchanged group should only report their moves.");
        }

        [Test]
        public void LodGroupChangedWhileRenderersSharingAChunkWithItsRenderersMove_AllArriveAsFullRecords()
        {
            var changedGroup = CreateLodGroup(globalIlluminationLod: 0);
            var otherGroup = CreateLodGroup(globalIlluminationLod: 0);
            var renderers = CreateRenderableEntities(8);
            var changedRenderers = renderers.Take(4).ToArray();
            var otherRenderers = renderers.Skip(4).ToArray();
            AddToLodGroup(changedRenderers, changedGroup, lodMask: 1 << 0);
            AddToLodGroup(otherRenderers, otherGroup, lodMask: 1 << 0);
            UpdateAndDrain();

            MoveAll(renderers, 1f);
            m_Manager.SetComponentData(changedGroup, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            AssertKeysAre(renderers, m_Changed);
            foreach (var record in m_Changed)
            {
                bool inChangedGroup = changedRenderers.Select(MakeKey).Contains(record.Key);
                Assert.AreEqual(!inChangedGroup, record.Visible, "Each record should follow its own group's selection.");
                Assert.AreEqual(1f, record.LocalToWorld.m13, "The records should carry the newest transforms.");
            }

            Assert.AreEqual(0, m_TransformChanged.Count, "A chunk with a changed group's renderer should not also report moves.");
        }

        [Test]
        public void ExcludedFromGlobalIllumination_NoLodGroupMembership_InvisibleRecordIsPushed()
        {
            var entity = CreateRenderableEntity();
            m_Manager.AddComponent<ExcludedFromGlobalIllumination>(entity);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsFalse(m_Changed[0].Visible, "A renderer carrying the frozen exclusion should be reported as invisible.");
        }

        [Test]
        public void ExcludedFromGlobalIllumination_GroupComponentsStripped_InvisibleRecordIsPushed()
        {
            var group = m_Manager.CreateEntity();
            var entity = CreateRenderableEntity();
            m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = 1 << 1 });
            m_Manager.AddComponent<ExcludedFromGlobalIllumination>(entity);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(m_Changed[0].Visible, "The frozen exclusion should win over the treated-as-normal-renderer path.");
        }

        [Test]
        public void LodGroup_OutOfRangeSelectionThenGroupComponentRemoved_FullRecordsAreRepushed()
        {
            var entity = CreateRenderableEntityInLodGroup(lodMask: 1 << 1, globalIlluminationLod: 0, out var group);
            // -2 collides with the recorded no-group sentinel; the baker never writes it, ECS code can.
            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = -2 });
            UpdateAndDrain();
            Assert.IsFalse(m_Changed[0].Visible, "An out-of-range selection should contribute nothing.");

            m_Manager.RemoveComponent<MeshLODGroupComponent>(group);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Removing the group component should re-push the group's renderers.");
            Assert.AreEqual(MakeKey(entity), m_Changed[0].Key);
            Assert.IsTrue(m_Changed[0].Visible, "Without a group component the entity should be treated as a normal renderer.");
        }

        [Test]
        public void TryRegisterConsumer_ConsumerAlreadyRegistered_ReturnsFalse()
        {
            // SetUp registered the single supported consumer.
            Assert.IsFalse(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
        }

        [Test]
        public void ConsumerReregistered_FullResyncIsPushed(
            [Values(1, k_ParallelCount)] int count,
            [Values] bool moveInResyncUpdate)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            SurfaceCacheEntityChangeBridge.UnregisterConsumer();
            Assert.IsTrue(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
            if (moveInResyncUpdate)
                MoveAll(entities, 2f);
            UpdateAndDrain();

            float newest = moveInResyncUpdate ? 2f : 1f;
            Assert.AreEqual(count, m_Changed.Count, "A re-registered consumer should receive the full state again.");
            AssertKeysAre(entities, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(newest, record.LocalToWorld.m13, "The resync records should carry the newest transforms.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Moves pushed to the previous consumer should not reach the new one.");
            Assert.AreEqual(0, m_Destroyed.Count);
        }

        [Test]
        public void DestroyedAfterAbortedDrain_OnlyTheDestroysArrive([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            var dirtied = CreateRenderableEntities(8);
            foreach (var entity in dirtied)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            foreach (var entity in dirtied)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_System.Update();
            BeginAndAbortDrain();

            using (var destroyed = new NativeArray<Entity>(moved.Concat(dirtied).ToArray(), Allocator.Temp))
                m_Manager.DestroyEntity(destroyed);
            UpdateAndDrain();

            AssertSameKeys(moved.Concat(dirtied), m_Destroyed);
            Assert.AreEqual(0, m_Changed.Count, "Records retained by the aborted drain should be withdrawn by the destroys.");
            Assert.AreEqual(0, m_TransformChanged.Count, "Moves retained by the aborted drain should be dropped for destroyed keys.");
        }

        [Test]
        public void NoConsumer_TrackingIsDroppedAndRebuiltOnReturn([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            SurfaceCacheEntityChangeBridge.UnregisterConsumer();
            m_System.Update();

            using (var trackedQuery = m_Manager.CreateEntityQuery(typeof(SurfaceCacheTrackedInstance)))
            {
                Assert.AreEqual(0, trackedQuery.CalculateEntityCount(), "Tracking should be dropped while no consumer is present.");
            }

            Assert.IsTrue(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
            UpdateAndDrain();

            Assert.AreEqual(count, m_Changed.Count, "A returning consumer should receive the instances through the new-entity path.");
            AssertKeysAre(entities, m_Changed);
        }

        [Test]
        public void SystemDestroyed_DestroyedKeysArePushed([Values(1, k_ParallelCount)] int count)
        {
            var entities = CreateRenderableEntities(count);
            UpdateAndDrain();

            MoveAll(entities, 1f);
            m_System.Update();

            m_World.DestroySystemManaged(m_System);
            Drain();

            Assert.AreEqual(count, m_Destroyed.Count, "System destruction should report all tracked instances as destroyed.");
            AssertSameKeys(entities, m_Destroyed);
            Assert.AreEqual(0, m_TransformChanged.Count, "Transforms of destroyed instances should be dropped at drain.");
        }

        [Test]
        public void TransformAndFullRecordChangesInSameUpdate_EachArrivesOnItsOwnPath([Values(1, k_ParallelCount)] int count)
        {
            var moved = CreateRenderableEntities(count);
            var dirtied = CreateRenderableEntities(8);
            foreach (var entity in dirtied)
                m_Manager.AddComponent<OtherChunkTag>(entity);
            UpdateAndDrain();

            MoveAll(moved, 1f);
            MoveAll(dirtied, 2f);
            foreach (var entity in dirtied)
                m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            UpdateAndDrain();

            Assert.AreEqual(dirtied.Length, m_Changed.Count, "Only the dirtied chunk should produce full records.");
            AssertKeysAre(dirtied, m_Changed);
            foreach (var record in m_Changed)
                Assert.AreEqual(2f, record.LocalToWorld.m13, "A full record should carry the instance's current transform.");
            AssertTransformsAre(moved, 1f, "Instances in clean chunks should produce transform records only.");
        }

        struct OtherChunkTag : IComponentData
        {
        }

        // Leaves a job writing MaterialMeshInfo in flight, as an earlier system in the frame might.
        [DisableAutoCreation]
        internal partial class PendingMaterialMeshInfoWriterSystem : SystemBase
        {
            EntityQuery m_Query;

            protected override void OnCreate()
            {
                m_Query = GetEntityQuery(ComponentType.ReadWrite<MaterialMeshInfo>());
            }

            protected override void OnUpdate()
            {
                Dependency = new WriteJob
                {
                    Entities = m_Query.ToEntityArray(Allocator.TempJob),
                    MaterialMeshInfos = GetComponentLookup<MaterialMeshInfo>(),
                }.Schedule(Dependency);
            }

            struct WriteJob : IJob
            {
                [DeallocateOnJobCompletion] public NativeArray<Entity> Entities;
                public ComponentLookup<MaterialMeshInfo> MaterialMeshInfos;

                public void Execute()
                {
                    foreach (var entity in Entities)
                        MaterialMeshInfos[entity] = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);
                }
            }
        }

        Entity CreateLodGroup(int globalIlluminationLod)
        {
            var group = m_Manager.CreateEntity();
            m_Manager.AddComponentData(group, new MeshLODGroupComponent());
            m_Manager.AddComponentData(group, new GlobalIlluminationLOD { Value = globalIlluminationLod });
            return group;
        }

        void AddToLodGroup(Entity[] entities, Entity group, int lodMask)
        {
            foreach (var entity in entities)
                m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = lodMask });
        }

        void AssertRepushedVisibleWithNewestTransforms(Entity[] renderers, float step)
        {
            Assert.AreEqual(renderers.Length, m_Changed.Count, "The group's renderers should be re-pushed once each.");
            AssertKeysAre(renderers, m_Changed);
            foreach (var record in m_Changed)
            {
                Assert.IsTrue(record.Visible, "Without a group the renderers should be treated as normal renderers.");
                Assert.AreEqual(step, record.LocalToWorld.m13, "The records should carry the newest transforms.");
            }

            Assert.AreEqual(0, m_TransformChanged.Count, "Transforms retained by the aborted drain should be superseded by the records.");
        }

        static void BeginAndAbortDrain()
        {
            SurfaceCacheEntityChangeBridge.BeginDrain(out _, out _, out _, out _, out _);
            SurfaceCacheEntityChangeBridge.AbortDrain();
        }

        Entity[] CreateRenderableEntities(int count)
        {
            var entities = new Entity[count];
            entities[0] = CreateRenderableEntity();
            for (int i = 1; i < count; i++)
                entities[i] = m_Manager.Instantiate(entities[0]);
            return entities;
        }

        void MoveAll(Entity[] entities, float step)
        {
            for (int i = 0; i < entities.Length; i++)
                m_Manager.SetComponentData(entities[i], new LocalToWorld { Value = float4x4.Translate(new float3(i, step, 0f)) });
        }

        void AssertTransformsAre(Entity[] entities, float step, string message)
        {
            Assert.AreEqual(entities.Length, m_TransformChanged.Count, message);
            var expected = new Dictionary<EntityId, Matrix4x4>();
            for (int i = 0; i < entities.Length; i++)
                expected.Add(MakeKey(entities[i]), float4x4.Translate(new float3(i, step, 0f)));

            foreach (var record in m_TransformChanged)
            {
                Assert.IsTrue(expected.Remove(record.Key, out var localToWorld), "Each instance should report exactly once.");
                Assert.AreEqual(localToWorld, record.LocalToWorld);
            }
        }

        static void AssertKeysAre(Entity[] entities, List<SurfaceCacheEntityInstanceRecord> records)
        {
            AssertSameKeys(entities, records.Select(record => record.Key));
        }

        // CollectionAssert.AreEquivalent is quadratic when the orders differ, which takes seconds at k_ParallelCount.
        static void AssertSameKeys(IEnumerable<Entity> entities, IEnumerable<EntityId> keys, string message = null)
        {
            var expected = new HashSet<EntityId>(entities.Select(MakeKey));
            var actual = keys.ToList();
            Assert.AreEqual(expected.Count, actual.Count, message);
            Assert.IsTrue(expected.SetEquals(actual), message);
        }

        Entity CreateRenderableEntity()
        {
            var entity = m_Manager.CreateEntity();
            m_Manager.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            m_Manager.AddComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_Manager.AddSharedComponent(entity, new RenderMeshArray(new[] { m_Material }, new[] { m_Mesh }));
            return entity;
        }

        // Runtime ids send the whole chunk through the main-thread fallback instead of the Burst job.
        Entity CreateRuntimeIdTwin(Entity entity)
        {
            var twin = m_Manager.Instantiate(entity);
            m_Manager.SetComponentData(twin, new MaterialMeshInfo(new BatchMaterialID { value = 1 }, new BatchMeshID { value = 1 }));
            return twin;
        }

        SurfaceCacheEntityInstanceRecord RecordFor(Entity entity)
        {
            var key = MakeKey(entity);
            return m_Changed.Single(record => record.Key == key);
        }

        List<SurfaceCacheSubMeshMaterial> MaterialsOf(in SurfaceCacheEntityInstanceRecord record)
        {
            return m_ChangedMaterials.GetRange(record.MaterialsStart, record.MaterialsCount);
        }

        List<SurfaceCacheMaterialOverride> OverridesOf(in SurfaceCacheEntityInstanceRecord record)
        {
            return m_ChangedOverrides.GetRange(record.OverridesStart, record.OverridesCount);
        }

        void AssertResolvesToTestAssets(in SurfaceCacheEntityInstanceRecord record)
        {
            Assert.AreEqual(m_Mesh.GetEntityId(), record.Mesh, "The record should carry the RenderMeshArray mesh.");
            var materials = MaterialsOf(record);
            Assert.AreEqual(1, materials.Count, "The record should carry one assignment.");
            Assert.AreEqual(0, materials[0].SubMeshIndex);
            Assert.AreEqual(m_Material.GetEntityId(), materials[0].Material, "The record should carry the RenderMeshArray material.");
        }

        // A GI LOD below zero means the group does not contribute, which is represented by the absence of the component.
        Entity CreateRenderableEntityInLodGroup(int lodMask, int globalIlluminationLod, out Entity group)
        {
            group = m_Manager.CreateEntity();
            m_Manager.AddComponentData(group, new MeshLODGroupComponent());
            if (globalIlluminationLod >= 0)
                m_Manager.AddComponentData(group, new GlobalIlluminationLOD { Value = globalIlluminationLod });

            var entity = CreateRenderableEntity();
            m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = lodMask });
            return entity;
        }

        void UpdateAndDrain()
        {
            m_System.Update();
            Drain();
        }

        void Drain()
        {
            m_Changed.Clear();
            m_ChangedMaterials.Clear();
            m_ChangedOverrides.Clear();
            m_TransformChanged.Clear();
            m_Destroyed.Clear();

            SurfaceCacheEntityChangeBridge.BeginDrain(out var changed, out var changedMaterials, out var changedOverrides, out var destroyed, out var transformChanged);
            for (int i = 0; i < changed.Length; i++)
                m_Changed.Add(changed[i]);
            for (int i = 0; i < changedMaterials.Length; i++)
                m_ChangedMaterials.Add(changedMaterials[i]);
            for (int i = 0; i < changedOverrides.Length; i++)
                m_ChangedOverrides.Add(changedOverrides[i]);
            for (int i = 0; i < destroyed.Length; i++)
                m_Destroyed.Add(destroyed[i]);
            for (int i = 0; i < transformChanged.Length; i++)
                m_TransformChanged.Add(transformChanged[i]);

            SurfaceCacheEntityChangeBridge.EndDrain();
        }

        static EntityId MakeKey(Entity entity)
        {
            return entity;
        }
    }
}

#endif
