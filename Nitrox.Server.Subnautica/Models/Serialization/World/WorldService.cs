using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.Serialization;
using Nitrox.Model.Server;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.Entities;
using Nitrox.Server.Subnautica.Models.GameLogic;
using Nitrox.Server.Subnautica.Models.GameLogic.Entities;
using Nitrox.Server.Subnautica.Models.GameLogic.Entities.Spawning;
using Nitrox.Server.Subnautica.Models.GameLogic.Players;
using Nitrox.Server.Subnautica.Models.GameLogic.Unlockables;
using Nitrox.Server.Subnautica.Models.Serialization.SaveDataUpgrades;
using Nitrox.Server.Subnautica.Services;
using Nitrox.Server.Subnautica.Services.Core;

namespace Nitrox.Server.Subnautica.Models.Serialization.World;

internal class WorldService : IHostedService
{
    private readonly BatchEntitySpawner batchEntitySpawner;
    private readonly EntityRegistry entityRegistry;
    private readonly EscapePodManager escapePodManager;
    private readonly ILogger<WorldService> logger;
    private readonly IOptions<SubnauticaServerOptions> options;
    private readonly PdaManager pdaManager;
    private readonly PlayerManager playerManager;

    private readonly TaskCompletionSource<bool> hasFinishedLoadingTcs = new();

    /// <summary>
    ///     <see cref="WorldData.RepairVersion" /> of the loaded world, written back by every save.
    /// </summary>
    private int repairVersion;

    private readonly SaveService saveService;
    private readonly IProgressReporter progressReporter;
    private readonly IOptions<ServerStartOptions> startOptions;
    private readonly StoryManager storyManager;
    private readonly StoryScheduler storyScheduler;
    private readonly TimeService timeService;
    private readonly SaveDataUpgrade[] upgrades;
    private readonly WorldEntityManager worldEntityManager;
    public IServerSerializer Serializer { get; private set; }
    private string FileEnding => Serializer?.FileEnding ?? "";

    public WorldService(
        ServerJsonSerializer jsonSerializer,
        IOptions<SubnauticaServerOptions> options,
        IEnumerable<SaveDataUpgrade> upgrades,
        BatchEntitySpawner batchEntitySpawner,
        EntityRegistry entityRegistry,
        PlayerManager playerManager,
        StoryScheduler storyScheduler,
        StoryManager storyManager,
        WorldEntityManager worldEntityManager,
        TimeService timeService,
        PdaManager pdaManager,
        EscapePodManager escapePodManager,
        SaveService saveService,
        IProgressReporter progressReporter,
        IOptions<ServerStartOptions> startOptions,
        ILogger<WorldService> logger)
    {
        this.options = options;
        this.upgrades = upgrades.ToArray();
        this.batchEntitySpawner = batchEntitySpawner;
        this.entityRegistry = entityRegistry;
        this.playerManager = playerManager;
        this.storyScheduler = storyScheduler;
        this.storyManager = storyManager;
        this.worldEntityManager = worldEntityManager;
        this.timeService = timeService;
        this.pdaManager = pdaManager;
        this.escapePodManager = escapePodManager;
        this.saveService = saveService;
        this.progressReporter = progressReporter;
        this.startOptions = startOptions;
        this.logger = logger;

        Serializer = jsonSerializer;
    }

