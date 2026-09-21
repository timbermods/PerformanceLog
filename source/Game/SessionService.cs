using System;
using Timberborn.EntitySystem;
using Timberborn.Modding;
using Timberborn.Population;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;

namespace PerformanceLog
{
    /// <summary>
    /// The game's own way of telling this mod that a game scene has finished loading (PostLoad) and is being left (Unload). It reads a few
    /// numbers from the game and hands them to <see cref="Session"/>, which does the work.
    /// </summary>
    public class SessionService : IPostLoadableSingleton, IUnloadableSingleton
    {
        readonly ModRepository modRepository;
        readonly SpeedManager speedManager;
        readonly EntityRegistry entityRegistry;
        readonly PopulationService populationService;
        readonly IDayNightCycle dayNightCycle;
        readonly ITickService tickService;

        public SessionService(ModRepository modRepository, SpeedManager speedManager, EntityRegistry entityRegistry,
            PopulationService populationService, IDayNightCycle dayNightCycle, ITickService tickService)
        {
            this.modRepository = modRepository;
            this.speedManager = speedManager;
            this.entityRegistry = entityRegistry;
            this.populationService = populationService;
            this.dayNightCycle = dayNightCycle;
            this.tickService = tickService;
        }

        public void PostLoad()
        {
            Milestones.Mark("post-load");
            try
            {
                Session.Start(Plugin.Config, new SessionServices
                {
                    Mods = modRepository,
                    Speed = () => speedManager.CurrentSpeed,
                    Colony = ReadColony,
                    TickIntervalSeconds = () => tickService.TickIntervalInSeconds,
                }, Plugin.Version);
            }
            catch (Exception error)
            {
                Log.Warning("The performance log could not be started: " + error.Message);
            }
        }

        public void Unload()
        {
            Session.Stop("the game was left");
        }

        /// <summary>Entities, beavers, bots and the day: cheap reads, refreshed only when a row is written.</summary>
        double[] ReadColony()
        {
            var colony = new double[4];
            try { colony[0] = entityRegistry.Entities.Count; } catch (Exception) { }
            try
            {
                var population = populationService.GlobalPopulationData;
                colony[1] = population.NumberOfBeavers;
                colony[2] = population.NumberOfBots;
            }
            catch (Exception) { }
            try { colony[3] = dayNightCycle.DayNumber; } catch (Exception) { }
            return colony;
        }
    }
}
