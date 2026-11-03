using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nitrox.Model.DataStructures;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.Entities;

namespace Nitrox.Server.Subnautica.Models.GameLogic.Entities
{
    public class EntityRegistry(ILogger<EntityRegistry> logger)
    {
        private readonly ILogger<EntityRegistry> logger = logger;
        private readonly ConcurrentDictionary<NitroxId, Entity> entitiesById = new();

        public Optional<T> GetEntityById<T>(NitroxId id) where T : Entity
        {
            TryGetEntityById(id, out T entity);

            return Optional.OfNullable(entity);
        }

        public Optional<Entity> GetEntityById(NitroxId id)
        {
            return GetEntityById<Entity>(id);
        }

        public bool TryGetEntityById<T>(NitroxId id, [NotNullWhen(true)] out T? entity) where T : Entity
        {
            if (entitiesById.TryGetValue(id, out Entity e) && e is T typedEntity)
            {
                entity = typedEntity;
                return true;
            }
            entity = null;
            return false;
        }

        public List<Entity> GetAllEntities(bool exceptGlobalRoot = false)
        {
            if (exceptGlobalRoot)
            {
                return new(entitiesById.Values.Where(entity => entity is not GlobalRootEntity));
            }
            return new List<Entity>(entitiesById.Values);            
        }

        public List<Entity> GetEntities(List<NitroxId> ids)
        {
            return entitiesById.Join(ids,
                                        entity => entity.Value.Id,
                                        id => id,
                                        (entity, id) => entity.Value)
                                .ToList();
        }

        public List<T> GetEntities<T>()
        {
            return entitiesById.Values.OfType<T>().ToList();
        }

        public void AddEntity(Entity entity)
        {
            if (!entitiesById.TryAdd(entity.Id, entity))
            {
                // Log an error to show stack trace but don't halt execution.
                logger.ZLogError(new InvalidOperationException(), $"Trying to add duplicate entity {entity.Id}");
            }
        }

        /// <summary>
        /// Registers or updates an entity and its children.
        /// </summary>
        public void AddOrUpdate(Entity entity)
        {
            if (!entitiesById.TryAdd(entity.Id, entity))
            {
                Entity current = entitiesById[entity.Id];

                RemoveFromParent(current);

                entitiesById.TryUpdate(entity.Id, entity, current);
            }

            AddToParent(entity);
            AddEntitiesIgnoringDuplicate(entity.ChildEntities);
        }

        public void AddEntities(IEnumerable<Entity> entities)
        {
            foreach(Entity entity in entities)
            {
                AddEntity(entity);
            }
        }

        /// <summary>
        /// Used for situations when some children may be new but others may not be. For
        /// example a dropped InventoryEntity turns into a WorldEntity but keeps its 
        /// battery inside (already known). 
        /// </summary>
        /// <remarks>
        /// Updates entities if they already exist. Callers that rely on that: <see cref="AddOrUpdate" /> (an entity that is sent again with new children) and the loading of the save
        /// (the children that are in the entity data AND in the global root data are registered as the objects of the global root tree, which their parents hold).
        /// A freshly generated batch must not use this: use <see cref="AddEntitiesKeepingExisting" />.
        /// </remarks>
        public void AddEntitiesIgnoringDuplicate(IEnumerable<Entity> entities)
        {
            foreach (Entity entity in entities)
            {
                if (entitiesById.TryGetValue(entity.Id, out Entity currentEntity))
                {
                    entitiesById.TryUpdate(entity.Id, entity, currentEntity);
                }
                else
                {
                    entitiesById.TryAdd(entity.Id, entity);
                }
                AddEntitiesIgnoringDuplicate(entity.ChildEntities);
            }
        }

