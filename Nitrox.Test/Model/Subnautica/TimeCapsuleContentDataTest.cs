using System.Runtime.CompilerServices;

namespace Nitrox.Model.Subnautica.DataStructures.GameLogic;

/// <summary>
///     The image address of a time capsule comes from another player (relayed and saved by the server). The game fetches whatever address it gets
///     (<c>ScreenshotManager</c>: a rooted address replaces the image host), so only a relative address on the game's own image host may pass, and the
///     content that arrives over the wire, which is built without the constructor, must be cleaned too.
/// </summary>
[TestClass]
public class TimeCapsuleContentDataTest
{
    [TestMethod]
    [DataRow("//attacker.example/a.png")]
    [DataRow(@"\\host\share\a.png")]
    [DataRow("http://x/a.png")]
    [DataRow("https://x/a.png")]
    [DataRow("file:///C:/a.png")]
    [DataRow(@"C:\a.png")]
    [DataRow("/a.png")]
    [DataRow("../a.png")]
    [DataRow("a/../../b.png")]
    [DataRow("%2F%2Fhost/a.png")]
    [DataRow("a b.png")]
    [DataRow("a.png\n")]
    [DataRow("")]
    public void UnsafeImageUrlsAreRefused(string url)
    {
        TimeCapsuleContentData.IsSafeImageUrl(url).Should().BeFalse();
    }

    [TestMethod]
    public void AMissingImageUrlIsRefused()
    {
        TimeCapsuleContentData.IsSafeImageUrl(null).Should().BeFalse();
    }

    [TestMethod]
    public void AnOverlongImageUrlIsRefused()
    {
        TimeCapsuleContentData.IsSafeImageUrl(new string('a', 501)).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("capsule.jpg")]
    [DataRow("images/capsule.jpg")]
    [DataRow("2017-05/abc_def.jpeg")]
    [DataRow("ABC123.PNG")]
    public void SafeImageUrlsAreKept(string url)
    {
        TimeCapsuleContentData.IsSafeImageUrl(url).Should().BeTrue();
    }

    [TestMethod]
    public void SanitizeEmptiesAnUnsafeImageUrlAndCutsTheText()
    {
        TimeCapsuleContentData content = FromTheWire();
        content.Key = "c1";
        content.Title = new string('t', 1000);
        content.Text = new string('x', 10000);
        content.ImageUrl = "//evil.example/a.png";

        content.Sanitize();

        content.ImageUrl.Should().BeEmpty();
        content.Title.Should().HaveLength(120);
        content.Text.Should().HaveLength(2000);
    }

    [TestMethod]
    public void SanitizeKeepsASafeImageUrl()
    {
        TimeCapsuleContentData content = FromTheWire();
        content.ImageUrl = "images/capsule.jpg";

        content.Sanitize();

        content.ImageUrl.Should().Be("images/capsule.jpg");
    }

    [TestMethod]
    public void SanitizeMakesMissingTextEmpty()
    {
        TimeCapsuleContentData content = FromTheWire();

        content.Sanitize();

        content.Title.Should().BeEmpty();
        content.Text.Should().BeEmpty();
        content.ImageUrl.Should().BeEmpty();
    }

    [TestMethod]
    public void TheConstructorRefusesAnUnsafeImageUrl()
    {
        new TimeCapsuleContentData("c1", "Title", "Text", "//evil.example/a.png").ImageUrl.Should().BeEmpty();
    }

    /// <summary>What a packet carries: an instance whose fields were set without the constructor running.</summary>
    private static TimeCapsuleContentData FromTheWire() => (TimeCapsuleContentData)RuntimeHelpers.GetUninitializedObject(typeof(TimeCapsuleContentData));
}
