namespace Si_RTS_AI.Core
{
    /// <summary>
    /// Rolling accumulator of "work done by the mod since the last frame".
    /// Every hot path (broker tick, shrimp producer, relocator, planner
    /// fires, shrimp order issuance) reports its ms cost + item count via
    /// the Add* methods below. When a lag spike hits, SnapshotAndReset()
    /// dumps and clears the accumulator — attributing the spike to the
    /// mod paths that ran in the window before it.
    /// </summary>
    internal static class RecentModWork
    {
        public static long PeriodicMs, BrokerMs, ShrimpProducerMs, ShrimpRelocatorMs, AlienConstructMs, PlannerFireMs;
        public static long LayerMs, BcMetricsMs, ShrimpStateMs, EcoRateMs, PlanKickMs;
        public static int  ShrimpMoves, ShrimpQueues, PlannerFires;
        public static void AddPeriodic(long ms)      { PeriodicMs         += ms; }
        public static void AddLayer(long ms)         { LayerMs            += ms; }
        public static void AddBcMetrics(long ms)     { BcMetricsMs        += ms; }
        public static void AddShrimpState(long ms)   { ShrimpStateMs      += ms; }
        public static void AddEcoRate(long ms)       { EcoRateMs          += ms; }
        public static void AddPlanKick(long ms)      { PlanKickMs         += ms; }
        public static void AddBroker(long ms)        { BrokerMs           += ms; }
        public static void AddShrimpProducer(long ms, int queues) { ShrimpProducerMs += ms; ShrimpQueues += queues; }
        public static void AddShrimpRelocator(long ms, int moves) { ShrimpRelocatorMs += ms; ShrimpMoves += moves; }
        public static void AddAlienConstruct(long ms) { AlienConstructMs += ms; }
        public static void AddPlannerFires(long ms, int fires) { PlannerFireMs += ms; PlannerFires += fires; }
        public static string SnapshotAndReset(float dt)
        {
            var s = $"periodic={PeriodicMs}ms (layer={LayerMs} bc={BcMetricsMs} shrimpState={ShrimpStateMs} ecoRate={EcoRateMs} plan={PlanKickMs}) " +
                    $"broker={BrokerMs}ms " +
                    $"shrimpProd={ShrimpProducerMs}ms(q={ShrimpQueues}) " +
                    $"shrimpReloc={ShrimpRelocatorMs}ms(m={ShrimpMoves})";
            PeriodicMs = BrokerMs = ShrimpProducerMs = ShrimpRelocatorMs = AlienConstructMs = PlannerFireMs = 0;
            LayerMs = BcMetricsMs = ShrimpStateMs = EcoRateMs = PlanKickMs = 0;
            ShrimpMoves = ShrimpQueues = PlannerFires = 0;
            return s;
        }
    }
}
