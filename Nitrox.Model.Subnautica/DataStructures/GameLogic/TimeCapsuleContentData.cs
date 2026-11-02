using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using BinaryPack.Attributes;

namespace Nitrox.Model.Subnautica.DataStructures.GameLogic;

/// <summary>
/// What the encyclopedia entry of a time capsule shows (the content comes from the UWE web service to the client that spawned the capsule, per player): the other players
/// never got it, and after a restart of the game nobody had it, so the entry showed the raw id and an error text.
/// </summary>
[Serializable]
[DataContract]
public class TimeCapsuleContentData
{
    // The service limits what a player can write (title 60, text 1000): more than that is not an honest capsule
    public const int MAX_TITLE_LENGTH = 120;
    public const int MAX_TEXT_LENGTH = 2000;
    public const int MAX_IMAGE_URL_LENGTH = 500;

    [DataMember(Order = 1)]
    public string Key;

    [DataMember(Order = 2)]
    public string Title;

    [DataMember(Order = 3)]
    public string Text;

    /// <summary>
    /// Relative to the public image host (the game builds the full address)
    /// </summary>
    [DataMember(Order = 4)]
    public string ImageUrl;

    [IgnoreConstructor]
    protected TimeCapsuleContentData()
    {
        // Constructor for serialization. Has to be "protected" for json serialization.
    }

    public TimeCapsuleContentData(string key, string title, string text, string imageUrl)
    {
        Key = key;
        Title = title;
        Text = text;
        ImageUrl = imageUrl;
        Sanitize();
    }

    /// <summary>
    ///     True for an address relative to the game's image host (<c>capsule.jpg</c>, <c>images/2017/capsule.jpg</c>). The game hands the address to
    ///     <c>ScreenshotManager</c>, which replaces the image host by any rooted address (<c>//host/a.png</c>, <c>\\host\share\a.png</c>, <c>C:\a.png</c>)
    ///     and then downloads it: another player could make every game that opens the entry connect to a server of their choosing and save its answer.
    ///     <c>\A</c> and <c>\z</c> on purpose: <c>$</c> also matches before a final line break.
    /// </summary>
    public static bool IsSafeImageUrl(string? url)
    {
        return url != null && SafeImageUrl.IsMatch(url) && !url.Contains("..");
    }

    /// <summary>
    ///     Cuts the texts to their limits and drops an image address that <see cref="IsSafeImageUrl" /> refuses (the entry then shows no picture). Content
    ///     that arrives in a packet or from a saved world is built without the constructor, so whoever receives it calls this before using or storing it.
    /// </summary>
    public void Sanitize()
    {
        Title = Truncate(Title, MAX_TITLE_LENGTH);
        Text = Truncate(Text, MAX_TEXT_LENGTH);
        string imageUrl = Truncate(ImageUrl, MAX_IMAGE_URL_LENGTH);
        ImageUrl = IsSafeImageUrl(imageUrl) ? imageUrl : string.Empty;
    }

    private static readonly Regex SafeImageUrl = new(@"\A[A-Za-z0-9_-][A-Za-z0-9_./-]{0,499}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Truncate(string value, int maxLength)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    public override string ToString()
    {
        return $"{nameof(Key)}: {Key}, {nameof(Title)}: {Title}";
    }
}
