using System.Collections.Generic;
using System.Linq;
using Nitrox.Model.DataStructures;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Server.Subnautica.Models.AppEvents;
using Nitrox.Server.Subnautica.Models.AppEvents.Core;
using Nitrox.Server.Subnautica.Models.GameLogic.Unlockables;

namespace Nitrox.Server.Subnautica.Models.GameLogic;

/// <summary>
///     Manager for thread-safe access to <see cref="PdaStateData" />.
/// </summary>
internal sealed class PdaManager(ILogger<PdaManager> logger) : ISummarize
{
    public const int MAX_ENCYCLOPEDIA_KEY_LENGTH = 128;
    public const int MAX_ENCYCLOPEDIA_ENTRIES = 4096;
    public const int MAX_TIME_CAPSULE_CONTENTS = 256;

    private readonly ILogger<PdaManager> logger = logger;

    private readonly Lock pdaStateLock = new();

    /// <summary>
    ///     Any access to this state must use the same thread-safe lock to avoid one list being updated based on the invalid
    ///     state of another.
    /// </summary>
    /// <remarks>
    ///     A loaded world may hold time capsule content that an older build stored unchecked (an image address of another host): it is cleaned here, before it
    ///     reaches the initial sync of anybody.
    /// </remarks>
    public PdaStateData PdaState
    {
        private get;
        set
        {
            lock (pdaStateLock)
            {
                if (value != null)
                {
                    foreach (TimeCapsuleContentData content in value.TimeCapsuleContents)
                    {
                        content?.Sanitize();
                    }
                    if (value.TimeCapsuleContents.Count > MAX_TIME_CAPSULE_CONTENTS)
                    {
                        value.TimeCapsuleContents.RemoveRange(MAX_TIME_CAPSULE_CONTENTS, value.TimeCapsuleContents.Count - MAX_TIME_CAPSULE_CONTENTS);
                    }
                }
                field = value;
            }
        }
    } = new();

    public PdaStateData GetPdaStateCopy()
    {
        lock (pdaStateLock)
        {
            return PdaState.GetFullCopy();
        }
    }

    public void AddKnownTechType(NitroxTechType techType, List<NitroxTechType> partialTechTypesToRemove)
    {
        bool duplicateAttempt = false;
        try
        {
            lock (pdaStateLock)
            {
                PdaState.ScannerPartial.RemoveAllFast(partialTechTypesToRemove, static (entry, list) => list.Contains(entry.TechType));
                if (PdaState.KnownTechTypes.Contains(techType))
                {
                    duplicateAttempt = true;
                    return;
                }
                PdaState.KnownTechTypes.Add(techType);
            }
        }
        finally
        {
            if (duplicateAttempt)
            {
                logger.ZLogDebug($"There was an attempt of adding a duplicated entry in the {nameof(PdaState.KnownTechTypes)}: [{techType.Name}]");
            }
        }
    }

    public void AddAnalyzedTechType(NitroxTechType techType)
    {
        bool duplicateAttempt = false;
        try
        {
            lock (pdaStateLock)
            {
                if (PdaState.AnalyzedTechTypes.Contains(techType))
                {
                    duplicateAttempt = true;
                    return;
                }
                PdaState.AnalyzedTechTypes.Add(techType);
            }
        }
        finally
        {
            if (duplicateAttempt)
            {
                logger.ZLogDebug($"There was an attempt of adding a duplicated entry in the {nameof(PdaState.AnalyzedTechTypes)}: [{techType.Name}]");
            }
        }
    }

    /// <param name="content">The content of a time capsule entry, kept with it when the entry is new. It is cleaned in place (<see cref="TimeCapsuleContentData.Sanitize" />), so the packet that carries it is relayed clean.</param>
    /// <returns>False if the entry was already known or is refused (empty, too long, or the list is full)</returns>
    public bool AddEncyclopediaEntry(string entry, TimeCapsuleContentData content = null)
    {
        bool duplicateAttempt = false;
        try
        {
            lock (pdaStateLock)
            {
                // Every entry goes into the save and into the initial sync of every player who joins: a client must not be able to grow them without end
                if (string.IsNullOrEmpty(entry) || entry.Length > MAX_ENCYCLOPEDIA_KEY_LENGTH || PdaState.EncyclopediaEntries.Count >= MAX_ENCYCLOPEDIA_ENTRIES)
                {
                    logger.ZLogWarning($"Refused the encyclopedia entry [{entry?.Length ?? 0} characters]: empty, longer than {MAX_ENCYCLOPEDIA_KEY_LENGTH} characters, or the {MAX_ENCYCLOPEDIA_ENTRIES} entries are reached");
                    return false;
                }
                if (PdaState.EncyclopediaEntries.Contains(entry))
                {
                    duplicateAttempt = true;
                    return false;
                }
                PdaState.EncyclopediaEntries.Add(entry);
                if (content != null && content.Key == entry)
                {
                    content.Sanitize();
                    if (PdaState.TimeCapsuleContents.Count < MAX_TIME_CAPSULE_CONTENTS)
                    {
                        PdaState.TimeCapsuleContents.Add(content);
                    }
                    else
                    {
                        logger.ZLogWarning($"Did not keep the content of the time capsule [{entry}]: the {MAX_TIME_CAPSULE_CONTENTS} contents are reached");
                    }
                }
                return true;
            }
        }
        finally
        {
            if (duplicateAttempt)
            {
                logger.ZLogDebug($"There was an attempt of adding a duplicated entry in the {nameof(PdaState.EncyclopediaEntries)}: [{entry}]");
            }
        }
    }