    public async Task<bool> SaveAsync(string saveDir, CancellationToken cancellationToken)
    {
        if (startOptions.Value.HealDryRun)
        {
            // The dry run of the world repair promises that the world folder is not touched
            logger.ZLogWarningOnce($"World repair dry run: the world is not saved while this server runs.");
            return false;
        }

        // Save data is collected from services so they must have finished loading at least once.
        try
        {
            await hasFinishedLoadingTcs.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        PersistedWorldData persistedWorld = new()
        {
            WorldData = new()
            {
                ParsedBatchCells = batchEntitySpawner.SerializableParsedBatches,
                GameData = GameData.From(pdaManager, storyManager.StoryGoalData, storyScheduler, storyManager, timeService),
                RepairVersion = repairVersion
            },
            PlayerData = PlayerData.From(playerManager.GetAllPlayers()),
            GlobalRootData = GlobalRootData.From(worldEntityManager.GetPersistentGlobalRootEntities()),
            EntityData = EntityData.From(entityRegistry.GetAllEntities(true))
        };
        return Save(persistedWorld, saveDir);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await progressReporter.ReportProgressAsync("Loading world data", 0.1f);

        await BackUpBeforeLoadAsync(startOptions.Value.GetServerSavePath());

        if (!await LoadWorldFromSavePathAsync(startOptions.Value.GetServerSavePath()))
        {
            await CreateAndLoadWorldAsync();
        }
        
        await CreateFullEntityCacheIfRequested();
        return;

        async Task CreateFullEntityCacheIfRequested()
        {
            if (!options.Value.CreateFullEntityCache)
            {
                // No entity cache - server is ready after world data loads
                return;
            }

            try
            {
                logger.ZLogInformation($"Starting to load all batches up front.");
                logger.ZLogInformation($"This can take up to several minutes and you can't join until it's completed.");
                logger.ZLogInformation($"{entityRegistry.GetAllEntities().Count} entities already cached");
                if (entityRegistry.GetAllEntities().Count < 504732)
                {
                    await worldEntityManager.LoadAllUnspawnedEntitiesAsync(cancellationToken);
                }
                logger.ZLogInformation($"All batches have now been loaded.");
            }
            catch (OperationCanceledException ex)
            {
                logger.ZLogWarning($"Server start was cancelled by user:{Environment.NewLine}{ex.Message}");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Completes when the saved world (players, entities, story) has been loaded. The services start concurrently, so a client can connect while the world is still loading (a first start with
    ///     a world repair takes 15 s): its reservation must not be made against an empty list of saved players.
    /// </summary>
    internal Task WaitForWorldLoadedAsync(CancellationToken cancellationToken) => hasFinishedLoadingTcs.Task.WaitAsync(cancellationToken);

    internal void UpdateSerializer(IServerSerializer serverSerializer)
    {
        Validate.NotNull(serverSerializer, "Serializer cannot be null");
        Serializer = serverSerializer;
    }

    internal bool Save(PersistedWorldData persistedData, string saveDir)
    {
        try
        {
            Directory.CreateDirectory(saveDir);

            Serializer.Serialize(Path.Combine(saveDir, $"Version{FileEnding}"), new SaveFileVersion());
            Serializer.Serialize(Path.Combine(saveDir, $"PlayerData{FileEnding}"), persistedData.PlayerData);
            Serializer.Serialize(Path.Combine(saveDir, $"WorldData{FileEnding}"), persistedData.WorldData);
            Serializer.Serialize(Path.Combine(saveDir, $"GlobalRootData{FileEnding}"), persistedData.GlobalRootData);
            Serializer.Serialize(Path.Combine(saveDir, $"EntityData{FileEnding}"), persistedData.EntityData);

            logger.ZLogInformation($"World state saved");
            return true;
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Could not save world :");
            return false;
        }
    }

    internal PersistedWorldData? LoadPersistedWorld(string saveDir)
    {
        try
        {
            PersistedWorldData persistedData = new()
            {
                PlayerData = Serializer.Deserialize<PlayerData>(Path.Combine(saveDir, $"PlayerData{FileEnding}")),
                WorldData = Serializer.Deserialize<WorldData>(Path.Combine(saveDir, $"WorldData{FileEnding}")),
                GlobalRootData = Serializer.Deserialize<GlobalRootData>(Path.Combine(saveDir, $"GlobalRootData{FileEnding}")),
                EntityData = Serializer.Deserialize<EntityData>(Path.Combine(saveDir, $"EntityData{FileEnding}"))
            };
            EntityDataPostProcessing(persistedData.EntityData);
            GlobalRootDataPostProcessing(persistedData.GlobalRootData);

            if (!persistedData.IsValid())
            {
                throw new InvalidDataException("Save files are not valid");
            }

            return persistedData;
        }
        catch (Exception ex)
        {
            // Check if the world was newly created using the world manager
            if (new FileInfo(Path.Combine(saveDir, $"Version{FileEnding}")).Length > 0)
            {
                // Give error saying that world could not be used, and to restore a backup
                logger.ZLogError($"Could not load world, please restore one of your backups to continue using this world. : {ex.GetType()} {ex.Message}");

                throw;
            }
        }

        return null;

        void EntityDataPostProcessing(EntityData entityData)
        {
            // After deserialization, we want to assign all of the
            // children to their respective parent entities.
            Dictionary<NitroxId, Entity> entitiesById = entityData.Entities.ToDictionary(entity => entity.Id);

            bool brokenEntitiesFound = false;

            foreach (Entity entity in entityData.Entities)
            {
                if (entity is WorldEntity we)
                {
                    NitroxVector3 pos = we.Transform.LocalPosition;
                    if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z) ||
                        float.IsInfinity(pos.X) || float.IsInfinity(pos.Y) || float.IsInfinity(pos.Z))
                    {
                        logger.ZLogError($"Found {nameof(WorldEntity)} with NaN or infinite position. Teleporting it to world origin.");
                        we.Transform.LocalPosition = NitroxVector3.Zero;
                    }

                    NitroxQuaternion rot = we.Transform.LocalRotation;
                    if (float.IsNaN(rot.X) || float.IsNaN(rot.Y) || float.IsNaN(rot.Z) || float.IsNaN(rot.W) ||
                        float.IsInfinity(rot.X) || float.IsInfinity(rot.Y) || float.IsInfinity(rot.Z) || float.IsInfinity(rot.W))
                    {
                        logger.ZLogError($"Found {nameof(WorldEntity)} with NaN or infinite rotation. Resetting rotation.");
                        we.Transform.LocalRotation = NitroxQuaternion.Identity;
                    }

                    if (we.Level == 100)
                    {
                        logger.ZLogError($"Found a world entity with cell level 100. It will be hotfixed.\n{we}");
                        we.Level = 0;
                        brokenEntitiesFound = true;
                    }
                }

                // We will re-build the child hierarchy below and want to avoid duplicates.
                // TODO: Rework system to no longer persist children entities because they are duplicates.
                entity.ChildEntities.Clear();
            }

            if (brokenEntitiesFound)
            {
                logger.ZLogWarningOnce($"Please consider reporting the above issues on the Discord server.");
            }

            EntityParentLinks.Link(entityData.Entities, entitiesById, logger);
        }

        void GlobalRootDataPostProcessing(GlobalRootData globalRootData)
        {
            foreach (GlobalRootEntity entity in globalRootData.Entities)
            {
                EnsureChildrenTransformAreParented(entity);
            }

            void EnsureChildrenTransformAreParented(WorldEntity entity)
            {
                if (entity.Transform == null)
                {
                    return;
                }

                bool brokenEntitiesFound = false;

                foreach (Entity child in entity.ChildEntities)
                {
                    if (child is not WorldEntity childWe)
                    {
                        continue;
                    }

                    if (child is not GlobalRootEntity && childWe.Level == 100)
                    {
                        logger.ZLogError($"Found a world entity with cell level 100. It will be hotfixed.\n{childWe}");
                        childWe.Level = 0;
                        brokenEntitiesFound = true;
                    }

                    if (childWe.Transform != null)
                    {
                        childWe.Transform.SetParent(entity.Transform, false);
                        EnsureChildrenTransformAreParented(childWe);
                    }
                }

                if (brokenEntitiesFound)
                {
                    logger.ZLogWarningOnce($"Please consider reporting the above issues on the Discord server.");
                }
            }
        }
    }

    // TODO: This method should be removed. Each service should load its own data instead of centralizing it here.
    private async Task LoadPersistedWorldIntoServicesAsync(PersistedWorldData pWorldData)
    {
        try
        {
            string seed = options.Value.Seed;
            logger.ZLogInformation($"Loading world with seed {seed}");

            // Time
            timeService.ActiveTime = TimeSpan.FromSeconds(pWorldData.WorldData.GameData.StoryTiming.RealTimeElapsed);
            timeService.GameTime = TimeSpan.FromSeconds(pWorldData.WorldData.GameData.StoryTiming.ElapsedSeconds);
            // Entities
            entityRegistry.AddEntities(pWorldData.EntityData.Entities);
            entityRegistry.AddEntitiesIgnoringDuplicate(pWorldData.GlobalRootData.Entities.OfType<Entity>().ToList());
            await escapePodManager.AddKnownPodsAsync(entityRegistry.GetEntities<EscapePodEntity>());
            // Before the cells are indexed: it puts the Crashfish back into the cell they stand in
            NormalizeCrashHomes();

            // TODO: hacky code - see WorldEntityManager for more information.
            List<WorldEntity> worldEntities = entityRegistry.GetEntities<WorldEntity>();
            worldEntityManager.globalRootEntitiesById = entityRegistry.GetEntities<GlobalRootEntity>().ToDictionary(entity => entity.Id);
            worldEntityManager.worldEntitiesByCell = worldEntities.Where(entity => entity is not GlobalRootEntity)
                                                                  .GroupBy(entity => entity.AbsoluteEntityCell)
                                                                  .ToDictionary(group => group.Key, group => group.ToDictionary(entity => entity.Id, entity => entity));
            EnsureRadiationLeakEntities();
            LogWorldAudit();

            foreach (Player player in pWorldData.PlayerData.GetPlayers())
            {
                playerManager.AddSavedPlayer(player);
            }
            batchEntitySpawner.SerializableParsedBatches = pWorldData.WorldData.ParsedBatchCells;
            // Pda
            pdaManager.PdaState = pWorldData.WorldData.GameData.PDAState;
            // Story progression
            storyManager.StoryGoalData = pWorldData.WorldData.GameData.StoryGoals;
            await storyScheduler.ScheduleStoriesIfNotInPast(pWorldData.WorldData.GameData.StoryGoals.ScheduledGoals);
            // Global story timed events
            storyManager.AuroraCountdownTimeMs = pWorldData.WorldData.GameData.StoryTiming.AuroraCountdownTime ?? storyManager.GenerateDeterministicAuroraTime(seed);
            storyManager.AuroraWarningTimeMs = pWorldData.WorldData.GameData.StoryTiming.AuroraWarningTime ?? timeService.GameTime.TotalMilliseconds;
            // +27 is from CrashedShipExploder.IsExploded, -480 is from the default time (see TimeService)
            storyManager.AuroraRealExplosionTime = TimeSpan.FromSeconds(pWorldData.WorldData.GameData.StoryTiming.AuroraRealExplosionTime ?? storyManager.AuroraCountdownTimeMs * 0.001 + 27 - TimeService.DEFAULT_STARTING_GAME_TIME_SECONDS);
        }
        finally
        {
            hasFinishedLoadingTcs.TrySetResult(true);
        }

        logger.ZLogInformation($"World finished loading");
    }

    private async Task<bool> LoadWorldFromSavePathAsync(string saveDir)
    {
        if (!Directory.Exists(saveDir) || !File.Exists(Path.Combine(saveDir, $"Version{FileEnding}")))
        {
            logger.ZLogWarning($"No previous save file found, creating a new one");
            return false;
        }

        UpgradeSave(saveDir);

        PersistedWorldData persistedData = LoadPersistedWorld(saveDir);
        if (persistedData == null)
        {
            return false;
        }
        RepairLoadedWorld(persistedData, saveDir);
        await LoadPersistedWorldIntoServicesAsync(persistedData);
        return true;
    }

    /// <summary>
    ///     One backup of the world per server start, before the world is read (and before anything can be repaired or saved), kept apart from the rolling backups: see <see cref="ServerStartBackups" />.
    ///     Not made in the dry run of the world repair, which must not touch the world folder.
    /// </summary>
    private async Task BackUpBeforeLoadAsync(string saveDir)
    {
        if (startOptions.Value.HealDryRun || options.Value.MaxBackups < 1 || !File.Exists(Path.Combine(saveDir, $"Version{FileEnding}")))
        {
            return;
        }

        try
        {
            string backupsDir = startOptions.Value.GetServerSaveBackupsPath();
            string zipPath = await Task.Run(() => ServerStartBackups.CreateAndPrune(saveDir, backupsDir, DateTime.Now));
            logger.ZLogInformation($"World backed up before it is loaded: {zipPath}");
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"The backup of the world at server start failed, the server starts anyway");
        }
    }

    /// <summary>
    ///     Puts a world that coop.3 saved after a restart right (see <see cref="WorldRepair" />). The world is loaded as it is when the repair cannot run.
    /// </summary>
    private void RepairLoadedWorld(PersistedWorldData persistedData, string saveDir)
    {
        try
        {
            repairVersion = WorldRepair.Run(persistedData, saveDir, startOptions.Value.HealDryRun, DateTime.Now, logger, options.Value.Seed);
        }
        catch (Exception ex)
        {
            repairVersion = persistedData.WorldData?.RepairVersion ?? 0;
            logger.ZLogError(ex, $"The repair of the world failed, the world is loaded as it was saved");
        }
    }

    /// <summary>
    ///     The sulfur plants of the world must agree with the Crashfish the world holds for them (see <see cref="CrashHomeStates" />): coop.3 lost the fish of nearly every home. Runs at every start, finds nothing in a healthy world.
    /// </summary>
    private void NormalizeCrashHomes()
    {
        try
        {
            // A home points to one fish: a second Crash entity under it would be a ghost on every client that loads the home, at every load (the fish of a home now stay through a restart)
            List<Entity> surplusFish = CrashHomeStates.FindSurplusFish(entityRegistry.GetAllEntities());
            foreach (Entity fish in surplusFish)
            {
                entityRegistry.RemoveEntity(fish.Id);
            }
            if (surplusFish.Count > 0)
            {
                logger.ZLogWarning($"[CrashHomes] {surplusFish.Count} Crashfish were a second fish under a home that has one and were removed from the world: {string.Join(", ", surplusFish.Take(10).Select(surplus => surplus.Id.ToString()))}{(surplusFish.Count > 10 ? ", ..." : "")}");
            }

            CrashHomeStates.Result result = CrashHomeStates.Normalize(entityRegistry.GetAllEntities());
            if (result.Changed > 0)
            {
                logger.ZLogInformation($"[CrashHomes] {result.FishlessHomes} homes without a fish were made fresh (their fish is made at once), {result.HomesWithFishAndTimer} homes with a fish and a timer lost the timer, {result.OtherEntities} entities that are no home lost the state of a home, {result.FishTransforms} Crashfish were taken out from under the transform of their home");
            }
        }
        catch (Exception ex)
        {
            logger.ZLogWarning(ex, $"The check of the crash homes failed, the world is loaded as it is");
        }
    }

    private void LogWorldAudit()
    {
        try
        {
            WorldAuditResult audit = WorldAudit.Run(entityRegistry.GetAllEntities(), DuplicateEntityRepair.IsMobileCreature);
            logger.ZLogInformation($"[WorldAudit] {audit}");
        }
        catch (Exception ex)
        {
            logger.ZLogWarning(ex, $"The audit of the loaded world failed");
        }
    }

    private async Task CreateAndLoadWorldAsync()
    {
        PersistedWorldData pWorldData = new()
        {
            EntityData = EntityData.From([]),
            PlayerData = PlayerData.From([]),
            WorldData = new WorldData
            {
                GameData = new GameData
                {
                    PDAState = new PdaStateData(),
                    StoryGoals = new StoryGoalData(),
                    StoryTiming = new StoryTimingData()
                },
                ParsedBatchCells = [],
                // Nothing in a new world needs repair
                RepairVersion = WorldRepair.CURRENT_VERSION
            },
            GlobalRootData = new GlobalRootData()
        };
        repairVersion = WorldRepair.CURRENT_VERSION;
        // The 11 radiation leaks of the Aurora are created while the world loads (see EnsureRadiationLeakEntities), so that the very first save of a new world has them too
        await LoadPersistedWorldIntoServicesAsync(pWorldData);
    }

    /// <summary>
    ///     The 11 RadiationLeakPoint located at (Aurora Scene) //Aurora-MainPrefab/Aurora/radiationleaks/RadiationLeaks(Clone) need one entity each (the clients bind the leaks of their scene to
    ///     them by index, which gives the leaks their ids and makes a weld travel). They used to be created for brand-new worlds only, after the first save of the world had been written: a world that
    ///     was started again from that save, or a save from before the leaks existed, never got them (no ids, welds not synced, radius not recomputed for a joiner). Also puts the leaks of older
    ///     saves right: they were saved as entities that clients watch as if they moved.
    /// </summary>
    private void EnsureRadiationLeakEntities()
    {
        List<RadiationLeakEntity> existing = entityRegistry.GetEntities<RadiationLeakEntity>();
        foreach (RadiationLeakEntity leak in existing)
        {
            leak.SpawnedByServer = true;
            leak.Transform = new();
        }

        foreach (int missingIndex in MissingLeakIndices(existing.Select(leak => leak.ObjectIndex)))
        {
            logger.ZLogInformation($"Creating the radiation leak entity {missingIndex} of the Aurora");
            worldEntityManager.AddOrUpdateGlobalRootEntity(new RadiationLeakEntity(new(), missingIndex, new(0)));
        }
    }

    /// <param name="presentIndices">The object indices of the leak entities that exist.</param>
    /// <param name="totalLeaks">Defined by Subnautica and never to be modified.</param>
    internal static IEnumerable<int> MissingLeakIndices(IEnumerable<int> presentIndices, int totalLeaks = 11)
    {
        HashSet<int> present = [.. presentIndices];
        return Enumerable.Range(0, totalLeaks).Where(index => !present.Contains(index)).ToList();
    }

    private void UpgradeSave(string saveDir)
    {
        SaveFileVersion saveFileVersion;

        try
        {
            saveFileVersion = Serializer.Deserialize<SaveFileVersion>(Path.Combine(saveDir, $"Version{FileEnding}"));
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Error while upgrading save file. \"Version{FileEnding}\" couldn't be read.");
            return;
        }

        if (saveFileVersion == null || saveFileVersion.Version == NitroxEnvironment.Version)
        {
            return;
        }

        try
        {
            foreach (SaveDataUpgrade upgrade in upgrades)
            {
                if (upgrade.TargetVersion > saveFileVersion.Version)
                {
                    upgrade.UpgradeSaveFiles(saveDir, FileEnding);
                }
            }
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Error while upgrading save file.");
            return;
        }

        Serializer.Serialize(Path.Combine(saveDir, $"Version{FileEnding}"), new SaveFileVersion());
        logger.ZLogInformation($"Save file was upgraded to {NitroxEnvironment.Version:@Version}");
    }
}