        /// <summary>
        /// Registers the entities of a freshly generated batch and never replaces what the registry already holds: the registered entity keeps its state (a cut door, a looted crate,
        /// the time a crash fish came out), the generated one is dropped together with everything generated below it.
        /// </summary>
        /// <remarks>
        /// <see cref="AddEntitiesIgnoringDuplicate" /> replaced instead. A batch that was generated a second time (after the record of generated batches was lost) came out with ids that were
        /// one off for everything after a skipped databox: the replacement reset the state of the entities that kept their id and registered the others as second copies under the ids
        /// of other entities. The list may hold the same object more than once (cell root children are listed twice) and may hold children as well as their parents.
        /// </remarks>
        /// <param name="entities">The generated entities, parents and children in any order.</param>
        /// <param name="ignored">Receives the entities that were dropped (the colliding ones and their descendants in the list).</param>
        /// <returns>The number of dropped entities.</returns>
        public int AddEntitiesKeepingExisting(IReadOnlyCollection<Entity> entities, ICollection<Entity>? ignored = null)
        {
            HashSet<Entity> dropped = new(ReferenceEqualityComparer.Instance);
            HashSet<NitroxId> droppedIds = [];

            // An entity collides when its id belongs to another object (the same object listed twice is not a collision)
            foreach (Entity entity in entities)
            {
                if (entitiesById.TryGetValue(entity.Id, out Entity registered) && !ReferenceEquals(registered, entity) && dropped.Add(entity))
                {
                    droppedIds.Add(entity.Id);
                }
            }

            // What was generated below a dropped entity goes with it, whatever its own id
            bool grew = droppedIds.Count > 0;
            while (grew)
            {
                grew = false;
                foreach (Entity entity in entities)
                {
                    if (entity.ParentId != null && droppedIds.Contains(entity.ParentId) && dropped.Add(entity))
                    {
                        droppedIds.Add(entity.Id);
                        grew = true;
                    }
                }
            }

            foreach (Entity entity in entities)
            {
                if (dropped.Contains(entity))
                {
                    continue;
                }

                if (!IsRegisteredAs(entity))
                {
                    // Registered by someone else between the check and the add
                    dropped.Add(entity);
                    continue;
                }
                AddMissingChildren(entity, dropped);
            }

            if (ignored != null)
            {
                foreach (Entity entity in dropped)
                {
                    ignored.Add(entity);
                }
            }
            return dropped.Count;
        }

        /// <summary>
        /// The children that hang below an entity but were not in the list (a list is normally complete): registered when their id is free, dropped (and taken out of the parent's list) when it is not.
        /// </summary>
        private void AddMissingChildren(Entity parent, HashSet<Entity> dropped)
        {
            foreach (Entity child in parent.ChildEntities)
            {
                if (dropped.Contains(child))
                {
                    continue;
                }

                if (IsRegisteredAs(child))
                {
                    AddMissingChildren(child, dropped);
                }
                else
                {
                    dropped.Add(child);
                }
            }

            // A kept entity must not list a child that was dropped: it would be sent to the clients under the id of another entity
            parent.ChildEntities.RemoveAll(child => dropped.Contains(child));
        }

        /// <returns>True when this very object is registered under its id afterwards (it is added when the id is free)</returns>
        private bool IsRegisteredAs(Entity entity)
        {
            return entitiesById.TryAdd(entity.Id, entity) || (entitiesById.TryGetValue(entity.Id, out Entity registered) && ReferenceEquals(registered, entity));
        }

        /// <param name="callerMember">Filled in by the compiler: the removal is remembered with the method that asked for it (see <see cref="TryGetRemoval" />).</param>
        /// <param name="callerFile">Filled in by the compiler.</param>
        public Optional<Entity> RemoveEntity(NitroxId id, [CallerMemberName] string? callerMember = null, [CallerFilePath] string? callerFile = null)
        {
            return RemoveEntityCore(id, DescribeCaller(callerMember, callerFile));
        }

        private Optional<Entity> RemoveEntityCore(NitroxId id, string remover)
        {
            if (entitiesById.TryRemove(id, out Entity entity))
            {
                if (CrashHomeStates.IsHome(entity))
                {
                    // A sulfur plant is never taken out of the world (its fish die, the plant stays): when one leaves, the line says which code asked for it. Rare, so it is not a risk for the log
                    remover = DescribeCallers(new StackTrace(false), 8);
                    logger.ZLogInformation($"[CrashHomes] The CrashHome {id} left the registry with {entity.ChildEntities.Count} children, taken out by {remover}");
                }

                RememberRemoval(id, remover);
                RemoveFromParent(entity);

                foreach (Entity child in entity.ChildEntities)
                {
                    RemoveEntityCore(child.Id, $"removed with its parent {id}");
                }
            }

            return Optional.OfNullable(entity);
        }

