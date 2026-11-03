using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.Packets;
using NitroxClient.Communication.Abstract;
using NitroxClient.Communication.Packets.Processors.Core;
using NitroxClient.GameLogic;

namespace NitroxClient.Communication.Packets.Processors;

internal sealed class PDAEncyclopediaEntryAddProcessor : IClientPacketProcessor<PDAEncyclopediaEntryAdd>
{
    public Task Process(ClientProcessorContext context, PDAEncyclopediaEntryAdd packet)
    {
        using (PacketSuppressor<PDAEncyclopediaEntryAdd>.Suppress())
        {
            AddEntry(packet.Key, packet.Verbose, packet.Content);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The 2-argument PDAEncyclopedia.Add needs entry data for the key, which a time capsule's entry doesn't have: vanilla creates it in AddTimeCapsule
    /// (an Entry carrying the time capsule id) when the capsule is collected, and only the key travels. Without this, the other player never got the entry (ST1j).
    /// </summary>
    public static void AddEntry(string key, bool verbose, TimeCapsuleContentData content = null)
    {
        // The content first: the label of the entry and the popup read it when the entry is added
        SetTimeCapsuleContent(content);

        // A key that is known here is not added again: no second popup, no badge timer set back to 3 s under a player who is reading the entry (vanilla refuses the add itself, this
        // makes the receiver idempotent by its own rule; a notification is one per entry, see PdaBadgeRules). The restore at join (not verbose) says nothing about a known key.
        PdaBadgeRules.RemoteAddAction action = PdaBadgeRules.DecideRemoteAdd(PDAEncyclopedia.ContainsEntry(key), verbose);
        if (action == PdaBadgeRules.RemoteAddAction.Ignore)
        {
            if (verbose)
            {
                Log.Debug($"[PDA] the encyclopedia entry {key} was reported again by another player, it is known here already: ignored");
            }
            return;
        }

        bool withBadge = action == PdaBadgeRules.RemoteAddAction.AddWithBadge;
        if (withBadge)
        {
            Log.Info($"[PDA] another player unlocked the encyclopedia entry {key}: it gets one popup and one badge here");
        }

        if (PDAEncyclopedia.HasEntryData(key))
        {
            PDAEncyclopedia.Add(key, withBadge);
        }
        else
        {
            PDAEncyclopedia.AddTimeCapsule(key, withBadge);
        }
    }

    /// <summary>
    /// Makes the text and picture of a time capsule known here, unless this client has the content of that capsule already (the one that opened it, with its items)
    /// </summary>
    public static void SetTimeCapsuleContent(TimeCapsuleContentData content)
    {
        if (content == null || string.IsNullOrEmpty(content.Key) || TimeCapsuleContentProvider.GetData(content.Key, out _, out _, out _))
        {
            return;
        }

        // The content comes from another player (or a server of another build): the game downloads whatever image address it is given, so only a relative
        // address on the game's own image host may reach it
        content.Sanitize();
        TimeCapsuleContentProvider.Set(content.Key, new TimeCapsuleContent
        {
            title = content.Title,
            text = content.Text,
            imageUrl = content.ImageUrl,
            isActive = true,
            items = null
        });
    }
}
