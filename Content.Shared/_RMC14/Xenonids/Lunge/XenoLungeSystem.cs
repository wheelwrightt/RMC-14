﻿using Content.Shared._RMC14.Marines;
using Content.Shared.Coordinates;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Lunge;

public sealed class XenoLungeSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ThrownItemComponent> _thrownItemQuery;

    public override void Initialize()
    {
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _thrownItemQuery = GetEntityQuery<ThrownItemComponent>();

        SubscribeLocalEvent<XenoLungeComponent, XenoLungeActionEvent>(OnXenoLungeAction);
        SubscribeLocalEvent<XenoLungeComponent, ThrowDoHitEvent>(OnXenoLungeHit);
    }

    private void OnXenoLungeAction(Entity<XenoLungeComponent> xeno, ref XenoLungeActionEvent args)
    {
        if (!_xeno.CanAbilityAttackTarget(xeno, args.Target))
            return;

        if (args.Handled)
            return;

        var attempt = new XenoLungeAttemptEvent();
        RaiseLocalEvent(xeno, ref attempt);

        if (attempt.Cancelled)
            return;

        args.Handled = true;

        var origin = _transform.GetMapCoordinates(xeno);
        var target = _transform.GetMapCoordinates(args.Target);
        var diff = target.Position - origin.Position;
        var length = diff.Length();
        diff *= xeno.Comp.Range / length;

        xeno.Comp.Charge = diff;
        Dirty(xeno);

        _throwing.TryThrow(xeno, diff, 10, animated: false);
    }

    private void OnXenoLungeHit(Entity<XenoLungeComponent> xeno, ref ThrowDoHitEvent args)
    {
        // TODO RMC14 lag compensation
        var targetId = args.Target;
        if (_physicsQuery.TryGetComponent(xeno, out var physics) &&
            _thrownItemQuery.TryGetComponent(xeno, out var thrown))
        {
            _thrownItem.LandComponent(xeno, thrown, physics, true);
            _thrownItem.StopThrow(xeno, thrown);
        }

        if (_timing.IsFirstTimePredicted && xeno.Comp.Charge != null)
            xeno.Comp.Charge = null;

        if (TryComp(xeno, out XenoComponent? xenoComp) &&
            TryComp(targetId, out XenoComponent? targetXeno) &&
            xenoComp.Hive == targetXeno.Hive)
        {
            return;
        }

        _stun.TryParalyze(targetId, xeno.Comp.StunTime, true);
        _pulling.TryStartPull(xeno, targetId);

        if (_net.IsServer &&
            HasComp<MarineComponent>(targetId))
        {
            SpawnAttachedTo(xeno.Comp.Effect, targetId.ToCoordinates());
        }
    }
}
