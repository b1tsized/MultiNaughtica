using Microsoft.Extensions.Logging.Abstractions;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.Entities;

namespace Nitrox.Server.Subnautica.Models.GameLogic.Entities;

/// <summary>
///     A client names the parent of an entity (<c>EntityReparented</c>, <c>SubRootChanged</c>, <c>EscapePodChanged</c>, <c>EntitySpawnedByClient</c>). A parent that is
///     the entity itself or one of its own children made a cycle: the transform math (<c>NitroxTransform.LocalToWorldMatrix</c>) or the child walk of
///     <see cref="EntityRegistry.AddOrUpdate" /> then recursed until the stack overflowed, which no catch survives, and the server process died.
/// </summary>
[TestClass]
public class EntityRegistryParentCycleTest
{
    private EntityRegistry registry = null!;
    private PlayerEntity root = null!;

    [TestInitialize]
    public void Init()
    {
        registry = new EntityRegistry(NullLogger<EntityRegistry>.Instance);
        root = Add(null);
    }

    [TestMethod]
    public void ReparentingAnEntityUnderItselfKeepsItsParent()
    {
        PlayerEntity a = Add(root);

        registry.ReparentEntity(a, (Entity)a).Should().BeFalse();

        a.ParentId.Should().Be(root.Id);
        root.ChildEntities.Should().ContainSingle().Which.Should().BeSameAs(a);
        a.ChildEntities.Should().BeEmpty();
    }

    [TestMethod]
    public void ReparentingByIdUnderItselfIsRefused()
    {
        PlayerEntity a = Add(root);

        registry.ReparentEntity(a.Id, a.Id).Should().BeFalse();

        a.ParentId.Should().Be(root.Id);
    }

    [TestMethod]
    public void ReparentingAnEntityUnderItsOwnChildIsRefused()
    {
        PlayerEntity a = Add(root);
        PlayerEntity b = Add(a);

        registry.ReparentEntity(a, (Entity)b).Should().BeFalse();

        a.ParentId.Should().Be(root.Id);
        b.ParentId.Should().Be(a.Id);
        b.ChildEntities.Should().BeEmpty();
    }

    [TestMethod]
    public void ReparentingAnEntityUnderADeeperDescendantIsRefused()
    {
        PlayerEntity a = Add(root);
        PlayerEntity b = Add(a);
        PlayerEntity c = Add(b);

        registry.ReparentEntity(a, (Entity)c).Should().BeFalse();

        a.ParentId.Should().Be(root.Id);
    }

    [TestMethod]
    public void ANormalMoveStillWorks()
    {
        PlayerEntity b = Add(root);
        PlayerEntity c = Add(root);
        PlayerEntity a = Add(b);

        registry.ReparentEntity(a, (Entity)c).Should().BeTrue();

        a.ParentId.Should().Be(c.Id);
        c.ChildEntities.Should().ContainSingle().Which.Should().BeSameAs(a);
        b.ChildEntities.Should().BeEmpty();
    }

    [TestMethod]
    public void MovingASiblingUnderAnotherSiblingStillWorks()
    {
        PlayerEntity a = Add(root);
        PlayerEntity b = Add(root);

        registry.ReparentEntity(b, (Entity)a).Should().BeTrue();

        b.ParentId.Should().Be(a.Id);
    }

    [TestMethod]
    public void MovingToTheCurrentParentStillWorks()
    {
        PlayerEntity a = Add(root);

        registry.ReparentEntity(a, (Entity)root).Should().BeTrue();

        a.ParentId.Should().Be(root.Id);
        root.ChildEntities.Should().ContainSingle().Which.Should().BeSameAs(a);
    }

    [TestMethod]
    public void MovingToNoParentStillWorks()
    {
        PlayerEntity a = Add(root);

        registry.ReparentEntity(a, (Entity?)null).Should().BeTrue();

        a.ParentId.Should().BeNull();
        root.ChildEntities.Should().BeEmpty();
    }

    [TestMethod]
    public void ASpawnUnderOneOfItsOwnChildrenIsAddedWithoutAParent()
    {
        PlayerEntity a = Add(root);
        PlayerEntity b = Add(a);
        PlayerEntity again = New(a.Id, b.Id);

        registry.AddOrUpdate(again);

        again.ParentId.Should().BeNull();
        b.ChildEntities.Should().BeEmpty();
    }

    [TestMethod]
    public void AnUnknownParentIsNoCycle()
    {
        PlayerEntity a = Add(root);

        registry.WouldCreateCycle(a.Id, new NitroxId()).Should().BeFalse();
        registry.WouldCreateCycle(a.Id, null).Should().BeFalse();
        registry.WouldCreateCycle(a.Id, a.Id).Should().BeTrue();
    }

    [TestMethod]
    public void AnEntityThatNamesItselfAsItsParentIsAddedWithoutAParent()
    {
        NitroxId id = new();
        PlayerEntity entity = New(id, id);

        registry.AddOrUpdate(entity);

        entity.ParentId.Should().BeNull();
        entity.ChildEntities.Should().BeEmpty();
        registry.TryGetEntityById(id, out Entity? _).Should().BeTrue();
    }

    private PlayerEntity Add(Entity? parent)
    {
        PlayerEntity entity = New(new NitroxId(), parent?.Id);
        registry.AddOrUpdate(entity);
        return entity;
    }

    private static PlayerEntity New(NitroxId id, NitroxId? parentId)
    {
        return new PlayerEntity(new NitroxTransform(new NitroxVector3(1, 2, 3), NitroxQuaternion.Identity, NitroxVector3.One), 0, "player", true, id, new NitroxTechType("Player"), null, parentId, []);
    }
}
