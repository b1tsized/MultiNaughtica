using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Server.Subnautica.Models.GameLogic.Unlockables;
using NSubstitute;

namespace Nitrox.Server.Subnautica.Models.GameLogic;

[TestClass]
public class PdaManagerTimeCapsuleTest
{
    private static PdaManager NewManager() => new(Substitute.For<ILogger<PdaManager>>());

    private static TimeCapsuleContentData Content(string key, string title = "Hello", string text = "Greetings from the other side") => new(key, title, text, "images/capsule.jpg");

    [TestMethod]
    public void ANewTimeCapsuleEntryKeepsItsContent()
    {
        PdaManager manager = NewManager();

        manager.AddEncyclopediaEntry("capsule-1", Content("capsule-1")).Should().BeTrue();

        InitialPDAData data = manager.GetInitialPDAData();
        data.EncyclopediaEntries.Should().Contain("capsule-1");
        data.TimeCapsuleContents.Should().ContainSingle().Which.Title.Should().Be("Hello");
    }

    [TestMethod]
    public void AnEntryThatWasKnownKeepsTheContentItHad()
    {
        PdaManager manager = NewManager();
        manager.AddEncyclopediaEntry("capsule-1", Content("capsule-1", title: "First"));

        manager.AddEncyclopediaEntry("capsule-1", Content("capsule-1", title: "Second")).Should().BeFalse();

        manager.GetInitialPDAData().TimeCapsuleContents.Should().ContainSingle().Which.Title.Should().Be("First");
    }

    [TestMethod]
    public void AnEntryWithoutContentStoresNone()
    {
        PdaManager manager = NewManager();

        manager.AddEncyclopediaEntry("Lifepod");

        manager.GetInitialPDAData().TimeCapsuleContents.Should().BeEmpty();
    }

    [TestMethod]
    public void ContentForAnotherKeyIsNotStored()
    {
        PdaManager manager = NewManager();

        manager.AddEncyclopediaEntry("capsule-1", Content("capsule-2"));

        manager.GetInitialPDAData().TimeCapsuleContents.Should().BeEmpty();
    }

    [TestMethod]
    public void TheStateCopyHasTheContentToo()
    {
        PdaManager manager = NewManager();
        manager.AddEncyclopediaEntry("capsule-1", Content("capsule-1"));

        PdaStateData copy = manager.GetPdaStateCopy();

        copy.TimeCapsuleContents.Should().ContainSingle();
    }

    [TestMethod]
    public void TooLongTextIsCutBeforeItIsStored()
    {
        TimeCapsuleContentData content = new("capsule-1", new string('t', 1000), new string('x', 10000), new string('u', 5000));

        content.Title.Length.Should().Be(TimeCapsuleContentData.MAX_TITLE_LENGTH);
        content.Text.Length.Should().Be(TimeCapsuleContentData.MAX_TEXT_LENGTH);
        content.ImageUrl.Length.Should().Be(TimeCapsuleContentData.MAX_IMAGE_URL_LENGTH);
    }

    [TestMethod]
    public void MissingTextBecomesEmptyText()
    {
        new TimeCapsuleContentData("capsule-1", null, null, null).Title.Should().BeEmpty();
    }

    [TestMethod]
    public void AnUnsafeImageUrlIsNotStoredOrRelayed()
    {
        PdaManager manager = NewManager();
        TimeCapsuleContentData content = Wire("capsule-1", "//evil.example/a.png");

        manager.AddEncyclopediaEntry("capsule-1", content).Should().BeTrue();

        manager.GetInitialPDAData().TimeCapsuleContents.Should().ContainSingle().Which.ImageUrl.Should().BeEmpty();
        // The processor relays the packet that holds this very object
        content.ImageUrl.Should().BeEmpty();
    }

    [TestMethod]
    public void AnOverlongKeyIsRefused()
    {
        PdaManager manager = NewManager();
        string key = new('k', 129);

        manager.AddEncyclopediaEntry(key).Should().BeFalse();

        manager.GetInitialPDAData().EncyclopediaEntries.Should().NotContain(key);
    }

    [TestMethod]
    public void AKeyOfTheLongestAllowedLengthIsKept()
    {
        PdaManager manager = NewManager();

        manager.AddEncyclopediaEntry(new string('k', 128)).Should().BeTrue();
    }

    [TestMethod]
    public void AnEmptyKeyIsRefused()
    {
        PdaManager manager = NewManager();

        manager.AddEncyclopediaEntry("").Should().BeFalse();
        manager.AddEncyclopediaEntry(null).Should().BeFalse();

        manager.GetInitialPDAData().EncyclopediaEntries.Should().BeEmpty();
    }

    [TestMethod]
    public void EntriesStopAtTheCap()
    {
        PdaManager manager = NewManager();
        for (int i = 0; i < 4096; i++)
        {
            manager.AddEncyclopediaEntry($"entry-{i}").Should().BeTrue();
        }

        manager.AddEncyclopediaEntry("entry-4096").Should().BeFalse();

        manager.GetInitialPDAData().EncyclopediaEntries.Should().HaveCount(4096);
    }

    [TestMethod]
    public void TimeCapsuleContentsStopAtTheCap()
    {
        PdaManager manager = NewManager();
        for (int i = 0; i < 256; i++)
        {
            manager.AddEncyclopediaEntry($"capsule-{i}", Content($"capsule-{i}")).Should().BeTrue();
        }

        manager.AddEncyclopediaEntry("capsule-256", Content("capsule-256")).Should().BeTrue();

        InitialPDAData data = manager.GetInitialPDAData();
        data.EncyclopediaEntries.Should().Contain("capsule-256");
        data.TimeCapsuleContents.Should().HaveCount(256);
    }

    [TestMethod]
    public void ALoadedStateIsCleaned()
    {
        PdaManager manager = NewManager();
        PdaStateData state = new();
        state.EncyclopediaEntries.Add("c1");
        state.TimeCapsuleContents.Add(Wire("c1", "//evil.example/a.png"));

        manager.PdaState = state;

        manager.GetInitialPDAData().TimeCapsuleContents.Should().ContainSingle().Which.ImageUrl.Should().BeEmpty();
    }

    [TestMethod]
    public void ALoadedStateKeepsAtMostTheCapOfContents()
    {
        PdaManager manager = NewManager();
        PdaStateData state = new();
        for (int i = 0; i < 300; i++)
        {
            state.EncyclopediaEntries.Add($"c{i}");
            state.TimeCapsuleContents.Add(Wire($"c{i}", "capsule.jpg"));
        }

        manager.PdaState = state;

        manager.GetInitialPDAData().TimeCapsuleContents.Should().HaveCount(256);
    }

    /// <summary>Content as it arrives in a packet or from a save: built without the constructor, so nothing was checked yet.</summary>
    private static TimeCapsuleContentData Wire(string key, string imageUrl)
    {
        TimeCapsuleContentData content = (TimeCapsuleContentData)RuntimeHelpers.GetUninitializedObject(typeof(TimeCapsuleContentData));
        content.Key = key;
        content.Title = "Hello";
        content.Text = "Greetings";
        content.ImageUrl = imageUrl;
        return content;
    }
}
