using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Server.Subnautica.Models.GameLogic.Entities;
using Nitrox.Server.Subnautica.Models.Packets.Core;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

internal sealed class EntityReparentedProcessor(EntityRegistry entityRegistry, ContainerCapacityArbiter containerCapacityArbiter, ILogger<EntityReparentedProcessor> logger) : IAuthPacketProcessor<EntityReparented>
{
    private readonly EntityRegistry entityRegistry = entityRegistry;
    private readonly ContainerCapacityArbiter containerCapacityArbiter = containerCapacityArbiter;
    private readonly ILogger<EntityReparentedProcessor> logger = logger;

    public async Task Process(AuthProcessorContext context, EntityReparented packet)
    {
        if (!entityRegistry.TryGetEntityById(packet.Id, out Entity entity))
        {
            logger.ZLogError($"Couldn't find entity for {packet.Id}");
            return;
        }
        if (!entityRegistry.TryGetEntityById(packet.NewParentId, out Entity parentEntity))
        {
            logger.ZLogError($"Couldn't find parent entity for {packet.NewParentId}");
            return;
        }

        // Another player may have filled the container since the sender last looked: the first move that fits wins, the later one goes back
        if (entity.ParentId != null && !entity.ParentId.Equals(packet.NewParentId) &&
            !containerCapacityArbiter.Fits(parentEntity, entity, packet.ItemWidth, packet.ItemHeight, packet.ContainerWidth, packet.ContainerHeight))
        {
            logger.ZLogWarning($"{context.Sender.Name} moved {entity.TechType} {packet.Id} into the full container {packet.NewParentId}, sending it back to {entity.ParentId}");
            await context.ReplyAsync(new EntityReparented(packet.Id, entity.ParentId));
            return;
        }

        // Refused (the new parent is the entity itself or one of its children): the other players must not get it either
        if (!entityRegistry.ReparentEntity(packet.Id, packet.NewParentId))
        {
            logger.ZLogWarning($"{context.Sender.Name} tried to move {packet.Id} under {packet.NewParentId}, which is the entity itself or one of its children: not done and not relayed");
            return;
        }
        await context.SendToOthersAsync(packet);
    }
}
