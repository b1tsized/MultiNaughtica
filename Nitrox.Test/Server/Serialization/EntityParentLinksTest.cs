using Microsoft.Extensions.Logging.Abstractions;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.GameLogic;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.Entities;

namespace Nitrox.Server.Subnautica.Models.Serialization.World;

/// <summary>
///     Loading a world rebuilds the tree of entities from the parent id that each saved entity names. A saved entity whose parent link loops (itself, or two that
///     name each other) made the load fail, and the server refused to start: one damaged entity bricked the world and, after a few saves, every backup too. The
///     load drops the link that closes the loop and keeps the rest.
/// </summary>
[TestClass]
public class EntityParentLinksTest
{
    [TestMethod]
    public void ATreeIsLinked()
    {
        PlayerEntity parent = New(new NitroxId(), null);
        PlayerEntity child = New(new NitroxId(), parent.Id);

        int dropped = Link(parent, child);

        dropped.Should().Be(0);
        parent.ChildEntities.Should().ContainSingle().Which.Should().BeSameAs(child);
        child.Transform.Parent.Should().BeSameAs(parent.Transform);
    }

    [TestMethod]
    public void AnEntityThatNamesItselfAsItsParentIsLoadedWithoutAParent()
    {
        NitroxId id = new();
        PlayerEntity entity = New(id, id);

        int dropped = Link(entity);

        dropped.Should().Be(1);
        entity.ParentId.Should().BeNull();
        entity.ChildEntities.Should().BeEmpty();
        entity.Transform.Parent.Should().BeNull();
    }

    [TestMethod]
    public void TwoEntitiesThatNameEachOtherAreLoadedWithOneLinkDropped()
    {
        PlayerEntity a = New(new NitroxId(), null);
        PlayerEntity b = New(new NitroxId(), a.Id);
        a.ParentId = b.Id;

        int dropped = Link(a, b);

        dropped.Should().Be(1);
        a.ParentId.Should().BeNull("the first entity whose link closes the loop loses it");
        b.ParentId.Should().Be(a.Id);
        a.ChildEntities.Should().ContainSingle().Which.Should().BeSameAs(b);
        b.ChildEntities.Should().BeEmpty();
    }

    private static int Link(params Entity[] entities) => EntityParentLinks.Link(entities, entities.ToDictionary(entity => entity.Id), NullLogger.Instance);

    private static PlayerEntity New(NitroxId id, NitroxId? parentId)
    {
        return new PlayerEntity(new NitroxTransform(new NitroxVector3(1, 2, 3), NitroxQuaternion.Identity, NitroxVector3.One), 0, "player", true, id, new NitroxTechType("Player"), null, parentId, []);
    }
}
