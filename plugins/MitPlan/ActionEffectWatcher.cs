using System;
using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler;

namespace MitPlan;

internal sealed unsafe class ActionEffectWatcher : IDisposable
{
    private readonly Hook<Delegates.Receive> hook;
    private readonly ConcurrentQueue<uint> resolvedActions = new();

    public ActionEffectWatcher(IGameInteropProvider interop)
    {
        hook = interop.HookFromAddress<Delegates.Receive>(Addresses.Receive.Value, ReceiveDetour);
        hook.Enable();
    }

    public bool TryDequeue(out uint actionId) => resolvedActions.TryDequeue(out actionId);

    public void Clear()
    {
        while (resolvedActions.TryDequeue(out _)) { }
    }

    private void ReceiveDetour(
        uint casterEntityId,
        Character* caster,
        Vector3* targetPosition,
        Header* header,
        TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        hook.Original(casterEntityId, caster, targetPosition, header, effects, targetEntityIds);
        if (header != null && header->ActionId != 0)
            resolvedActions.Enqueue(header->ActionId);
    }

    public void Dispose() => hook.Dispose();
}
