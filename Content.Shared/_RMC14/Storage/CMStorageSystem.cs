﻿using Content.Shared._RMC14.Marines.Skills;
using Content.Shared.DoAfter;
using Content.Shared.Item;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Whitelist;
using Robust.Shared.Timing;
using static Content.Shared.Storage.StorageComponent;

namespace Content.Shared._RMC14.Storage;

public sealed class CMStorageSystem : EntitySystem
{
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedItemSystem _item = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SkillsSystem _skills = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private readonly List<EntityUid> _toRemove = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<StorageFillComponent, CMStorageItemFillEvent>(OnStorageFillItem);

        SubscribeLocalEvent<StorageOpenDoAfterComponent, OpenStorageDoAfterEvent>(OnStorageOpenDoAfter);

        SubscribeLocalEvent<StorageSkillRequiredComponent, StorageInteractAttemptEvent>(OnStorageSkillOpenAttempt);
        SubscribeLocalEvent<StorageSkillRequiredComponent, DumpableDoAfterEvent>(OnDumpableDoAfter, before: [typeof(DumpableSystem)]);

        Subs.BuiEvents<StorageCloseOnMoveComponent>(StorageUiKey.Key, sub =>
        {
            sub.Event<BoundUIOpenedEvent>(OnCloseOnMoveUIOpened);
        });

        Subs.BuiEvents<StorageOpenComponent>(StorageUiKey.Key, sub =>
        {
            sub.Event<BoundUIClosedEvent>(OnCloseOnMoveUIClosed);
        });

        UpdatesAfter.Add(typeof(SharedStorageSystem));
    }

    private void OnDumpableDoAfter(Entity<StorageSkillRequiredComponent> ent, ref DumpableDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        if (TryCancel(args.User, ent))
            args.Handled = true;
    }

    private void OnStorageFillItem(Entity<StorageFillComponent> storage, ref CMStorageItemFillEvent args)
    {
        var tries = 0;
        while (!_storage.CanInsert(storage, args.Item, out var reason) &&
               reason == "comp-storage-insufficient-capacity" &&
               tries < 3)
        {
            tries++;

            // TODO RMC14 make this error if this is a cm-specific storage
            Log.Warning($"Storage {ToPrettyString(storage)} can't fit {ToPrettyString(args.Item)}");

            var modified = false;
            foreach (var shape in _item.GetItemShape((storage, args.Storage), (args.Item, args.Item)))
            {
                var grid = args.Storage.Grid;
                if (grid.Count == 0)
                {
                    grid.Add(shape);
                    continue;
                }

                // TODO RMC14 this might create more space than is necessary to fit the item if there is some free space left in the storage before expanding it
                var last = grid[^1];
                var expanded = new Box2i(last.Left, last.Bottom, last.Right + shape.Width + 1, last.Top);

                if (expanded.Top < shape.Top)
                    expanded.Top = shape.Top;

                grid[^1] = expanded;
                modified = true;
            }

            if (modified)
                Dirty(storage);
        }
    }

    public bool IgnoreItemSize(Entity<StorageComponent> storage, EntityUid item)
    {
        return TryComp(storage, out IgnoreContentsSizeComponent? ignore) &&
               _whitelist.IsValid(ignore.Items, item);
    }

    public bool OpenDoAfter(EntityUid uid, EntityUid entity, StorageComponent? storageComp = null, bool silent = false)
    {
        if (!TryComp(uid, out StorageOpenDoAfterComponent? comp) ||
            comp.Duration == TimeSpan.Zero)
        {
            return false;
        }

        var ev = new OpenStorageDoAfterEvent(GetNetEntity(uid), GetNetEntity(entity), silent);
        var doAfter = new DoAfterArgs(EntityManager, entity, comp.Duration, ev, uid)
        {
            BreakOnMove = true
        };
        _doAfter.TryStartDoAfter(doAfter);

        return true;
    }

    private void OnStorageOpenDoAfter(Entity<StorageOpenDoAfterComponent> ent, ref OpenStorageDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (!TryGetEntity(args.Uid, out var uid) ||
            !TryGetEntity(args.Entity, out var entity))
        {
            return;
        }

        if (!TryComp(uid, out StorageComponent? storage))
            return;

        args.Handled = true;
        _storage.OpenStorageUI(uid.Value, entity.Value, storage, args.Silent, false);
    }

    private void OnStorageSkillOpenAttempt(Entity<StorageSkillRequiredComponent> ent, ref StorageInteractAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (TryCancel(args.User, ent))
            args.Cancelled = true;
    }

    private void OnCloseOnMoveUIOpened(Entity<StorageCloseOnMoveComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        var user = args.Actor;
        var coordinates = GetNetCoordinates(_transform.GetMoverCoordinates(user));
        EnsureComp<StorageOpenComponent>(ent).OpenedAt[user] = coordinates;
    }

    private void OnCloseOnMoveUIClosed(Entity<StorageOpenComponent> ent, ref BoundUIClosedEvent args)
    {
        ent.Comp.OpenedAt.Remove(args.Actor);
    }

    private bool TryCancel(EntityUid user, Entity<StorageSkillRequiredComponent> storage)
    {
        if (!_skills.HasSkills(user, in storage.Comp.Skills))
        {
            _popup.PopupClient(Loc.GetString("cm-storage-unskilled"), storage, user, PopupType.SmallCaution);
            return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        var removeOnlyQuery = EntityQueryEnumerator<RemoveOnlyStorageComponent>();
        while (removeOnlyQuery.MoveNext(out var uid, out _))
        {
            if (TryComp(uid, out StorageComponent? storage))
            {
                storage.Whitelist = new EntityWhitelist();
                Dirty(uid, storage);
            }

            RemCompDeferred<RemoveOnlyStorageComponent>(uid);
        }

        var openQuery = EntityQueryEnumerator<StorageOpenComponent>();
        while (openQuery.MoveNext(out var uid, out var open))
        {
            _toRemove.Clear();
            foreach (var (user, netOrigin) in open.OpenedAt)
            {
                if (TerminatingOrDeleted(user))
                {
                    _toRemove.Add(user);
                    continue;
                }

                var origin = GetCoordinates(netOrigin);
                var current = _transform.GetMoverCoordinates(user);

                if (!_transform.InRange(origin, current, 0.1f))
                    _toRemove.Add(user);
            }

            foreach (var user in _toRemove)
            {
                _ui.CloseUi(uid, StorageUiKey.Key, user);
                open.OpenedAt.Remove(user);
            }

            if (open.OpenedAt.Count == 0)
                RemCompDeferred<StorageOpenComponent>(uid);
        }
    }
}