        /// <summary>
        /// The ids that left the registry, for diagnostics only: a packet about an id the server does not know can say whether the id was known once, when it left and which code took it out
        /// (2026-10-23: the update of a crash home that the server did not know, one packet in five hours; nothing in the log said whether the server had ever held the id, and a memory of two
        /// minutes could not have said so either). The memory is bounded by the number of removals and not by time, so a removal is still on record hours later; the oldest are forgotten
        /// first, and the answer says when something was forgotten, so that "no record" means "never removed" only while nothing was.
        /// </summary>
        internal const int MAX_REMEMBERED_REMOVALS = 16384;

        private readonly record struct RemovalNote(long Tick, long Sequence, string Remover);

        private readonly Dictionary<NitroxId, RemovalNote> removalNotes = [];
        private readonly Queue<(NitroxId Id, long Sequence, long Tick)> removalOrder = new();
        private readonly object removalLock = new();
        private long removalSequence;
        private long forgottenRemovals;

        /// <summary>
        /// The clock of the removal memory in milliseconds; the tests replace it.
        /// </summary>
        internal Func<long> TickSource { get; set; } = static () => Environment.TickCount64;

        private void RememberRemoval(NitroxId id, string remover)
        {
            lock (removalLock)
            {
                long now = TickSource();
                long sequence = ++removalSequence;
                removalNotes[id] = new RemovalNote(now, sequence, remover);
                removalOrder.Enqueue((id, sequence, now));

                while (removalOrder.Count > MAX_REMEMBERED_REMOVALS)
                {
                    (NitroxId Id, long Sequence, long Tick) oldest = removalOrder.Dequeue();
                    // The id may have been removed again since: its newer note stays
                    if (removalNotes.TryGetValue(oldest.Id, out RemovalNote note) && note.Sequence == oldest.Sequence)
                    {
                        removalNotes.Remove(oldest.Id);
                        forgottenRemovals++;
                    }
                }
            }
        }

        /// <returns>True if the id left the registry and the memory still holds it; <paramref name="ageMs" /> is how long ago and <paramref name="remover" /> which code took it out.</returns>
        public bool TryGetRemoval(NitroxId id, out long ageMs, out string remover)
        {
            lock (removalLock)
            {
                if (removalNotes.TryGetValue(id, out RemovalNote note))
                {
                    ageMs = TickSource() - note.Tick;
                    remover = note.Remover;
                    return true;
                }
            }

            ageMs = 0;
            remover = "";
            return false;
        }

        /// <returns>True if the id left the registry and the memory still holds it; <paramref name="ageMs" /> is how long ago.</returns>
        public bool TryGetRemovalAge(NitroxId id, out long ageMs) => TryGetRemoval(id, out ageMs, out string _);

        /// <summary>
        /// What the removal memory knows about an id that a packet names and the registry does not hold, as the end of a log line.
        /// </summary>
        public string DescribeUnknownId(NitroxId id)
        {
            lock (removalLock)
            {
                long now = TickSource();
                bool removed = removalNotes.TryGetValue(id, out RemovalNote note);
                long oldestAgeMs = removalOrder.Count > 0 ? now - removalOrder.Peek().Tick : 0;
                return DescribeRemoval(removed, removed ? now - note.Tick : 0, removed ? note.Remover : "", removalOrder.Count, oldestAgeMs, forgottenRemovals);
            }
        }

        internal static string DescribeRemoval(bool removed, long ageMs, string remover, int remembered, long oldestAgeMs, long forgotten)
        {
            if (removed)
            {
                return string.IsNullOrEmpty(remover) ? $"it left the registry {FormatAge(ageMs)} ago" : $"it left the registry {FormatAge(ageMs)} ago, taken out by {remover}";
            }

            if (forgotten == 0)
            {
                return $"no removal of it is on record since this server started (all {remembered} removals of this run are on record): the registry has not held it in this run";
            }

            return $"no removal of it is on record (the record holds the last {remembered} removals, back to {FormatAge(oldestAgeMs)} ago, and {forgotten} older ones are forgotten): it left the registry longer ago than that, or the registry never held it";
        }

        internal static string FormatAge(long ageMs)
        {
            return ageMs switch
            {
                < 1000 => $"{ageMs} ms",
                < 60000 => $"{(ageMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)} s",
                < 3600000 => $"{(ageMs / 60000.0).ToString("F1", CultureInfo.InvariantCulture)} min",
                _ => $"{(ageMs / 3600000.0).ToString("F1", CultureInfo.InvariantCulture)} h"
            };
        }

