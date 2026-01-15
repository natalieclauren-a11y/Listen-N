using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_ReplayInvariants
    {
        [Fact]
        public void HoldMode_FreezesGateAndPreventsWindowContraction()
        {
            var config = RtReplayConfig.Load(Path.Combine(GetRepoRoot(), "configs", "rt_replay_v1.json"));
            using var engine = new AdaptiveWindowEngine(config, startWorker: false);

            engine.ResetTimestampState(0);
            SetField(engine, "_fsm", Enum.Parse(GetNestedType(engine, "FSM"), "Hold"));
            SetField(engine, "_holdQuietUntilUs", long.MaxValue);
            SetField(engine, "_tgIdx", 2);
            SetField(engine, "_W", 2.0);

            engine.OnDetection(new Detection(0));

            double lastWindow = 2.0;
            for (int i = 1; i <= 5; i++)
            {
                long nowUs = i * 20_000;
                engine.OnDetection(new Detection(nowUs));
                engine.RunDeterministicStep(nowUs, config.StepPeriodS);

                int tgIdx = (int)GetField(engine, "_tgIdx");
                double window = (double)GetField(engine, "_W");

                Assert.Equal(2, tgIdx);
                Assert.True(window >= lastWindow, "Window must not contract while in Hold.");
                lastWindow = window;
            }
        }

        [Fact]
        public void GateChangesRequireConfirmationSteps()
        {
            var config = BuildConfig(gateChangeConfirmSteps: 3);
            using var engine = new AdaptiveWindowEngine(config, startWorker: false);

            var updateGate = engine.GetType().GetMethod("UpdateGateSelection", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(updateGate);

            SetField(engine, "_tgIdx", 0);

            updateGate!.Invoke(engine, new object[] { 2 });
            Assert.Equal(0, (int)GetField(engine, "_tgIdx"));

            updateGate.Invoke(engine, new object[] { 2 });
            Assert.Equal(0, (int)GetField(engine, "_tgIdx"));

            updateGate.Invoke(engine, new object[] { 2 });
            Assert.Equal(2, (int)GetField(engine, "_tgIdx"));
        }

        [Fact]
        public void AdaptWindow_IsRateLimitedMultiplicatively()
        {
            var config = BuildConfig(
                rateLimitDefaultDown: 0.8,
                rateLimitDefaultUp: 1.1,
                precheckExpandMultiplier: 1.0,
                precheckShrinkMultiplier: 1.0,
                thresholdExpandMultiplier: 1.0,
                thresholdShrinkMultiplier: 1.0);
            using var engine = new AdaptiveWindowEngine(config, startWorker: false);

            var adaptWindow = engine.GetType().GetMethod("AdaptWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(adaptWindow);

            SetField(engine, "_W", 10.0);

            adaptWindow!.Invoke(engine, new object[] { 1.0, 10.0, 1.0, 0.01, false, 0.8, 1.1 });
            double expanded = (double)GetField(engine, "_W");
            Assert.True(expanded <= 11.0 + 1e-6, "Expansion must respect upper rate limit.");

            SetField(engine, "_W", 10.0);
            adaptWindow.Invoke(engine, new object[] { 1.0, 0.0001, 1.0, 0.0001, false, 0.8, 1.1 });
            double contracted = (double)GetField(engine, "_W");
            Assert.True(contracted >= 8.0 - 1e-6, "Contraction must respect lower rate limit.");
        }

        [Fact]
        public void ReplayConfigDrivesGateLadderAndThresholds()
        {
            var config = BuildConfig(
                gateLadderS: new[] { 0.0007, 0.0014, 0.0028 },
                minSignificance: 7.0,
                zTrack: 4.0,
                zHold: 8.0,
                zPoisson: 2.5);

            using var engine = new AdaptiveWindowEngine(config, startWorker: false);

            int[] tgUs = (int[])GetField(engine, "_tgUs");
            int zMin = (int)GetField(engine, "_zMin");
            double zTrack = (double)GetField(engine, "_zTrack");
            double zHold = (double)GetField(engine, "_zHold");
            double zPoisson = (double)GetField(engine, "_zPoisson");

            Assert.Equal(new[] { 700, 1400, 2800 }, tgUs);
            Assert.Equal(7, zMin);
            Assert.Equal(4.0, zTrack, 3);
            Assert.Equal(8.0, zHold, 3);
            Assert.Equal(2.5, zPoisson, 3);
        }

        private static RtReplayConfig BuildConfig(
            int gateChangeConfirmSteps = 2,
            double[]? gateLadderS = null,
            double minSignificance = 3.0,
            double zTrack = 3.0,
            double zHold = 6.0,
            double zPoisson = 4.0,
            double rateLimitDefaultDown = 0.5,
            double rateLimitDefaultUp = 2.0,
            double precheckExpandMultiplier = 1.2,
            double precheckShrinkMultiplier = 0.9,
            double thresholdExpandMultiplier = 1.3,
            double thresholdShrinkMultiplier = 0.95)
        {
            var config = new RtReplayConfig
            {
                Version = "test",
                StepPeriodS = 0.02,
                BinWidthS = 0.00005,
                WindowStartS = 0.5,
                WindowMinS = 0.5,
                WindowMaxS = 60.0,
                Warmup = new RtReplayConfig.WarmupSettings
                {
                    RequireWindowFilled = true,
                    MinGateCount = 2,
                    RequirePositiveM1 = true
                },
                GateLadderS = gateLadderS ?? new[] { 0.0005, 0.001, 0.002 },
                CovarianceRegularizationEpsilon = 0.001,
                GateStability = new RtReplayConfig.GateStabilitySettings
                {
                    MinGateIndexForZ = 2,
                    MinGateCountForZ = 0
                },
                MinSignificance = minSignificance,
                Plateau = new RtReplayConfig.PlateauSettings
                {
                    EtaFraction = 0.05
                },
                GateChangeConfirmSteps = gateChangeConfirmSteps,
                WindowAdaptation = new RtReplayConfig.WindowAdaptationSettings
                {
                    TargetRelY = 0.1,
                    TargetRelM1 = 0.02,
                    RelUncertaintyShrinkFactor = 0.5,
                    RelUncertaintyExpandFactor = 2.0,
                    PrecheckShrinkMultiplier = precheckShrinkMultiplier,
                    PrecheckExpandMultiplier = precheckExpandMultiplier,
                    ThresholdExpandMultiplier = thresholdExpandMultiplier,
                    ThresholdShrinkMultiplier = thresholdShrinkMultiplier,
                    RateLimitDefaultDown = rateLimitDefaultDown,
                    RateLimitDefaultUp = rateLimitDefaultUp,
                    RateLimitExpandDown = 0.8,
                    RateLimitExpandUp = 2.0,
                    RateLimitContractDown = 0.5,
                    RateLimitContractUp = 1.2,
                    WFloorS = 0.5,
                    KTau = 3.0
                },
                PageHinkley = new RtReplayConfig.PageHinkleyConfig
                {
                    Singles = new RtReplayConfig.PageHinkleySettings
                    {
                        Delta = 0.005,
                        Lambda = 50.0
                    },
                    CorrZ = new RtReplayConfig.PageHinkleySettings
                    {
                        Delta = 0.005,
                        Lambda = 50.0
                    }
                },
                QuietHorizon = new RtReplayConfig.QuietHorizonSettings
                {
                    StepMultiplier = 5.0,
                    TauMultiplier = 4.0
                },
                FsmConfirmationSteps = 2,
                Thresholds = new RtReplayConfig.ThresholdSettings
                {
                    ZTrack = zTrack,
                    ZHold = zHold,
                    ZPoisson = zPoisson,
                    PoissonQuietRequired = 1,
                    LowRateGateIndex = 0,
                    DegradedGateIndex = 0,
                    LowRateWindowGrowthFactor = 1.2
                }
            };

            string tempPath = Path.Combine(Path.GetTempPath(), $"rt_replay_{Guid.NewGuid():N}.json");
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(tempPath, json);
            return RtReplayConfig.Load(tempPath);
        }

        private static object GetField(object instance, string name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException($"Field '{name}' not found.");
            }
            return field.GetValue(instance)!;
        }

        private static void SetField(object instance, string name, object value)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException($"Field '{name}' not found.");
            }
            field.SetValue(instance, value);
        }

        private static Type GetNestedType(object instance, string name)
        {
            return instance.GetType().GetNestedType(name, BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Nested type '{name}' not found.");
        }

        private static string GetRepoRoot()
        {
            string current = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string candidate = Path.Combine(current, "Listen-N.sln");
                if (File.Exists(candidate))
                {
                    return current;
                }
                current = Path.GetFullPath(Path.Combine(current, ".."));
            }
            throw new InvalidOperationException("Repository root not found.");
        }
    }
}
