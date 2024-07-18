﻿using Content.Shared._RMC14.Medical.Wounds;
using Content.Shared._RMC14.Xenonids.Parasite;
using Content.Shared.Body.Organ;
using Robust.Shared.Containers;

namespace Content.Shared._RMC14.Medical.Stasis;

public sealed class CMStasisBagSystem : EntitySystem
{
    [Dependency] private readonly SharedXenoParasiteSystem _parasite = default!;

    private EntityQuery<OrganComponent> _organQuery;

    public override void Initialize()
    {
        base.Initialize();

        _organQuery = GetEntityQuery<OrganComponent>();

        SubscribeLocalEvent<CMStasisBagComponent, ContainerIsInsertingAttemptEvent>(OnStasisInsert);
        SubscribeLocalEvent<CMStasisBagComponent, ContainerIsRemovingAttemptEvent>(OnStasisRemove);

        SubscribeLocalEvent<CMInStasisComponent, CMMetabolizeAttemptEvent>(OnBloodstreamMetabolizeAttempt);
        SubscribeLocalEvent<CMInStasisComponent, MapInitEvent>(OnInStasisMapInit);
        SubscribeLocalEvent<CMInStasisComponent, ComponentRemove>(OnInStasisRemove);
        SubscribeLocalEvent<CMInStasisComponent, GetInfectedIncubationMultiplierEvent>(OnInStasisGetInfectedIncubationMultiplier);
        SubscribeLocalEvent<CMInStasisComponent, CMBleedAttemptEvent>(OnInStasisBleedAttempt);
    }

    private void OnStasisInsert(Entity<CMStasisBagComponent> ent, ref ContainerIsInsertingAttemptEvent args)
    {
        OnInsert(ent, args.EntityUid);
    }

    private void OnStasisRemove(Entity<CMStasisBagComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        OnRemove(ent, args.EntityUid);
    }

    private void OnBloodstreamMetabolizeAttempt(Entity<CMInStasisComponent> ent, ref CMMetabolizeAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnInStasisMapInit(Entity<CMInStasisComponent> ent, ref MapInitEvent args)
    {
        _parasite.RefreshIncubationMultipliers(ent.Owner);
    }

    private void OnInStasisRemove(Entity<CMInStasisComponent> ent, ref ComponentRemove args)
    {
        _parasite.RefreshIncubationMultipliers(ent.Owner);
    }

    private void OnInStasisGetInfectedIncubationMultiplier(Entity<CMInStasisComponent> ent, ref GetInfectedIncubationMultiplierEvent args)
    {
        if (ent.Comp.Running)
        {
            // less effective in late stages
            var multiplier = ent.Comp.IncubationMultiplier;
            if (args.stage >= ent.Comp.LessEffectiveStage)
                multiplier += (multiplier / 3);

            args.Multiply(multiplier);
        }
    }

    private void OnInStasisBleedAttempt(Entity<CMInStasisComponent> ent, ref CMBleedAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnInsert(Entity<CMStasisBagComponent> bag, EntityUid target)
    {
        EnsureComp<CMInStasisComponent>(target);
    }

    private void OnRemove(Entity<CMStasisBagComponent> bag, EntityUid target)
    {
        RemCompDeferred<CMInStasisComponent>(target);
    }

    public bool CanBodyMetabolize(EntityUid body)
    {
        // TODO RMC14 for now we need to call this manually from upstream code become upstream metabolism code is a sad joke
        var ev = new CMMetabolizeAttemptEvent();
        RaiseLocalEvent(body, ref ev);
        return !ev.Cancelled;
    }

    public bool CanOrganMetabolize(Entity<OrganComponent?> organ)
    {
        // TODO RMC14 for now we need to call this manually from upstream code become upstream metabolism code is a sad joke
        if (!_organQuery.Resolve(organ, ref organ.Comp, false) ||
            organ.Comp.Body is not { } body)
        {
            return true;
        }

        var ev = new CMMetabolizeAttemptEvent();
        RaiseLocalEvent(body, ref ev);
        return !ev.Cancelled;
    }
}