        /// <returns>The file and the method of a call, from the values the compiler filled in.</returns>
        internal static string DescribeCaller(string? member, string? file)
        {
            string fileName = "";
            if (!string.IsNullOrEmpty(file))
            {
                fileName = file.Substring(Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\')) + 1);
                if (fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = fileName.Substring(0, fileName.Length - 3);
                }
            }

            if (fileName.Length == 0 && string.IsNullOrEmpty(member))
            {
                return "unknown code";
            }

            return fileName.Length == 0 ? member! : $"{fileName}.{member}";
        }

        /// <summary>
        /// The methods that led to a call, nearest first, as one text: without the frames of the registry and of the runtime, with the real name of an async method or a lambda instead of the
        /// compiler's state machine or closure.
        /// </summary>
        internal static string DescribeCallers(StackTrace trace, int maxFrames)
        {
            List<string> names = [];
            try
            {
                foreach (StackFrame frame in trace.GetFrames())
                {
                    MethodBase? method = frame.GetMethod();
                    Type? type = method?.DeclaringType;
                    if (method == null || type == null || type == typeof(EntityRegistry) || type.DeclaringType == typeof(EntityRegistry))
                    {
                        continue;
                    }

                    string? space = type.Namespace;
                    if (space != null && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal) || space.StartsWith("Microsoft.", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    string typeName = type.Name;
                    string methodName = method.Name;
                    if (typeName.StartsWith('<') && type.DeclaringType != null)
                    {
                        // <Process>d__3 (the state machine of Process) or <>c (the closures of a type): the method is named inside the type's name
                        int end = typeName.IndexOf('>');
                        if (end > 1)
                        {
                            methodName = typeName.Substring(1, end - 1);
                        }

                        typeName = type.DeclaringType.Name;
                    }

                    names.Add($"{typeName}.{methodName}");
                    if (names.Count >= maxFrames)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                names.Add("(stack not readable)");
            }

            return names.Count == 0 ? "(no frames)" : string.Join(" <- ", names);
        }

        public void AddToParent(Entity entity)
        {
            if (entity.ParentId != null)
            {
                // A client names the parent of what it spawns: under itself or under one of its own children, the child walk of AddOrUpdate would never end
                if (WouldCreateCycle(entity.Id, entity.ParentId))
                {
                    logger.ZLogWarning($"Refused to add {entity.Id} under {entity.ParentId}, which is the entity itself or one of its children: it is added without a parent");
                    entity.ParentId = null;
                    return;
                }
                Optional<Entity> parent = GetEntityById(entity.ParentId);

                if (parent.HasValue)
                {
                    parent.Value.ChildEntities.Add(entity);
                }
            }
        }

        public void RemoveFromParent(Entity? entity)
        {
            if (entity == null)
            {
                return;
            }

            if (entity.ParentId != null && TryGetEntityById(entity.ParentId, out Entity parentEntity))
            {
                parentEntity.ChildEntities.RemoveAll(childEntity => childEntity.Id.Equals(entity.Id));
                entity.ParentId = null;
                if (entity is WorldEntity worldEntity && worldEntity.Transform != null)
                {
                    worldEntity.Transform.SetParent(null, true);
                }
            }
        }


        /// <summary>
        /// Removes all children from <paramref name="entity"/>
        /// </summary>
        public void CleanChildren(Entity entity)
        {
            for (int i = entity.ChildEntities.Count - 1; i >= 0; i--)
            {
                RemoveEntity(entity.ChildEntities[i].Id);
            }
        }

        /// <returns>False when the entity is unknown or the move is refused (see <see cref="ReparentEntity(Entity?, Entity?)" />)</returns>
        public bool ReparentEntity(NitroxId? entityId, NitroxId? newParentId)
        {
            if (entityId == null || !TryGetEntityById(entityId, out Entity entity))
            {
                logger.ZLogError($"Could not find entity to reparent: {entityId}");
                return false;
            }
            return ReparentEntity(entity, newParentId);
        }

        /// <returns>False when the entity is unknown or the move is refused (see <see cref="ReparentEntity(Entity?, Entity?)" />)</returns>
        public bool ReparentEntity(NitroxId entityId, Entity newParent)
        {
            if (entityId == null || !TryGetEntityById(entityId, out Entity entity))
            {
                logger.ZLogError($"Could not find entity to reparent: {entityId}");
                return false;
            }
            return ReparentEntity(entity, newParent);
        }

        /// <returns>False when the move is refused (see <see cref="ReparentEntity(Entity?, Entity?)" />)</returns>
        public bool ReparentEntity(Entity? entity, NitroxId? newParentId)
        {
            Entity parentEntity = newParentId != null ? GetEntityById(newParentId).Value : null;
            return ReparentEntity(entity, parentEntity);
        }

        /// <summary>
        ///     Moves <paramref name="entity" /> under <paramref name="newParent" /> (or to no parent). A parent that is the entity itself or one of its own children is
        ///     refused and the entity stays where it was: such a cycle made every position read of the transform recurse until the stack overflowed, which ends the
        ///     server process, and a client names the parent in several packets.
        /// </summary>
        /// <returns>False when the move is refused.</returns>
        public bool ReparentEntity(Entity? entity, Entity? newParent)
        {
            if (entity == null)
            {
                return false;
            }
            if (newParent != null && (newParent.Id.Equals(entity.Id) || WouldCreateCycle(entity.Id, newParent.ParentId)))
            {
                logger.ZLogWarning($"Refused to move {entity.Id} under {newParent.Id}, which is the entity itself or one of its children");
                return false;
            }
            RemoveFromParent(entity);
            if (newParent == null)
            {
                return true;
            }
            if (entity is WorldEntity worldEntity && worldEntity.Transform != null &&
                newParent is WorldEntity parentWorldEntity && parentWorldEntity.Transform != null)
            {
                worldEntity.Transform.SetParent(parentWorldEntity.Transform, true);
            }
            entity.ParentId = newParent.Id;
            newParent.ChildEntities.Add(entity);
            return true;
        }

        /// <summary>
        ///     Whether putting the entity <paramref name="entityId" /> under <paramref name="newParentId" /> would make it its own ancestor: the parent is the entity
        ///     itself, or the entity is found going up from the parent. A parent chain that already loops without the entity (a damaged save) ends the walk: the
        ///     entity is not part of that loop, so the move makes no new one.
        /// </summary>
        public bool WouldCreateCycle(NitroxId entityId, NitroxId? newParentId)
        {
            if (newParentId == null)
            {
                return false;
            }
            if (newParentId.Equals(entityId))
            {
                return true;
            }
            HashSet<NitroxId> visited = [newParentId];
            NitroxId current = newParentId;
            while (TryGetEntityById(current, out Entity ancestor) && ancestor.ParentId != null)
            {
                if (ancestor.ParentId.Equals(entityId))
                {
                    return true;
                }
                if (!visited.Add(ancestor.ParentId))
                {
                    return false;
                }
                current = ancestor.ParentId;
            }
            return false;
        }

        public void TransferChildren(NitroxId parentId, NitroxId newParentId, Func<Entity, bool> filter = null)
        {
            if (!TryGetEntityById(parentId, out Entity parentEntity))
            {
                logger.ZLogError($"[{nameof(TransferChildren)}] Couldn't find origin parent entity for {parentId}");
                return;
            }
            if (!TryGetEntityById(newParentId, out Entity newParentEntity))
            {
                logger.ZLogError($"[{nameof(TransferChildren)}] Couldn't find new parent entity for {newParentId}");
                return;
            }
            TransferChildren(parentEntity, newParentEntity, filter);
        }

        public void TransferChildren(Entity parent, Entity newParent, Func<Entity, bool> filter = null)
        {
            // A client names both ids (UpdateBase.ChildrenTransfer): a child that is the new parent, or one of its ancestors, stays where it is. Moved under
            // itself it would hold itself as a child, and the save, the initial sync and every later walk of the tree would loop
            List<Entity> childrenToMove = [];
            int refused = 0;
            foreach (Entity child in parent.ChildEntities)
            {
                if (filter != null && !filter(child))
                {
                    continue;
                }
                if (WouldCreateCycle(child.Id, newParent.Id))
                {
                    refused++;
                    continue;
                }
                childrenToMove.Add(child);
            }
            if (refused > 0)
            {
                logger.ZLogWarning($"Did not move {refused} children of {parent.Id} under {newParent.Id}, which is one of them or one of their descendants");
            }

            // In case parent == newParent (which is actually a case used) we need removal to happen before adding the entities back
            HashSet<Entity> moving = new(childrenToMove, ReferenceEqualityComparer.Instance);
            parent.ChildEntities.RemoveAll(moving.Contains);

            foreach (Entity childEntity in childrenToMove)
            {
                childEntity.ParentId = newParent.Id;
                newParent.ChildEntities.Add(childEntity);
            }
        }
    }
}
