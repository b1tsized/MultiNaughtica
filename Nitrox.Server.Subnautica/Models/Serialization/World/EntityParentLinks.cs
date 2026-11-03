using System.Collections.Generic;
using Nitrox.Model.DataStructures;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.Entities;

namespace Nitrox.Server.Subnautica.Models.Serialization.World;

/// <summary>
///     Rebuilds the tree of the saved entities from the parent id each one names (the save does not keep the children lists). A link that would close a loop
///     (an entity naming itself, or naming one of its own descendants) is dropped and that entity is loaded without a parent: linked, it made the load throw (or
///     recurse until the stack overflowed) and the server refused to start, so one damaged entity bricked the world and, after a few saves, its backups too.
/// </summary>
internal static class EntityParentLinks
{
    /// <returns>How many parent links were dropped.</returns>
    public static int Link(IReadOnlyCollection<Entity> entities, IReadOnlyDictionary<NitroxId, Entity> entitiesById, ILogger logger)
    {
        int dropped = 0;
        foreach (Entity entity in entities)
        {
            if (entity.ParentId != null && entitiesById.TryGetValue(entity.ParentId, out Entity parentEntity))
            {
                if (ClosesALoop(entity, entitiesById))
                {
                    logger.ZLogWarning($"The saved entity {entity.Id} names {entity.ParentId} as its parent, which is the entity itself or one of its descendants: it is loaded without a parent");
                    entity.ParentId = null;
                    dropped++;
                    continue;
                }
                parentEntity.ChildEntities.Add(entity);

                if (entity is WorldEntity worldEntity && parentEntity is WorldEntity parentWorldEntity)
                {
                    worldEntity.Transform.SetParent(parentWorldEntity.Transform, false);
                }
            }
        }
        return dropped;
    }

    /// <summary>Whether going up the parent ids from <paramref name="entity" /> comes back to it. A loop further up that does not contain it ends the walk.</summary>
    private static bool ClosesALoop(Entity entity, IReadOnlyDictionary<NitroxId, Entity> entitiesById)
    {
        HashSet<NitroxId> visited = [entity.Id];
        NitroxId? current = entity.ParentId;
        while (current != null)
        {
            if (current.Equals(entity.Id))
            {
                return true;
            }
            if (!visited.Add(current) || !entitiesById.TryGetValue(current, out Entity ancestor))
            {
                return false;
            }
            current = ancestor.ParentId;
        }
        return false;
    }
}
