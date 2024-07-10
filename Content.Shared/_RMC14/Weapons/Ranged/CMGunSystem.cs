﻿using System.Numerics;
using Content.Shared._RMC14.Marines.Skills;
using Content.Shared._RMC14.Weapons.Ranged.Whitelist;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Timing;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Weapons.Ranged;

public sealed class CMGunSystem : EntitySystem
{
    [Dependency] private readonly SharedBroadphaseSystem _broadphase = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SkillsSystem _skills = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UseDelaySystem _useDelay = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;

    public override void Initialize()
    {
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();

        SubscribeLocalEvent<AmmoFixedDistanceComponent, AmmoShotEvent>(OnAmmoFixedDistanceShot);

        SubscribeLocalEvent<ProjectileFixedDistanceComponent, ComponentRemove>(OnProjectileStop);
        SubscribeLocalEvent<ProjectileFixedDistanceComponent, PhysicsSleepEvent>(OnProjectileStop);

        SubscribeLocalEvent<GunShowUseDelayComponent, GunShotEvent>(OnShowUseDelayShot);
        SubscribeLocalEvent<GunShowUseDelayComponent, ItemWieldedEvent>(OnShowUseDelayWielded);

        SubscribeLocalEvent<GunUserWhitelistComponent, AttemptShootEvent>(OnGunUserWhitelistAttemptShoot);

        SubscribeLocalEvent<GunUnskilledPenaltyComponent, GotEquippedHandEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunUnskilledPenaltyComponent, GotUnequippedHandEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunUnskilledPenaltyComponent, GunRefreshModifiersEvent>(OnGunUnskilledPenaltyRefresh);

        SubscribeLocalEvent<GunDamageModifierComponent, AmmoShotEvent>(OnGunDamageModifierAmmoShot);
        SubscribeLocalEvent<GunDamageModifierComponent, MapInitEvent>(OnGunDamageModifierMapInit);

        SubscribeLocalEvent<GunSkilledRecoilComponent, GotEquippedHandEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunSkilledRecoilComponent, GotUnequippedHandEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunSkilledRecoilComponent, ItemWieldedEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunSkilledRecoilComponent, ItemUnwieldedEvent>(TryRefreshGunModifiers);
        SubscribeLocalEvent<GunSkilledRecoilComponent, GunRefreshModifiersEvent>(OnRecoilSkilledRefreshModifiers);
    }

    private void OnAmmoFixedDistanceShot(Entity<AmmoFixedDistanceComponent> ent, ref AmmoShotEvent args)
    {
        if (!TryComp(ent, out GunComponent? gun) ||
            gun.ShootCoordinates is not { } target)
        {
            return;
        }

        var from = _transform.GetMapCoordinates(ent);
        var to = _transform.ToMapCoordinates(target);
        if (from.MapId != to.MapId)
            return;

        var direction = to.Position - from.Position;
        if (direction == Vector2.Zero)
            return;

        var distance = ent.Comp.MaxRange != null ? Math.Min(ent.Comp.MaxRange.Value, direction.Length()) : direction.Length();
        var time = _timing.CurTime;
        var normalized = direction.Normalized();
        foreach (var projectile in args.FiredProjectiles)
        {
            if (!_physicsQuery.TryComp(projectile, out var physics))
                continue;

            var impulse = normalized * gun.ProjectileSpeedModified * physics.Mass;
            _physics.SetLinearVelocity(projectile, Vector2.Zero, body: physics);
            _physics.ApplyLinearImpulse(projectile, impulse, body: physics);
            _physics.SetBodyStatus(projectile, physics, BodyStatus.InAir);

            var comp = EnsureComp<ProjectileFixedDistanceComponent>(projectile);
            comp.FlyEndTime = time + TimeSpan.FromSeconds(distance / gun.ProjectileSpeedModified);
        }
    }

    private void OnProjectileStop<T>(Entity<ProjectileFixedDistanceComponent> ent, ref T args)
    {
        StopProjectile(ent);
    }

    private void OnShowUseDelayShot(Entity<GunShowUseDelayComponent> ent, ref GunShotEvent args)
    {
        UpdateDelay(ent);
    }

    private void OnShowUseDelayWielded(Entity<GunShowUseDelayComponent> ent, ref ItemWieldedEvent args)
    {
        UpdateDelay(ent);
    }