    /// <returns>False if the entry was already known</returns>
    public bool AddPDALogEntry(PDALogEntry entry)
    {
        bool duplicateAttempt = false;
        try
        {
            lock (pdaStateLock)
            {
                if (PdaState.PdaLog.Any(logEntry => logEntry.Key == entry.Key))
                {
                    duplicateAttempt = true;
                    return false;
                }
                PdaState.PdaLog.Add(entry);
                return true;
            }
        }
        finally
        {
            if (duplicateAttempt)
            {
                logger.ZLogDebug($"There was an attempt of adding a duplicated entry in the {nameof(PdaState.PdaLog)}: [{entry.Key}]");
            }
        }
    }

    public void AddScannerFragment(NitroxId id)
    {
        lock (pdaStateLock)
        {
            PdaState.ScannerFragments.Add(id);
        }
    }

    public void UpdateEntryUnlockedProgress(NitroxTechType techType, int unlockedAmount, bool fullyResearched)
    {
        lock (pdaStateLock)
        {
            if (fullyResearched)
            {
                PdaState.ScannerPartial.RemoveAllFast(techType, static (entry, toRemove) => entry.TechType.Equals(toRemove));
                PdaState.ScannerComplete.Add(techType);
            }
            else
            {
                if (PdaState.ScannerPartial.FirstOrDefault(e => e.TechType.Equals(techType)) is { } entry)
                {
                    entry.Unlocked = unlockedAmount;
                }
                else
                {
                    PdaState.ScannerPartial.Add(new(techType, unlockedAmount));
                }
            }
        }
    }

    public InitialPDAData GetInitialPDAData()
    {
        lock (pdaStateLock)
        {
            return new(PdaState.KnownTechTypes.ToList(),
                       PdaState.AnalyzedTechTypes.ToList(),
                       PdaState.PdaLog.ToList(),
                       PdaState.EncyclopediaEntries.ToList(),
                       PdaState.ScannerFragments.ToList(),
                       PdaState.ScannerPartial.ToList(),
                       PdaState.ScannerComplete.ToList(),
                       PdaState.TimeCapsuleContents.ToList());
        }
    }

    public void RemovePdaLogsByKey(string eventKey)
    {
        lock (pdaStateLock)
        {
            PdaState.PdaLog.RemoveAllFast(eventKey, static (entry, keyToRemove) => entry.Key == keyToRemove);
        }
    }

    public bool IsKnownTech(NitroxTechType techType)
    {
        lock (pdaStateLock)
        {
            return PdaState.KnownTechTypes.Contains(techType);
        }
    }

    public bool ContainsCompletelyScannedTech(NitroxTechType techType)
    {
        lock (pdaStateLock)
        {
            return PdaState.ScannerComplete.Contains(techType);
        }
    }

    public bool ContainsLog(string pdaEntryId)
    {
        lock (pdaStateLock)
        {
            return PdaState.PdaLog.Any(entry => entry.Key == pdaEntryId);
        }
    }

    Task IEvent<ISummarize.Args>.OnEventAsync(ISummarize.Args args)
    {
        int knownTechCount;
        int encyclopediaEntriesCount;
        lock (pdaStateLock)
        {
            knownTechCount = PdaState.KnownTechTypes.Count;
            encyclopediaEntriesCount = PdaState.EncyclopediaEntries.Count;
        }
        logger.ZLogInformation($"Known tech: {knownTechCount}");
        logger.ZLogInformation($"Encyclopedia entries: {encyclopediaEntriesCount}");

        return Task.CompletedTask;
    }
}
