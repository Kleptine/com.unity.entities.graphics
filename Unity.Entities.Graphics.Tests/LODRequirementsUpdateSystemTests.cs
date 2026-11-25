using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

namespace Unity.Entities.Graphics.Tests
{
    internal class LODRequirementsUpdateSystemTests
    {
        World m_World;
        EntityManager m_Manager;
        LODRequirementsUpdateSystem m_System;

        [SetUp]
        public void SetUp()
        {
            m_World = new World("LODRequirementsUpdateSystemTests");
            m_Manager = m_World.EntityManager;
            m_System = m_World.GetOrCreateSystemManaged<LODRequirementsUpdateSystem>();
            m_System.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();
        }

        [Test]
        public void GroupEntityWithoutGroupComponent_UpdateCompletes()
        {
            var group = m_Manager.CreateEntity();
            var renderer = CreateLODRenderer(group, group);

            Assert.DoesNotThrow(() =>
            {
                m_System.Update();
                m_Manager.CompleteAllTrackedJobs();
            });

            Assert.IsTrue(m_Manager.Exists(renderer));
        }

        [Test]
        public void GroupParentWithoutGroupComponent_UpdateCompletes()
        {
            // The renderer's own group resolves, so the guard above it does not short-circuit and the parent
            // lookup is actually reached.
            var orphanedParent = m_Manager.CreateEntity();
            var group = m_Manager.CreateEntity(typeof(MeshLODGroupComponent), typeof(LocalToWorld), typeof(LODGroupWorldReferencePoint));
            m_Manager.SetComponentData(group, new LocalToWorld { Value = float4x4.identity });
            m_Manager.SetComponentData(group, new MeshLODGroupComponent
            {
                LODDistances0 = new float4(10f, 20f, 30f, 40f),
                ParentGroup = orphanedParent,
                ParentMask = 1,
            });

            var renderer = CreateLODRenderer(group, Entity.Null);

            Assert.DoesNotThrow(() =>
            {
                m_System.Update();
                m_Manager.CompleteAllTrackedJobs();
            });

            Assert.IsTrue(m_Manager.Exists(renderer));
        }

        [Test]
        public void GroupComponentRemovedAfterValidUpdate_RangeBecomesUnselectable()
        {
            var group = m_Manager.CreateEntity(typeof(MeshLODGroupComponent), typeof(LocalToWorld), typeof(LODGroupWorldReferencePoint));
            m_Manager.SetComponentData(group, new LocalToWorld { Value = float4x4.identity });
            m_Manager.SetComponentData(group, new MeshLODGroupComponent { LODDistances0 = new float4(10f, 20f, 30f, 40f) });

            var renderer = CreateLODRenderer(group, Entity.Null);
            m_System.Update();
            m_Manager.CompleteAllTrackedJobs();

            Assert.AreNotEqual(0, m_Manager.GetComponentData<LODRange>(renderer).LODMask,
                "The renderer should have a real range while its group resolves.");

            // The group stops resolving. MeshLODComponent is rewritten so the change-filtered query still visits
            // this chunk -- without that the job would not run at all and nothing could be rewritten.
            m_Manager.RemoveComponent<MeshLODGroupComponent>(group);
            m_Manager.SetComponentData(renderer, m_Manager.GetComponentData<MeshLODComponent>(renderer));

            m_System.Update();
            m_Manager.CompleteAllTrackedJobs();

            Assert.AreEqual(0, m_Manager.GetComponentData<LODRange>(renderer).LODMask,
                "A renderer whose group stopped resolving should be left unselectable, not on its previous range.");
            Assert.AreEqual(0f, m_Manager.GetComponentData<RootLODRange>(renderer).LOD.MaxDist);
        }

        Entity CreateLODRenderer(Entity group, Entity parentGroup)
        {
            var renderer = m_Manager.CreateEntity(
                typeof(LocalToWorld),
                typeof(MeshLODComponent),
                typeof(RootLODRange),
                typeof(LODRange),
                typeof(RootLODWorldReferencePoint),
                typeof(LODWorldReferencePoint));

            m_Manager.SetComponentData(renderer, new LocalToWorld { Value = float4x4.identity });
            m_Manager.SetComponentData(renderer, new MeshLODComponent
            {
                Group = group,
                ParentGroup = parentGroup,
                LODMask = 1,
            });

            return renderer;
        }
    }
}
