﻿using Content.Shared._RMC14.CCVar;
using Content.Shared._RMC14.Damage;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Medical.Scanner;
using Content.Shared._RMC14.NightVision;
using Content.Shared._RMC14.Vendors;
using Content.Shared._RMC14.Xenonids.Construction.Nest;
using Content.Shared._RMC14.Xenonids.Evolution;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared._RMC14.Xenonids.Pheromones;
using Content.Shared._RMC14.Xenonids.Plasma;
using Content.Shared._RMC14.Xenonids.Rest;
using Content.Shared._RMC14.Xenonids.Weeds;
using Content.Shared.Access.Components;
using Content.Shared.Actions;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction.Events;
using Content.Shared.Lathe;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Radio;
using Content.Shared.Standing;
using Content.Shared.UserInterface;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids;

public sealed class XenoSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _action = default!;
    [Dependency] private readonly CMDamageableSystem _cmDamageable = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _mobThresholds = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly SharedNightVisionSystem _nightVision = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly XenoPlasmaSystem _xenoPlasma = default!;

    private EntityQuery<AffectableByWeedsComponent> _affectableQuery;
    private EntityQuery<DamageableComponent> _damageableQuery;
    private EntityQuery<MarineComponent> _marineQuery;
    private EntityQuery<MobStateComponent> _mobStateQuery;
    private EntityQuery<MobThresholdsComponent> _mobThresholdsQuery;
    private EntityQuery<XenoNestedComponent> _xenoNestedQuery;
    private EntityQuery<XenoPlasmaComponent> _xenoPlasmaQuery;
    private EntityQuery<XenoRecoveryPheromonesComponent> _xenoRecoveryQuery;

    private float _xenoDamageDealtMultiplier;
    private float _xenoDamageReceivedMultiplier;
    private float _xenoSpeedMultiplier;

    public override void Initialize()
    {
        base.Initialize();

        _affectableQuery = GetEntityQuery<AffectableByWeedsComponent>();
        _damageableQuery = GetEntityQuery<DamageableComponent>();
        _marineQuery = GetEntityQuery<MarineComponent>();
        _mobStateQuery = GetEntityQuery<MobStateComponent>();
        _mobThresholdsQuery = GetEntityQuery<MobThresholdsComponent>();
        _xenoNestedQuery = GetEntityQuery<XenoNestedComponent>();
        _xenoPlasmaQuery = GetEntityQuery<XenoPlasmaComponent>();
        _xenoRecoveryQuery = GetEntityQuery<XenoRecoveryPheromonesComponent>();

        SubscribeLocalEvent<XenoComponent, MapInitEvent>(OnXenoMapInit);
        SubscribeLocalEvent<XenoComponent, GetAccessTagsEvent>(OnXenoGetAdditionalAccess);
        SubscribeLocalEvent<XenoComponent, NewXenoEvolvedEvent>(OnNewXenoEvolved);
        SubscribeLocalEvent<XenoComponent, XenoDevolvedEvent>(OnXenoDevolved);
        SubscribeLocalEvent<XenoComponent, HealthScannerAttemptTargetEvent>(OnXenoHealthScannerAttemptTarget);
        SubscribeLocalEvent<XenoComponent, GetDefaultRadioChannelEvent>(OnXenoGetDefaultRadioChannel);
        SubscribeLocalEvent<XenoComponent, AttackAttemptEvent>(OnXenoAttackAttempt);
        SubscribeLocalEvent<XenoComponent, UserOpenActivatableUIAttemptEvent>(OnXenoOpenActivatableUIAttempt);
        SubscribeLocalEvent<XenoComponent, GetMeleeDamageEvent>(OnXenoGetMeleeDamage);
        SubscribeLocalEvent<XenoComponent, DamageModifyEvent>(OnXenoDamageModify);
        SubscribeLocalEvent<XenoComponent, RefreshMovementSpeedModifiersEvent>(OnXenoRefreshSpeed);

        Subs.CVar(_config, CMCVars.CMXenoDamageDealtMultiplier, v => _xenoDamageDealtMultiplier = v, true);
        Subs.CVar(_config, CMCVars.CMXenoDamageReceivedMultiplier, v => _xenoDamageReceivedMultiplier = v, true);
        Subs.CVar(_config, CMCVars.CMXenoSpeedMultiplier, UpdateXenoSpeedMultiplier, true);

        UpdatesAfter.Add(typeof(SharedXenoPheromonesSystem));
    }

    private void OnXenoMapInit(Entity<XenoComponent> xeno, ref MapInitEvent args)
    {
        foreach (var actionId in xeno.Comp.ActionIds)
        {
            if (!xeno.Comp.Actions.ContainsKey(actionId) &&
                _action.AddAction(xeno, actionId) is { } newAction)
            {
                xeno.Comp.Actions[actionId] = newAction;
            }
        }

        xeno.Comp.NextRegenTime = _timing.CurTime + xeno.Comp.RegenCooldown;
        Dirty(xeno);

        if (!MathHelper.CloseTo(_xenoSpeedMultiplier, 1))
            _movementSpeed.RefreshMovementSpeedModifiers(xeno);
    }

    private void OnXenoGetAdditionalAccess(Entity<XenoComponent> xeno, ref GetAccessTagsEvent args)
    {
        args.Tags.UnionWith(xeno.Comp.AccessLevels);
    }

    private void OnNewXenoEvolved(Entity<XenoComponent> newXeno, ref NewXenoEvolvedEvent args)
    {
        var oldRotation = _transform.GetWorldRotation(args.OldXeno);
        _transform.SetWorldRotation(newXeno, oldRotation);
    }

    private void OnXenoDevolved(Entity<XenoComponent> newXeno, ref XenoDevolvedEvent args)
    {
        var oldRotation = _transform.GetWorldRotation(args.OldXeno);
        _transform.SetWorldRotation(newXeno, oldRotation);
    }

    private void OnXenoHealthScannerAttemptTarget(Entity<XenoComponent> ent, ref HealthScannerAttemptTargetEvent args)
    {
        args.Popup = "The scanner can't make sense of this creature.";
        args.Cancelled = true;
    }

    private void OnXenoGetDefaultRadioChannel(Entity<XenoComponent> ent, ref GetDefaultRadioChannelEvent args)
    {
        args.Channel = SharedChatSystem.HivemindChannel;
    }

    private void OnXenoAttackAttempt(Entity<XenoComponent> xeno, ref AttackAttemptEvent args)
    {
        if (args.Target is not { } target)
            return;

        // TODO RMC14 different hives
        // TODO RMC14 this still falsely plays the hit red flash effect on xenos if others are hit in a wide swing
        if (HasComp<XenoFriendlyComponent>(target) ||
            _mobState.IsDead(target))
        {
            args.Cancel();
        }
    }

    private void OnXenoOpenActivatableUIAttempt(Entity<XenoComponent> ent, ref UserOpenActivatableUIAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (HasComp<LatheComponent>(args.Target) ||
            HasComp<CMAutomatedVendorComponent>(args.Target))
        {
            args.Cancel();
        }
    }

    private void OnXenoGetMeleeDamage(Entity<XenoComponent> ent, ref GetMeleeDamageEvent args)
    {
        if (MathHelper.CloseTo(_xenoDamageDealtMultiplier, 1))
            return;

        args.Damage *= _xenoDamageDealtMultiplier;
    }

    private void OnXenoDamageModify(Entity<XenoComponent> ent, ref DamageModifyEvent args)
    {
        if (MathHelper.CloseTo(_xenoDamageReceivedMultiplier, 1))
            return;

        args.Damage *= _xenoDamageReceivedMultiplier;
    }

    private void OnXenoRefreshSpeed(Entity<XenoComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (MathHelper.CloseTo(_xenoSpeedMultiplier, 1))
            return;

        args.ModifySpeed(_xenoSpeedMultiplier, _xenoSpeedMultiplier);
    }

    private void UpdateXenoSpeedMultiplier(float speed)
    {
        _xenoSpeedMultiplier = speed;

        var xenos = EntityQueryEnumerator<XenoComponent, MovementSpeedModifierComponent>();
        while (xenos.MoveNext(out var uid, out _, out var comp))
        {
            _movementSpeed.RefreshMovementSpeedModifiers(uid, comp);
        }
    }

    public void MakeXeno(Entity<XenoComponent?> xeno)
    {
        EnsureComp<XenoComponent>(xeno);
    }

    public void SetHive(Entity<XenoComponent?> xeno, Entity<HiveComponent?>? hive)
    {
        if (!Resolve(xeno, ref xeno.Comp))
            return;

        if (hive == null)
        {
            xeno.Comp.Hive = null;
            Dirty(xeno, xeno.Comp);
            return;
        }

        var hiveEnt = hive.Value;
        if (!Resolve(hiveEnt, ref hiveEnt.Comp))
            return;

        xeno.Comp.Hive = hive;
        Dirty(xeno, xeno.Comp);

        _nightVision.SetSeeThroughContainers(xeno.Owner, hiveEnt.Comp.SeeThroughContainers);
    }

    public void SetSameHive(Entity<XenoComponent?> to, Entity<XenoComponent?> from)
    {
        if (!Resolve(from, ref from.Comp))
            return;

        SetHive(to, from.Comp.Hive);
    }

    private FixedPoint2 GetWeedsHealAmount(Entity<XenoComponent> xeno)
    {
        if (!_mobThresholdsQuery.TryComp(xeno, out var thresholds) ||
            !_mobThresholds.TryGetIncapThreshold(xeno, out var threshold, thresholds))
        {
            return FixedPoint2.Zero;
        }

        FixedPoint2 multiplier;
        if (_mobState.IsCritical(xeno))
            multiplier = xeno.Comp.RestHealMultiplier; // TODO RMC14
        else if (_standing.IsDown(xeno) || HasComp<XenoRestingComponent>(xeno))
            multiplier = xeno.Comp.RestHealMultiplier;
        else
            multiplier = xeno.Comp.StandHealingMultiplier;

        var passiveHeal = threshold.Value / 65 + xeno.Comp.FlatHealing;
        var recovery = (CompOrNull<XenoRecoveryPheromonesComponent>(xeno)?.Multiplier ?? 0) / 2;
        var recoveryHeal = (threshold.Value / 65) * (recovery / 2);
        return (passiveHeal + recoveryHeal) * multiplier / 2;
    }

    public void HealDamage(Entity<DamageableComponent?> xeno, FixedPoint2 amount)
    {
        if (!_damageableQuery.Resolve(xeno, ref xeno.Comp, false) ||
            xeno.Comp.Damage.GetTotal() <= FixedPoint2.Zero)
        {
            return;
        }

        if (_mobStateQuery.TryGetComponent(xeno, out var mobState) &&
            _mobState.IsDead(xeno, mobState))
        {
            return;
        }

        var heal = _cmDamageable.DistributeTypes((xeno, xeno.Comp), -amount);

        if (heal.GetTotal() > FixedPoint2.Zero)
        {
            Log.Error($"Tried to deal damage while healing xeno {ToPrettyString(xeno)}");
            return;
        }

        _damageable.TryChangeDamage(xeno, heal);
    }

    // TODO RMC14 generalize this for survivors, synthetics, enemy hives, etc
    public bool CanHitLiving(EntityUid xeno, EntityUid defender)
    {
        return _marineQuery.HasComponent(defender);
    }

    public bool FromSameHive(Entity<XenoComponent?> xenoOne, Entity<XenoComponent?> xenoTwo)
    {
        if (!Resolve(xenoOne, ref xenoOne.Comp, false) ||
            !Resolve(xenoTwo, ref xenoTwo.Comp, false))
        {
            return false;
        }

        return xenoOne.Comp.Hive == xenoTwo.Comp.Hive;
    }

    public bool CanAbilityAttackTarget(EntityUid xeno, EntityUid target)
    {
        // TODO RMC14 xenos of the same hive
        if (xeno == target)
            return false;

        if (_mobState.IsDead(target))
            return false;

        if (_xenoNestedQuery.HasComp(target))
            return false;

        return HasComp<MarineComponent>(target);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<XenoComponent>();
        var time = _timing.CurTime;

        while (query.MoveNext(out var uid, out var xeno))
        {
            if (time < xeno.NextRegenTime)
                continue;

            xeno.NextRegenTime = time + xeno.RegenCooldown;
            Dirty(uid, xeno);

            if (!_affectableQuery.TryComp(uid, out var affectable) ||
                !affectable.OnXenoWeeds)
            {
                if (_xenoPlasmaQuery.TryComp(uid, out var plasma))
                {
                    var amount = FixedPoint2.Max(plasma.PlasmaRegenOffWeeds * plasma.MaxPlasma / 100 / 2, 0.01);
                    _xenoPlasma.RegenPlasma((uid, plasma), amount);
                }

                continue;
            }

            var heal = GetWeedsHealAmount((uid, xeno));
            if (heal > FixedPoint2.Zero)
            {
                HealDamage(uid, heal);

                if (_xenoPlasmaQuery.TryComp(uid, out var plasma))
                {
                    var plasmaRestored = plasma.PlasmaRegenOnWeeds * plasma.MaxPlasma / 100 / 2;
                    _xenoPlasma.RegenPlasma((uid, plasma), plasmaRestored);

                    if (_xenoRecoveryQuery.TryComp(uid, out var recovery))
                    {
                        var amount = plasmaRestored * recovery.Multiplier / 4;
                        _xenoPlasma.RegenPlasma((uid, plasma), amount);
                    }
                }
            }
        }
    }
}
