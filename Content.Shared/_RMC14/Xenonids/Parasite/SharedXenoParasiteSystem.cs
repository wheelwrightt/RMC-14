using Content.Shared._RMC14.Hands;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Xenonids.Construction.Nest;
using Content.Shared._RMC14.Xenonids.Leap;
using Content.Shared._RMC14.Xenonids.Pheromones;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.DragDrop;
using Content.Shared.Examine;
using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Jittering;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.Popups;
using Content.Shared.Rejuvenate;
using Content.Shared.Rounding;
using Content.Shared.Standing;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Parasite;

public abstract class SharedXenoParasiteSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly BlindableSystem _blindable = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly CMHandsSystem _cmHands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly StatusEffectsSystem _status = default!;
    [Dependency] private readonly SharedRottingSystem _rotting = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<InfectableComponent, ActivateInWorldEvent>(OnInfectableActivate);
        SubscribeLocalEvent<InfectableComponent, CanDropTargetEvent>(OnInfectableCanDropTarget);

        SubscribeLocalEvent<XenoParasiteComponent, XenoLeapHitEvent>(OnParasiteLeapHit);
        SubscribeLocalEvent<XenoParasiteComponent, AfterInteractEvent>(OnParasiteAfterInteract);
        SubscribeLocalEvent<XenoParasiteComponent, BeforeInteractHandEvent>(OnParasiteInteractHand);
        SubscribeLocalEvent<XenoParasiteComponent, DoAfterAttemptEvent<AttachParasiteDoAfterEvent>>(OnParasiteAttachDoAfterAttempt);
        SubscribeLocalEvent<XenoParasiteComponent, AttachParasiteDoAfterEvent>(OnParasiteAttachDoAfter);
        SubscribeLocalEvent<XenoParasiteComponent, CanDragEvent>(OnParasiteCanDrag);
        SubscribeLocalEvent<XenoParasiteComponent, CanDropDraggedEvent>(OnParasiteCanDropDragged);
        SubscribeLocalEvent<XenoParasiteComponent, DragDropDraggedEvent>(OnParasiteDragDropDragged);

        SubscribeLocalEvent<ParasiteSpentComponent, MapInitEvent>(OnParasiteSpentMapInit);
        SubscribeLocalEvent<ParasiteSpentComponent, UpdateMobStateEvent>(OnParasiteSpentUpdateMobState,
            after: [typeof(MobThresholdSystem), typeof(SharedXenoPheromonesSystem)]);

        SubscribeLocalEvent<VictimInfectedComponent, MapInitEvent>(OnVictimInfectedMapInit);
        SubscribeLocalEvent<VictimInfectedComponent, ComponentRemove>(OnVictimInfectedRemoved);
        SubscribeLocalEvent<VictimInfectedComponent, CanSeeAttemptEvent>(OnVictimInfectedCancel);
        SubscribeLocalEvent<VictimInfectedComponent, ExaminedEvent>(OnVictimInfectedExamined);
        SubscribeLocalEvent<VictimInfectedComponent, RejuvenateEvent>(OnVictimInfectedRejuvenate);

        SubscribeLocalEvent<VictimBurstComponent, MapInitEvent>(OnVictimBurstMapInit);
        SubscribeLocalEvent<VictimBurstComponent, UpdateMobStateEvent>(OnVictimUpdateMobState,
            after: [typeof(MobThresholdSystem), typeof(SharedXenoPheromonesSystem)]);
        SubscribeLocalEvent<VictimBurstComponent, RejuvenateEvent>(OnVictimBurstRejuvenate);
        SubscribeLocalEvent<VictimBurstComponent, ExaminedEvent>(OnVictimBurstExamine);
    }

    private void OnInfectableActivate(Entity<InfectableComponent> ent, ref ActivateInWorldEvent args)
    {
        if (TryComp(args.User, out XenoParasiteComponent? parasite) &&
            StartInfect((args.User, parasite), args.Target, args.User))
        {
            args.Handled = true;
        }
    }

    private void OnInfectableCanDropTarget(Entity<InfectableComponent> ent, ref CanDropTargetEvent args)
    {
        if (TryComp(args.Dragged, out XenoParasiteComponent? parasite) &&
            CanInfectPopup((args.Dragged, parasite), ent, args.User, false))
        {
            args.CanDrop = true;
            args.Handled = true;
        }
    }

    private void OnParasiteLeapHit(Entity<XenoParasiteComponent> parasite, ref XenoLeapHitEvent args)
    {
        var coordinates = _transform.GetMoverCoordinates(parasite);
        if (_transform.InRange(coordinates, args.Leaping.Origin, parasite.Comp.InfectRange))
            Infect(parasite, args.Hit, false);
    }

    private void OnParasiteAfterInteract(Entity<XenoParasiteComponent> ent, ref AfterInteractEvent args)
    {
        if (!args.CanReach || args.Target == null)
            return;

        if (StartInfect(ent, args.Target.Value, args.User))
            args.Handled = true;
    }

    private void OnParasiteInteractHand(Entity<XenoParasiteComponent> ent, ref BeforeInteractHandEvent args)
    {
        if (!IsInfectable(ent, args.Target))
            return;

        StartInfect(ent, args.Target, ent);

        args.Handled = true;
    }

    private void OnParasiteAttachDoAfterAttempt(Entity<XenoParasiteComponent> ent, ref DoAfterAttemptEvent<AttachParasiteDoAfterEvent> args)
    {
        if (args.DoAfter.Args.Target is not { } target)
        {
            args.Cancel();
            return;
        }

        if (!CanInfectPopup(ent, target, ent))
            args.Cancel();
    }

    private void OnParasiteAttachDoAfter(Entity<XenoParasiteComponent> ent, ref AttachParasiteDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Target == null)
            return;

        if (Infect(ent, args.Target.Value))
            args.Handled = true;
    }

    private void OnParasiteCanDrag(Entity<XenoParasiteComponent> ent, ref CanDragEvent args)
    {
        args.Handled = true;
    }

    private void OnParasiteCanDropDragged(Entity<XenoParasiteComponent> ent, ref CanDropDraggedEvent args)
    {
        if (args.User != ent.Owner && !_cmHands.IsPickupByAllowed(ent.Owner, args.User))
            return;

        if (!CanInfectPopup(ent, args.Target, args.User, false))
            return;

        args.CanDrop = true;
        args.Handled = true;
    }

    private void OnParasiteDragDropDragged(Entity<XenoParasiteComponent> ent, ref DragDropDraggedEvent args)
    {
        if (args.User != ent.Owner && !_cmHands.IsPickupByAllowed(ent.Owner, args.User))
            return;

        StartInfect(ent, args.Target, args.User);
        args.Handled = true;
    }

    protected virtual void ParasiteLeapHit(Entity<XenoParasiteComponent> parasite)
    {
    }

    private void OnParasiteSpentMapInit(Entity<ParasiteSpentComponent> spent, ref MapInitEvent args)
    {
        if (TryComp(spent, out MobStateComponent? mobState))
            _mobState.UpdateMobState(spent, mobState);
    }

    private void OnParasiteSpentUpdateMobState(Entity<ParasiteSpentComponent> spent, ref UpdateMobStateEvent args)
    {
        args.State = MobState.Dead;
    }

    private void OnVictimInfectedMapInit(Entity<VictimInfectedComponent> victim, ref MapInitEvent args)
    {
        victim.Comp.FallOffAt = _timing.CurTime + victim.Comp.FallOffDelay;
        victim.Comp.BurstAt = _timing.CurTime + victim.Comp.BurstDelay;

        _appearance.SetData(victim, victim.Comp.InfectedLayer, true);
    }

    private void OnVictimInfectedRemoved(Entity<VictimInfectedComponent> victim, ref ComponentRemove args)
    {
        _blindable.UpdateIsBlind(victim.Owner);
        _standing.Stand(victim);
    }

    private void OnVictimInfectedCancel<T>(Entity<VictimInfectedComponent> victim, ref T args) where T : CancellableEntityEventArgs
    {
        if (victim.Comp.LifeStage <= ComponentLifeStage.Running && !victim.Comp.Recovered)
            args.Cancel();
    }

    private void OnVictimInfectedExamined(Entity<VictimInfectedComponent> victim, ref ExaminedEvent args)
    {
        if (HasComp<XenoComponent>(args.Examiner) || (CompOrNull<GhostComponent>(args.Examiner)?.CanGhostInteract ?? false))
            args.PushMarkup("This creature is impregnated.");
    }

    private void OnVictimInfectedRejuvenate(Entity<VictimInfectedComponent> victim, ref RejuvenateEvent args)
    {
        RemCompDeferred<VictimInfectedComponent>(victim);
    }

    private void OnVictimBurstMapInit(Entity<VictimBurstComponent> burst, ref MapInitEvent args)
    {
        _appearance.SetData(burst, burst.Comp.BurstLayer, true);

        if (TryComp(burst, out MobStateComponent? mobState))
            _mobState.UpdateMobState(burst, mobState);
    }

    private void OnVictimUpdateMobState(Entity<VictimBurstComponent> burst, ref UpdateMobStateEvent args)
    {
        args.State = MobState.Dead;
    }

    private void OnVictimBurstRejuvenate(Entity<VictimBurstComponent> burst, ref RejuvenateEvent args)
    {
        RemCompDeferred<VictimBurstComponent>(burst);
    }

    private void OnVictimBurstExamine(Entity<VictimBurstComponent> burst, ref ExaminedEvent args)
    {
        using(args.PushGroup(nameof(VictimBurstComponent)))
            args.PushMarkup($"[color=red][bold]{Loc.GetString("rmc-xeno-infected-bursted", ("victim", burst))}[/bold][/color]");
    }

    private bool StartInfect(Entity<XenoParasiteComponent> parasite, EntityUid victim, EntityUid user)
    {
        if (!CanInfectPopup(parasite, victim, user))
            return false;

        var ev = new AttachParasiteDoAfterEvent();
        var doAfter = new DoAfterArgs(EntityManager, user, parasite.Comp.ManualAttachDelay, ev, parasite, victim)
        {
            BreakOnMove = true,
            AttemptFrequency = AttemptFrequency.EveryTick
        };
        _doAfter.TryStartDoAfter(doAfter);

        return true;
    }

    private bool IsInfectable(EntityUid parasite, EntityUid victim)
    {
        return HasComp<InfectableComponent>(victim)
               && !HasComp<ParasiteSpentComponent>(parasite)
               && !HasComp<VictimInfectedComponent>(victim);
    }

    private bool CanInfectPopup(Entity<XenoParasiteComponent> parasite, EntityUid victim, EntityUid user, bool popup = true, bool force = false)
    {
        if (!IsInfectable(parasite, victim))
        {
            if (popup)
                _popup.PopupClient(Loc.GetString("rmc-xeno-failed-cant-infect", ("target", victim)), victim, user, PopupType.MediumCaution);

            return false;
        }

        if (!force
            && !HasComp<XenoNestedComponent>(victim)
            && TryComp(victim, out StandingStateComponent? standing)
            && !_standing.IsDown(victim, standing))
        {
            if (popup)
                _popup.PopupClient(Loc.GetString("rmc-xeno-failed-cant-reach", ("target", victim)), victim, user, PopupType.MediumCaution);

            return false;
        }

        if (_mobState.IsDead(victim))
        {
            if (popup)
                _popup.PopupClient(Loc.GetString("rmc-xeno-failed-target-dead"), victim, user, PopupType.MediumCaution);

            return false;
        }

        return true;
    }

    public bool Infect(Entity<XenoParasiteComponent> parasite, EntityUid victim, bool popup = true, bool force = false)
    {
        if (!CanInfectPopup(parasite, victim, parasite, popup, force))
            return false;

        if (_inventory.TryGetContainerSlotEnumerator(victim, out var slots, SlotFlags.MASK))
        {
            var any = false;
            while (slots.MoveNext(out var slot))
            {
                if (slot.ContainedEntity != null)
                {
                    _inventory.TryUnequip(victim, victim, slot.ID, force: true);
                    any = true;
                }
            }

            if (any && _net.IsServer)
            {
                _popup.PopupEntity(Loc.GetString("rmc-xeno-infect-success", ("target", victim)), victim);
            }
        }

        if (_net.IsServer &&
            TryComp(victim, out InfectableComponent? infectable) &&
            TryComp(victim, out HumanoidAppearanceComponent? appearance) &&
            infectable.Sound.TryGetValue(appearance.Sex, out var sound))
        {
            var filter = Filter.Pvs(victim);
            _audio.PlayEntity(sound, filter, victim, true);
        }

        var time = _timing.CurTime;
        var victimComp = EnsureComp<VictimInfectedComponent>(victim);
        victimComp.AttachedAt = time;
        victimComp.RecoverAt = time + parasite.Comp.ParalyzeTime;
        victimComp.Hive = CompOrNull<XenoComponent>(parasite)?.Hive ?? default;
        _stun.TryParalyze(victim, parasite.Comp.ParalyzeTime, true);
        _status.TryAddStatusEffect(victim, "Muted", parasite.Comp.ParalyzeTime, true, "Muted");
        RefreshIncubationMultipliers(victim);

        var container = _container.EnsureContainer<ContainerSlot>(victim, victimComp.ContainerId);
        _container.Insert(parasite.Owner, container);

        _blindable.UpdateIsBlind(victim);
        _appearance.SetData(parasite, victimComp.InfectedLayer, true);

        // TODO RMC14 also do damage to the parasite
        EnsureComp<ParasiteSpentComponent>(parasite);

        ParasiteLeapHit(parasite);
        return true;
    }

    public void RefreshIncubationMultipliers(Entity<VictimInfectedComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        var ev = new GetInfectedIncubationMultiplierEvent(ent.Comp.CurrentStage);
        RaiseLocalEvent(ent, ref ev);

        var multiplier = 1f;

        foreach (var add in ev.Additions)
        {
            multiplier += add;
        }

        foreach (var multi in ev.Multipliers)
        {
            multiplier *= multi;
        }

        ent.Comp.IncubationMultiplier = multiplier;
    }

    public override void Update(float frameTime)
    {
        if (_net.IsClient)
            return;

        var time = _timing.CurTime;
        var query = EntityQueryEnumerator<VictimInfectedComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var infected, out var xform))
        {
            if (infected.FallOffAt < time && !infected.FellOff)
            {
                infected.FellOff = true;
                _appearance.SetData(uid, infected.InfectedLayer, false);
                if (_container.TryGetContainer(uid, infected.ContainerId, out var container))
                    _container.EmptyContainer(container);
            }

            if (infected.RecoverAt < time && !infected.Recovered)
            {
                infected.Recovered = true;
                _blindable.UpdateIsBlind(uid);
            }

            if (_net.IsClient)
                continue;

            if (infected.BurstAt > time)
            {
                // Embryo dies if unrevivable when dead
                // Kill the embryo if we've rotted or are a simplemob
                if (_mobState.IsDead(uid) && (HasComp<InfectStopOnDeathComponent>(uid) || _rotting.IsRotten(uid)))
                {
                    RemCompDeferred<VictimInfectedComponent>(uid);
                    continue;
                }
                // Stasis slows this, while nesting makes it happen sooner
                if (infected.IncubationMultiplier != 1)
                    infected.BurstAt += TimeSpan.FromSeconds(1 - infected.IncubationMultiplier) * frameTime;

                // Stages
                // Percentage of how far along we out to burst time times the number of stages, truncated. You can't go back a stage once you've reached one
                int stage = Math.Max((int) ((infected.BurstDelay - (infected.BurstAt - time)) / infected.BurstDelay * infected.FinalStage), infected.CurrentStage);
                if (stage != infected.CurrentStage)
                {
                    infected.CurrentStage = stage;
                    Dirty(uid, infected);
                    // Refresh multipliers since some become more/less effective
                    RefreshIncubationMultipliers(uid);
                }

                // Warn on the last to final stage of a burst
                if (!infected.DidBurstWarning && stage == infected.FinalStage - 1)
                {
                    _popup.PopupEntity(Loc.GetString("rmc-xeno-infection-burst-soon-self"), uid, uid, PopupType.MediumCaution);
                    _popup.PopupEntity(Loc.GetString("rmc-xeno-infection-burst-soon", ("victim", uid)), uid, Filter.PvsExcept(uid), true, PopupType.MediumCaution);
                    _jitter.DoJitter(uid, infected.JitterTime * 6, false);
                    infected.DidBurstWarning = true;
                    continue;
                }
                // Symptoms only start after the IntialSymptomStart is passed (by default, 2)
                // And continue until burst time is reached
                // TODO after burst time is reached, the larva is made and stage set to 6, have wait time for someone to take the larva.
                // During this stage the victim should be in intense pain, and auto-burst after some time
                if (stage >= infected.FinalSymptomsStart)
                {
                    if (_random.Prob(infected.MajorPainChance * frameTime))
                    {
                        var message = Loc.GetString("rmc-xeno-infection-majorpain-" + _random.Pick(new List<string> { "chest", "breathing", "heart" }));
                        _popup.PopupEntity(message, uid, uid, PopupType.SmallCaution);
                        if (_random.Prob(0.5f))
                        {
                            var ev = new VictimInfectedEmoteEvent(infected.ScreamId);
                            RaiseLocalEvent(uid, ref ev);
                        }
                    }

                    if (_random.Prob(infected.ShakesChance * frameTime))
                        InfectionShakes(uid, infected, infected.BaseKnockdownTime * 4, infected.JitterTime * 4);
                }
                else if (stage >= infected.MiddlingSymptomsStart)
                {
                    if (_random.Prob(infected.ThroatPainChance * frameTime))
                    {
                        var message = Loc.GetString("rmc-xeno-infection-throat-" + _random.Pick(new List<string> { "sore", "mucous" }));
                        _popup.PopupEntity(message, uid, uid, PopupType.SmallCaution);
                    }
                    // TODO 20% chance to take limb damage
                    else if (_random.Prob(infected.MuscleAcheChance * frameTime))
                    {
                        _popup.PopupEntity(Loc.GetString("rmc-xeno-infection-muscle-ache"), uid, uid, PopupType.SmallCaution);
                        if (_random.Prob(0.2f))
                            _damage.TryChangeDamage(uid, infected.InfectionDamage, true, false);
                    }
                    else if (_random.Prob(infected.SneezeCoughChance * frameTime))
                    {
                        var emote = _random.Pick(new List<ProtoId<EmotePrototype>> { infected.SneezeId, infected.CoughId });
                        var ev = new VictimInfectedEmoteEvent(emote);
                        RaiseLocalEvent(uid, ref ev);
                    }

                    if (_random.Prob((infected.ShakesChance * 5 / 6) * frameTime))
                        InfectionShakes(uid, infected, infected.BaseKnockdownTime * 2, infected.JitterTime * 2);
                }
                else if (stage >= infected.InitialSymptomsStart)
                {
                    if (_random.Prob(infected.MinorPainChance * frameTime))
                    {
                        var message = Loc.GetString("rmc-xeno-infection-minorpain-" + _random.Pick(new List<string> { "stomach", "chest" }));
                        _popup.PopupEntity(message, uid, uid, PopupType.SmallCaution);
                    }

                    if (_random.Prob((infected.ShakesChance * 2 / 3) * frameTime))
                        InfectionShakes(uid, infected, infected.BaseKnockdownTime, infected.JitterTime);
                }
                continue;
            }

            RemCompDeferred<VictimInfectedComponent>(uid);

            var spawned = SpawnAtPosition(infected.BurstSpawn, xform.Coordinates);
            infected.CurrentStage = 6;
            Dirty(uid, infected);

            _xeno.SetHive(spawned, infected.Hive);

            EnsureComp<VictimBurstComponent>(uid);

            _audio.PlayPvs(infected.BurstSound, uid);
        }
    }
    // Shakes chances decrease as symptom stages progress, and they get longer
    private void InfectionShakes(EntityUid victim, VictimInfectedComponent infected, TimeSpan knockdownTime, TimeSpan jitterTime)
    {
        // Don't activate when unconscious
        if (_mobState.IsIncapacitated(victim))
            return;
        //TODO Minor limb damage and causes pain
        _stun.TryParalyze(victim, knockdownTime, false);
        _jitter.DoJitter(victim, jitterTime, false);
        _popup.PopupEntity(Loc.GetString("rmc-xeno-infection-shakes-self"), victim, victim, PopupType.MediumCaution);
        _popup.PopupEntity(Loc.GetString("rmc-xeno-infection-shakes", ("victim", victim)), victim, Filter.PvsExcept(victim), true, PopupType.MediumCaution);
        _damage.TryChangeDamage(victim, infected.InfectionDamage, true, false);
    }
}
