#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unity.Entities.Graphics.Tests.SurfaceCache
{
    internal class SurfaceCacheProducerAllocationTests
    {
        const int k_InstanceCount = 1000;

        World m_World;
        EntityManager m_Manager;
        SurfaceCacheMeshInstanceProducerSystem m_System;
        Mesh m_Mesh;
        Material m_Material;
        Entity m_Prefab;
        NativeArray<Entity> m_Instances;
        bool m_ConsumerRegistered;

        int m_DrainedChangedCount;
        int m_DrainedMaterialCount;
        int m_DrainedDestroyedCount;
        int m_DrainedTransformCount;

        [SetUp]
        public void SetUp()
        {
            Assume.That(!SurfaceCacheEntityChangeBridge.IsConsumerPresent,
                "These tests require exclusive ownership of the SurfaceCacheEntityChangeBridge.");

            m_World = new World("SurfaceCacheProducerAllocationTests");
            m_Manager = m_World.EntityManager;
            m_System = m_World.GetOrCreateSystemManaged<SurfaceCacheMeshInstanceProducerSystem>();
            m_System.Enabled = true;

            m_Mesh = new Mesh { name = "SurfaceCacheProducerAllocationTests Mesh" };
            m_Mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            m_Mesh.triangles = new[] { 0, 1, 2 };
            m_Material = new Material(Shader.Find("Hidden/InternalErrorShader"))
            {
                name = "SurfaceCacheProducerAllocationTests Material"
            };

            m_Prefab = m_Manager.CreateEntity();
            m_Manager.AddComponentData(m_Prefab, new LocalToWorld { Value = float4x4.identity });
            m_Manager.AddComponentData(m_Prefab, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            m_Manager.AddSharedComponent(m_Prefab, new RenderMeshArray(new[] { m_Material }, new[] { m_Mesh }));
            m_Manager.AddComponent<Prefab>(m_Prefab);

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

            if (m_Instances.IsCreated)
                m_Instances.Dispose();

            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();

            Object.DestroyImmediate(m_Mesh);
            Object.DestroyImmediate(m_Material);
        }

        [Test]
        public void NewInstances_DoNotAllocateGCMemory()
        {
            WarmUpInstanceStorage();

            SpawnInstances(k_InstanceCount);
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(k_InstanceCount, m_DrainedChangedCount, "Every spawned instance should produce a full record.");
            Assert.AreEqual(k_InstanceCount, m_DrainedMaterialCount, "Every full record should carry its material.");
        }

        [Test]
        public void DestroyedInstances_DoNotAllocateGCMemory()
        {
            WarmUpInstanceStorage();

            SpawnInstances(k_InstanceCount);
            UpdateAndDrain();
            DestroyInstances();
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(k_InstanceCount, m_DrainedDestroyedCount, "Every destroyed instance should be reported.");
        }

        [Test]
        public void FullRecordChanges_DoNotAllocateGCMemory()
        {
            SpawnInstances(k_InstanceCount);
            UpdateAndDrain();
            WarmUpFullRecordChanges();

            DirtyAllMaterialMeshInfos();
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(k_InstanceCount, m_DrainedChangedCount, "Every dirtied instance should produce a full record.");
            Assert.AreEqual(k_InstanceCount, m_DrainedMaterialCount, "Every full record should carry its material.");
        }

        [Test]
        public void TransformChanges_DoNotAllocateGCMemory(
            [Values(k_InstanceCount, 2 * SurfaceCacheMeshInstanceProducerSystem.k_ParallelTransformThreshold)] int instanceCount)
        {
            SpawnInstances(instanceCount);
            UpdateAndDrain();
            WarmUpTransformChanges();

            DirtyAllTransforms();
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(instanceCount, m_DrainedTransformCount,
                "Every moved instance should produce a transform record.");
        }

        [Test]
        public void NoChanges_DoNotAllocateGCMemory()
        {
            SpawnInstances(k_InstanceCount);
            UpdateAndDrain();
            WarmUpIdleUpdates();

            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(0, m_DrainedChangedCount + m_DrainedDestroyedCount + m_DrainedTransformCount,
                "Nothing changed, so nothing should be produced.");
        }

        [Test]
        public void MovesWithNewInstances_DoNotAllocateGCMemory()
        {
            const int movedCount = 2 * SurfaceCacheMeshInstanceProducerSystem.k_ParallelTransformThreshold;
            const int newCount = 16;

            SpawnInstances(movedCount);
            UpdateAndDrain();

            DirtyAllTransforms();
            var warmUp = m_Manager.Instantiate(m_Prefab, newCount, Allocator.Temp);
            UpdateAndDrain();
            m_Manager.DestroyEntity(warmUp);
            UpdateAndDrain();

            DirtyAllTransforms();
            m_Manager.Instantiate(m_Prefab, newCount, Allocator.Temp).Dispose();
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(newCount, m_DrainedChangedCount, "Every new instance should produce a full record.");
            Assert.AreEqual(movedCount, m_DrainedTransformCount, "Every moved instance should produce a transform record.");
        }

        [Test]
        public void TransformThenFullRecordWithoutDrain_DoesNotAllocateGCMemory(
            [Values(k_InstanceCount, 2 * SurfaceCacheMeshInstanceProducerSystem.k_ParallelTransformThreshold)] int instanceCount)
        {
            SpawnInstances(instanceCount);
            UpdateAndDrain();

            DirtyAllTransforms();
            m_System.Update();
            DirtyAllMaterialMeshInfos();
            UpdateAndDrain();

            DirtyAllTransforms();
            int allocations = CountGCAllocations(m_System.Update);
            DirtyAllMaterialMeshInfos();
            allocations += CountGCAllocations(UpdateAndDrain);

            Assert.AreEqual(0, allocations, "GC allocations during extraction.");
            Assert.AreEqual(instanceCount, m_DrainedChangedCount, "Every dirtied instance should produce a full record.");
            Assert.AreEqual(0, m_DrainedTransformCount, "Every transform should be folded into its instance's full record.");
        }

        [Test]
        public void LodSelectionChanges_DoNotAllocateGCMemory()
        {
            var group = m_Manager.CreateEntity();
            m_Manager.AddComponentData(group, new MeshLODGroupComponent());
            m_Manager.AddComponentData(group, new GlobalIlluminationLOD { Value = 0 });
            SpawnInstances(k_InstanceCount);
            foreach (var entity in m_Instances)
                m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = 1 << 0 });
            UpdateAndDrain();

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 1 });
            UpdateAndDrain();

            m_Manager.SetComponentData(group, new GlobalIlluminationLOD { Value = 0 });
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(k_InstanceCount, m_DrainedChangedCount, "Every renderer of the changed group should be re-pushed.");
        }

        [Test]
        public void RuntimeIdFullRecords_DoNotAllocateGCMemory()
        {
            var runtimeIds = new MaterialMeshInfo(new BatchMaterialID { value = 1 }, new BatchMeshID { value = 1 });
            m_Manager.SetComponentData(m_Prefab, runtimeIds);
            SpawnInstances(k_InstanceCount);
            UpdateAndDrain();
            SetAllMaterialMeshInfos(runtimeIds);
            UpdateAndDrain();

            SetAllMaterialMeshInfos(runtimeIds);
            Assert.AreEqual(0, CountGCAllocations(UpdateAndDrain), "GC allocations during extraction.");
            Assert.AreEqual(k_InstanceCount, m_DrainedChangedCount, "Every dirtied instance should produce a full record.");
        }

        void WarmUpInstanceStorage()
        {
            SpawnInstances(k_InstanceCount);
            UpdateAndDrain();
            DestroyInstances();
            UpdateAndDrain();
        }

        void WarmUpFullRecordChanges()
        {
            DirtyAllMaterialMeshInfos();
            UpdateAndDrain();
        }

        void WarmUpTransformChanges()
        {
            DirtyAllTransforms();
            UpdateAndDrain();
        }

        void WarmUpIdleUpdates()
        {
            UpdateAndDrain();
        }

        void SpawnInstances(int instanceCount)
        {
            m_Instances = m_Manager.Instantiate(m_Prefab, instanceCount, Allocator.Persistent);
        }

        void DestroyInstances()
        {
            m_Manager.DestroyEntity(m_Instances);
            m_Instances.Dispose();
        }

        void DirtyAllMaterialMeshInfos()
        {
            SetAllMaterialMeshInfos(MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        }

        void SetAllMaterialMeshInfos(MaterialMeshInfo materialMeshInfo)
        {
            foreach (var entity in m_Instances)
            {
                m_Manager.SetComponentData(entity, materialMeshInfo);
            }
        }

        void DirtyAllTransforms()
        {
            foreach (var entity in m_Instances)
            {
                m_Manager.SetComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            }
        }

        static int CountGCAllocations(System.Action action)
        {
            using var recorder = new ProfilerRecorder("GC.Alloc", 1,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread | ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
            try
            {
                action();
            }
            finally
            {
                recorder.Stop();
            }

            return (int)recorder.GetSample(0).Count;
        }

        void UpdateAndDrain()
        {
            m_System.Update();

            SurfaceCacheEntityChangeBridge.BeginDrain(
                out var changed, out var changedMaterials, out _, out var destroyed, out var transformChanged);
            m_DrainedChangedCount = changed.Length;
            m_DrainedMaterialCount = changedMaterials.Length;
            m_DrainedDestroyedCount = destroyed.Length;
            m_DrainedTransformCount = transformChanged.Length;
            SurfaceCacheEntityChangeBridge.EndDrain();
        }
    }
}

#endif