    private void OnGunUserWhitelistAttemptShoot(Entity<GunUserWhitelistComponent> ent, ref AttemptShootEvent args)
    {
        if (args.Cancelled)
            return;

        if (_whitelist.IsValid(ent.Comp.Whitelist, args.User))
            return;

        args.Cancelled = true;
        _popup.PopupClient(Loc.GetString("cm-gun-unskilled", ("gun", ent.Owner)), args.User, args.User);
    }

    private void OnGunUnskilledPenaltyRefresh(Entity<GunUnskilledPenaltyComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (TryGetUserSkills(ent, out var skills) &&
            skills.Comp.Skills.Firearms >= ent.Comp.Firearms)
        {
            return;
        }

        args.MinAngle += ent.Comp.AngleIncrease;
        args.MaxAngle += ent.Comp.AngleIncrease;
    }

    private void OnGunDamageModifierMapInit(Entity<GunDamageModifierComponent> ent, ref MapInitEvent args)
    {
        RefreshGunDamageMultiplier((ent.Owner, ent.Comp));
    }

    private void OnGunDamageModifierAmmoShot(Entity<GunDamageModifierComponent> ent, ref AmmoShotEvent args)
    {
        foreach (var projectile in args.FiredProjectiles)
        {
            if (!_projectileQuery.TryGetComponent(projectile, out var comp))
                continue;

            comp.Damage *= ent.Comp.ModifiedMultiplier;
        }
    }

    private void OnRecoilSkilledRefreshModifiers(Entity<GunSkilledRecoilComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (!TryGetUserSkills(ent, out var user) ||
            !_skills.HasSkills((user, user), in ent.Comp.Skills))
        {
            return;
        }

        if (ent.Comp.MustBeWielded && CompOrNull<WieldableComponent>(ent)?.Wielded != true)
            return;

        args.CameraRecoilScalar = 0;
    }

    private void StopProjectile(Entity<ProjectileFixedDistanceComponent> projectile)
    {
        if (!_physicsQuery.TryGetComponent(projectile, out var physics))
            return;

        _physics.SetLinearVelocity(projectile, Vector2.Zero, body: physics);
        _physics.SetBodyStatus(projectile, physics, BodyStatus.OnGround);

        if (physics.Awake)
            _broadphase.RegenerateContacts(projectile, physics);
    }

    private void UpdateDelay(Entity<GunShowUseDelayComponent> ent)
    {
        if (!TryComp(ent, out GunComponent? gun))
            return;

        var remaining = gun.NextFire - _timing.CurTime;
        if (remaining <= TimeSpan.Zero)
            return;

        var useDelay = EnsureComp<UseDelayComponent>(ent);
        _useDelay.SetLength((ent, useDelay), remaining, ent.Comp.DelayId);
        _useDelay.TryResetDelay((ent, useDelay), false, ent.Comp.DelayId);
    }

    private void TryRefreshGunModifiers<TComp, TEvent>(Entity<TComp> ent, ref TEvent args) where TComp : IComponent?
    {
        if (TryComp(ent, out GunComponent? gun))
            _gun.RefreshModifiers((ent, gun));
    }

    private bool TryGetUserSkills(EntityUid gun, out Entity<SkillsComponent> user)
    {
        user = default;
        if (!_container.TryGetContainingContainer((gun, null), out var container) ||
            !HasComp<HandsComponent>(container.Owner) ||
            !TryComp(container.Owner, out SkillsComponent? skills))
        {
            return false;
        }

        user = (container.Owner, skills);
        return true;
    }

    public void RefreshGunDamageMultiplier(Entity<GunDamageModifierComponent?> gun)
    {
        gun.Comp = EnsureComp<GunDamageModifierComponent>(gun);

        var ev = new GetGunDamageModifierEvent(gun.Comp.Multiplier);
        RaiseLocalEvent(gun, ref ev);

        gun.Comp.ModifiedMultiplier = ev.Multiplier;
    }

    public override void Update(float frameTime)
    {
        var time = _timing.CurTime;
        var query = EntityQueryEnumerator<ProjectileFixedDistanceComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (time < comp.FlyEndTime)
                continue;

            RemCompDeferred<ProjectileFixedDistanceComponent>(uid);
        }
    }
}
