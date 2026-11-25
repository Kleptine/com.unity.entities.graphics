# Upgrade the Entities Graphics package

This page describes the changes you need to make to your project when you upgrade the Entities Graphics package. If you upgrade across multiple versions, apply the changes for every version between your current version and the new one.

Starting with Unity 6.4, Entities Graphics is a [core package](https://docs.unity3d.com/Manual/pack-core.html) that is distributed with each version of the Unity Editor. The version of the package follows the Unity release, for example version 6.6.0 in Unity 6.6, instead of the previous 1.x versioning scheme.

## Update code that reads companion components

In Unity 6.6, an entity references its companion component using the `CompanionComponent<T>` type, an unmanaged `IComponentData` struct with a `CompanionRef` field that stores a `UnityObjectRef<T>`. The managed component APIs that previously read companion components are deprecated. Update your code as follows:

* Replace calls to the `EntityManager.GetComponentObject<T>` method with calls to the `EntityManager.GetCompanion<T>` method.
* In queries, use the `CompanionComponent<T>` type instead of the component type. For example, replace the `WithAll<Light>()` call with `WithAll<CompanionComponent<Light>>()`.
* In a `SystemAPI.Query` call, replace the `SystemAPI.ManagedAPI.UnityEngineComponent<T>` type with `CompanionComponent<T>`, and read the component from the `CompanionRef` field.

During baking, Unity still creates the companion GameObject and attaches the same `UnityEngine.Component` instances to it, so the components that your systems read are not changed. For more information, refer to [Companion components](companion-components.md).

## Upgrade to Entities Graphics version 1.0

To upgrade to Entities Graphics package version 1.0, you need to do the following: 

* Remove usage of HLOD.
* Replace runtime usage of `RenderMesh` with `RenderMeshArray`.
* Replace usage of rendering settings in `RenderMesh` with rendering settings in `RenderFilterSettings`.
* Replace Managed SharedComponent `RenderMesh` with unmanaged IComponentData `RenderMeshUnmanaged` for big performance gain while baking

### Remove HLOD

HLOD is a feature created specifically for the MegaCity demo and has been removed from the 1.0 release of the Entities Graphics package. To upgrade Entities Graphics to 1.0, you must remove any usage of HLOD in your project.

### Replace runtime usage of RenderMesh with RenderMeshArray

Previously, Entities Graphics used the RenderMesh shared component at runtime to create batches for rendering. Entities Graphics 1.0 replaces this with the RenderMeshArray shared component and the MaterialMeshInfo component.

The `RenderMesh` component still exists as a convenient intermediate step during entity baking, but Entities Graphics ignores it at runtime. To upgrade Entities Graphics to 1.0, you must update any code that uses `RenderMesh` or the `RenderMeshUtility.AddComponents` APIs to use the new `RenderMeshArray` versions. For more information, refer to [Runtime entity creation](runtime-entity-creation.md).

### Replace usage of rendering settings in RenderMesh with rendering settings in RenderFilterSettings

Entities Graphics 1.0 moves many rendering settings from the `RenderMesh` shared component to the `RenderFilterSettings` shared component. This includes settings such as rendering layer settings, motion vector rendering settings, and shadow rendering settings. To upgrade Entities Graphics to 1.0, you must update any code that uses the settings in `RenderMesh` to use the settings in `RenderFilterSettings`.

For a full list of changes and updates in this version, refer to the Entities Graphics package [changelog](xref:changelog).

### Replace Managed SharedComponent `RenderMesh` with unmanaged IComponentData `RenderMeshUnmanaged` for big performance gain while baking

Added an unmanaged version of `RenderMesh` as a normal `IComponentData`. 
Similar to `RenderMesh`, when `RenderMeshUnmanaged` is added in a baker, it will ensure a mesh and material is correctly put into the baked `RenderMeshArray`.

