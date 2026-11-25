---
uid: whats-new
---

# What's new in Entities Graphics

Discover new features and performance improvements in the latest update to Entities Graphics.

For more information and for fixes in other versions, refer to the [changelog](../changelog/CHANGELOG.html).

## Version 6.4

Entities Graphics is now distributed alongside each version of the Unity Editor as a [core package](https://docs.unity3d.com/Manual/pack-core.html). You no longer need to install or update the package in the Package Manager window.

## Version 1.4.12

### Mesh LOD support

Entities Graphics now supports [Mesh LOD](https://docs.unity3d.com/Manual/lod/mesh-lod-landing.html), which reduces the number of polygons Unity has to draw and provides automatic LOD creation.

### Adaptive Probe Volumes in subscenes

You can now use [Adaptive Probe Volume (APV)](https://docs.unity3d.com/Manual/urp/probevolumes.html) components in subscenes. For more information, refer to [Companion components](companion-components.md).

## Version 1.4.5

### Motion vectors for entities in URP

You can now add motion vectors from entities to the motion vector render pass in the Universal Render Pipeline (URP), using the new `PerVertexMotionVectors_Tag` component. Unity automatically adds this component to entities you bake from `MeshRenderer` components.

## Version 1.4.2

### Manually prune the upload buffer

You can now manually ask Unity to prune the upload buffer pool using the new `PruneUploadBufferPool` API. As a result, you have more control over how much memory the GPU uses at runtime.

## Version 1.2.1

### Non-allocating material references and mesh references for improved performance

The `RenderMeshArray.Materials` and `RenderMeshArray.Meshes` properties are now deprecated. Use `RenderMeshArray.MaterialReferences` and `RenderMeshArray.MeshReferences` instead. These properties store meshes and materials as `UnityObjectRef<T>` values, which are more efficient to access and don't require managed arrays.

## Version 1.2.0

### RenderMeshUnmanaged component for improved baking performance

The `RenderMesh` baking shared component is now obsolete. Use `RenderMeshUnmanaged` instead. `RenderMeshUnmanaged` is an unmanaged `IComponentData` that stores its mesh and material as `UnityObjectRef<T>` values, which are more efficient to access and don't require managed arrays.

## Version 1.1

### Decal projectors in subscenes

You can now use [decal projector components](https://docs.unity3d.com/Manual/urp/renderer-feature-decal-projector-reference.html) in subscenes. For more information, refer to [Companion components](companion-components.md).

### LOD improvements in subscenes

You can now use the same mesh in multiple LOD levels within a single `LODGroup` inside a subscene. Unity now also uses the **Maximum LOD Level** setting in **Project Settings** > **Quality** for entities in subscenes. For more information, refer to [Import or create LOD levels for a LOD group](https://docs.unity3d.com/Manual/lod-group-configure.html).

### Toggle entities visibility more quickly

You can now use the `SetEnabled` method of a `MaterialMeshInfo` component to toggle rendering an entity. As a result you can make entities visible or invisible quickly without adding or removing components. For more information, refer to [Runtime entity creation](runtime-entity-creation.md).

## Version 1.0

Summary of changes in Entities Graphics package version 1.0.

The main updates in this release include:

### Added

* Added support for OpenGL ES 3.1 on Android.
* New `RegisterMesh` and `RegisterMaterial` APIs.
* Added efficient Mesh and Material switching at runtime using `MaterialMeshInfo`.

#### New RegisterMesh and RegisterMaterial APIs

In Entities Graphics 1.0, you can directly register your own meshes and materials to use for rendering. To do this, call the new `EntitiesGraphicsSystem.RegisterMaterial` and `EntitiesGraphicsSystem.RegisterMesh` APIs to get Burst-compatible IDs that can be placed in a `MaterialMeshInfo` component.

#### Efficient Mesh and Material switching at runtime using MaterialMeshInfo

Entities Graphics 1.0 uses the new `MaterialMeshInfo` component to specify the mesh and material for entities. `MaterialMeshInfo` is a normal IComponentData, and you can change its value efficiently, unlike a shared component. This makes it possible to efficiently change the mesh and material that Unity uses to render an entity.

To use the new `MaterialMeshInfo` component, you either need to manually register materials and meshes using the new `RegisterMaterial` and `RegisterMesh` APIs, or you can also use array indices to select the corresponding mesh and material from the `RenderMeshArray` shared component, if the entity has one.

Entities that use manually registered IDs don't need to have a `RenderMeshArray` component, while entities that use array indices must have one. Entities Graphics 1.0 entity baking uses `RenderMeshArray`.

### Updated

* Updated the name of the package from Hybrid Renderer to Entities Graphics.
* Updated Scene view entity picking to use [BatchRendererGroup](https://docs.unity3d.com/2022.1/Documentation/Manual/batch-renderer-group.html).
* Rework sub-mesh handling for MeshRenderer components.

#### Rework sub-mesh handling

In previous versions, Entities Graphics generated one entity per sub-mesh on a MeshRenderer. Those entities were mostly duplicates, wasted a lot of memory, and negatively impacted performance.  

To avoid this behavior, add a scripting define to your project. Go to **Edit &gt; Project Settings &gt; Player &gt; Scripting Define Symbols** and add the define `ENABLE_MESH_RENDERER_SUBMESH_DATA_SHARING` to the list.  

The duplicated entities will no longer be created to handle sub-meshes except when using SkinnedMeshRenderer. If your project relies on this behavior, then you might need to manually create one entity per sub-mesh yourself, because Entities Graphics won't do so anymore.

### Removed

* Removed the HLOD component.

For a full list of changes and updates in this version, refer to the Entities Graphics package [changelog](xref:changelog).

## Additional resources

- [Changelog](../changelog/CHANGELOG.html)
