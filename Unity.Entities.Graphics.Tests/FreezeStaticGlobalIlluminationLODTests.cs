using NUnit.Framework;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;

namespace Unity.Entities.Graphics.Tests
{
    internal class FreezeStaticGlobalIlluminationLODTests
    {
        World m_World;
        EntityManager m_Manager;
        FreezeStaticGlobalIlluminationLOD m_System;

        [SetUp]
        public void SetUp()
        {
            m_World = new World("FreezeStaticGlobalIlluminationLODTests");
            m_Manager = m_World.EntityManager;
            m_System = m_World.GetOrCreateSystemManaged<FreezeStaticGlobalIlluminationLOD>();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();
        }

        [Test]
        public void StaticRendererOutsideSelection_IsTagged()
        {
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true);

            m_System.Update();

            Assert.IsTrue(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A static renderer outside the group's GI LOD level should be tagged before the strip.");
        }

        [Test]
        public void StaticRendererInsideSelection_IsNotTagged()
        {
            var group = CreateGroup(globalIlluminationLod: 1, isStatic: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true);

            m_System.Update();

            Assert.IsFalse(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A static renderer inside the group's GI LOD level should contribute like an unmanaged renderer.");
        }

        [Test]
        public void DynamicRendererAndGroup_IsNotTagged()
        {
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: false);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: false);

            m_System.Update();

            Assert.IsFalse(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A fully dynamic group keeps its indirection, so the runtime lookup stays authoritative.");
        }

        [Test]
        public void StaticGroupWithDynamicRenderer_IsTagged()
        {
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: false);

            m_System.Update();

            Assert.IsTrue(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A renderer whose group loses its components to the strip should carry the frozen selection.");
        }

        [Test]
        public void StaticRendererWithoutGlobalIlluminationLod_IsTagged()
        {
            var group = CreateGroup(globalIlluminationLod: -1, isStatic: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 0, isStatic: true);

            m_System.Update();

            Assert.IsTrue(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A group without a GlobalIlluminationLOD component contributes nothing.");
        }

        [Test]
        public void StaticRendererUnderDynamicGroupOutsideSelection_IsTagged()
        {
            // The renderer loses its membership at bake, so a later selection change on the still-dynamic
            // group cannot reach it: the selection is frozen for the renderer in both directions.
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: false);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true);

            m_System.Update();

            Assert.IsTrue(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A static renderer outside the selection should carry the frozen exclusion even under a dynamic group.");
        }

        [Test]
        public void StaticRendererUnderDynamicGroupInsideSelection_IsNotTagged()
        {
            var group = CreateGroup(globalIlluminationLod: 1, isStatic: false);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true);

            m_System.Update();

            Assert.IsFalse(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A static renderer inside the selection should contribute like an unmanaged renderer.");
        }

        [Test]
        public void DisabledRendererUnderStaticGroupOutsideSelection_IsTagged()
        {
            // The strip skips Disabled entities, so the renderer keeps its membership while the active
            // static group loses its components; without the tag it would contribute at every level
            // once activated at runtime.
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true, isDisabled: true);

            m_System.Update();

            Assert.IsTrue(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A disabled renderer outside the selection should be tagged when its group is stripped.");
        }

        [Test]
        public void DisabledStaticGroupWithDynamicRenderer_IsNotTagged()
        {
            // The strip also skips a Disabled group, so its components survive and the runtime lookup
            // keeps applying the selection.
            var group = CreateGroup(globalIlluminationLod: 0, isStatic: true, isDisabled: true);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: false);

            m_System.Update();

            Assert.IsFalse(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A renderer whose disabled group keeps its components should stay on the runtime lookup.");
        }

        [Test]
        public void StaticRendererWithGrouplessGroupEntity_IsNotTagged()
        {
            var group = m_Manager.CreateEntity();
            m_Manager.AddComponent<Static>(group);
            var renderer = CreateRenderer(group, lodMask: 1 << 1, isStatic: true);

            m_System.Update();

            Assert.IsFalse(m_Manager.HasComponent<ExcludedFromGlobalIllumination>(renderer),
                "A renderer whose group has no MeshLODGroupComponent is treated as a normal renderer.");
        }

        Entity CreateGroup(int globalIlluminationLod, bool isStatic, bool isDisabled = false)
        {
            var group = m_Manager.CreateEntity(typeof(MeshLODGroupComponent));
            if (globalIlluminationLod >= 0)
                m_Manager.AddComponentData(group, new GlobalIlluminationLOD { Value = globalIlluminationLod });
            if (isStatic)
                m_Manager.AddComponent<Static>(group);
            if (isDisabled)
                m_Manager.AddComponent<Disabled>(group);
            return group;
        }

        Entity CreateRenderer(Entity group, int lodMask, bool isStatic, bool isDisabled = false)
        {
            var renderer = m_Manager.CreateEntity();
            m_Manager.AddComponentData(renderer, new MeshLODComponent { Group = group, LODMask = lodMask });
            if (isStatic)
                m_Manager.AddComponent<Static>(renderer);
            if (isDisabled)
                m_Manager.AddComponent<Disabled>(renderer);
            return renderer;
        }
    }
}
