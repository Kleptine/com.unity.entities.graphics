# Companion components

This feature allows you to query `MonoBehaviour` components in ECS systems by attaching them to entities, without converting them to `IComponentData`. The link between an entity and the component it references is stored as `CompanionComponent<T>`, an unmanaged `IComponentData` that wraps a `UnityObjectRef<T>`, so ECS queries can filter on it like any other unmanaged component. Dereferencing the wrapper to reach the underlying `UnityEngine.Component` goes through managed code, so the per-entity work that touches the component itself does not get the fast performance that pure ECS components have.

## Supported components

Entities Graphics bakes the following component types into companion components:

- Light
- LightProbeProxyVolume
- ReflectionProbe
- TextMesh, together with the MeshRenderer on the same GameObject
- SpriteRenderer
- ParticleSystem, together with the ParticleSystemRenderer on the same GameObject
- VisualEffect
- AudioSource
- DecalProjector (URP and HDRP)
- HDAdditionalLightData (HDRP)
- HDAdditionalReflectionData (HDRP)
- LocalVolumetricFog (HDRP)
- PlanarReflectionProbe (HDRP)
- UniversalAdditionalLightData (URP)
- Volume
- SphereCollider, BoxCollider, CapsuleCollider, and MeshCollider, together with a Volume on the same GameObject (local volumes)
- ProbeVolume and ProbeVolumePerSceneData (Adaptive Probe Volumes)

If a component isn't in the list above, Unity removes it from the companion GameObject during baking. This includes components such as `Animator` and your own `MonoBehaviour` classes. Unity also doesn't preserve the hierarchy of Transform components, and creates every companion GameObject as a root GameObject.

Unity doesn't bake Camera components (including the `HDAdditionalCameraData` and `UniversalAdditionalCameraData` components) into companion components by default, because the main camera of a scene can't be a companion component entity. To enable this, add a `HYBRID_ENTITIES_CAMERA_CONVERSION` [custom scripting symbol](https://docs.unity3d.com/Manual/custom-scripting-symbols.html).

## Companion component entities

Unity updates the transform of a companion component entity whenever it updates the LocalToWorld component. Parenting a companion component entity to a standard entity is supported. Companion component entities can be included in subscenes. Unity serializes the companion GameObject and the components attached to it in the subscene.

## Query companion components

You can write ECS queries on `CompanionComponent<T>` like any other `IComponentData`. The query iteration itself is Burst-friendly, but reading the wrapped `UnityEngine.Component` is a managed call that must run on the main thread, so the system that does the dereference cannot be Burst-compiled and should iterate with `foreach()` rather than scheduling an [IJobEntity](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IJobEntity.html).

An example of setting HDRP Light component intensity:

```csharp
class AnimateHDRPIntensitySystem : SystemBase
{
    protected override void OnUpdate()
    {
        foreach (var companion in SystemAPI.Query<RefRO<CompanionComponent<HDAdditionalLightData>>>())
        {
            HDAdditionalLightData hdLight = companion.ValueRO.CompanionRef;
            hdLight.intensity = 1.5f;
        }
    }
}
```

The implicit conversion from `UnityObjectRef<T>` to `T` resolves the reference back to the live `UnityEngine.Component` instance. As an alternative, `EntityManager.GetCompanion<T>(entity)` returns the same instance given the entity.


## Additional resources

- [`IComponentData`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.IComponentData.html)
- [`EntityManager`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.EntityManager.html)
- [Upgrade the Entities Graphics package](upgrade-guide.md#update-code-that-reads-companion-components)
