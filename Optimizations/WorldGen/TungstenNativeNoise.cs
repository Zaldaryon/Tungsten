using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;

namespace Tungsten
{
    internal static partial class TungstenNativeNoise
    {
        private const string LibName = "tungsten_noise";
        private static bool s_available;
        private static bool s_resolverSet;

        public static bool IsAvailable => s_available;

        public static void Initialize(string modBasePath)
        {
            if (!s_resolverSet)
            {
                s_resolverSet = true;
                NativeLibrary.SetDllImportResolver(typeof(TungstenNativeNoise).Assembly, (name, asm, path) =>
                {
                    if (name != LibName) return IntPtr.Zero;

                    string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" :
                                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-arm64" :
                                 RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" :
                                 "linux-x64";

                    string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" :
                                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";
                    string libFile = (ext == ".dll" ? "" : "lib") + LibName + ext;
                    string libPath = Path.Combine(modBasePath, "native", rid, libFile);

                    if (!File.Exists(libPath)) return IntPtr.Zero;
                    return NativeLibrary.Load(libPath);
                });
            }

            // Test if library loads and validate bit-exact output
            try
            {
                s_available = ValidateNoise();
            }
            catch
            {
                s_available = false;
            }
        }

        /// <summary>
        /// Validates that native noise produces bit-exact same output as managed.
        /// Tests 1024 points with deterministic seeds.
        /// </summary>
        private static bool ValidateNoise()
        {
            var rng = new Random(42);
            int failures = 0;

            for (int i = 0; i < 1024; i++)
            {
                long seed = 12345L * 65599 + (i % 9);
                double x = (rng.NextDouble() * 20000.0) - 10000.0;
                double y = rng.NextDouble() * 256.0;
                double z = (rng.NextDouble() * 20000.0) - 10000.0;

                float managed = Vintagestory.API.MathTools.NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seed, x, y, z);
                float native = EvaluateImprovedXZ(seed, x, y, z);

                if (managed != native)
                {
                    failures++;
                    if (failures <= 3)
                    {
                        TungstenMod.Instance?.Api?.Logger.Warning(
                            $"[Tungsten] [NativeNoise] Validation FAIL #{failures}: seed={seed} ({x:F4},{y:F4},{z:F4}) managed={managed} native={native} diff={managed - native}");
                    }
                }
            }

            if (failures > 0)
            {
                TungstenMod.Instance?.Api?.Logger.Warning(
                    $"[Tungsten] [NativeNoise] Validation FAILED: {failures}/1024 mismatches. Native path disabled.");
                return false;
            }

            TungstenMod.Instance?.Api?.Logger.Notification(
                "[Tungsten] [NativeNoise] Validation passed: 1024/1024 bit-exact matches.");
            return true;
        }

        /// <summary>
        /// Micro-benchmark: measures native vs managed Evaluate_ImprovedXZ throughput.
        /// Call via: /tungsten diag nativenoisebench dump
        /// </summary>
        public static void RunMicroBenchmark(Vintagestory.API.Server.ICoreServerAPI api)
        {
            if (!s_available)
            {
                api.Logger.Warning("[Tungsten] [NativeNoise] Cannot benchmark - native lib not available.");
                return;
            }

            const int ITERATIONS = 100000;
            var rng = new Random(123);
            var seeds = new long[ITERATIONS];
            var xs = new double[ITERATIONS];
            var ys = new double[ITERATIONS];
            var zs = new double[ITERATIONS];

            for (int i = 0; i < ITERATIONS; i++)
            {
                seeds[i] = 12345L * 65599 + (i % 9);
                xs[i] = (rng.NextDouble() * 20000.0) - 10000.0;
                ys[i] = rng.NextDouble() * 256.0;
                zs[i] = (rng.NextDouble() * 20000.0) - 10000.0;
            }

            // Warmup
            for (int i = 0; i < 1000; i++)
            {
                NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seeds[i], xs[i], ys[i], zs[i]);
                EvaluateImprovedXZ(seeds[i], xs[i], ys[i], zs[i]);
            }

            // Benchmark managed
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float sumManaged = 0;
            for (int i = 0; i < ITERATIONS; i++)
                sumManaged += NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seeds[i], xs[i], ys[i], zs[i]);
            sw.Stop();
            double managedMs = sw.Elapsed.TotalMilliseconds;

            // Benchmark native
            sw.Restart();
            float sumNative = 0;
            for (int i = 0; i < ITERATIONS; i++)
                sumNative += EvaluateImprovedXZ(seeds[i], xs[i], ys[i], zs[i]);
            sw.Stop();
            double nativeMs = sw.Elapsed.TotalMilliseconds;

            double speedup = managedMs / nativeMs;
            double nsManaged = managedMs * 1000000.0 / ITERATIONS;
            double nsNative = nativeMs * 1000000.0 / ITERATIONS;

            api.Logger.Notification($"[Tungsten] [NativeNoise] Micro-benchmark ({ITERATIONS} evals):");
            api.Logger.Notification($"[Tungsten] [NativeNoise]   Managed: {managedMs:F1}ms ({nsManaged:F0}ns/eval)");
            api.Logger.Notification($"[Tungsten] [NativeNoise]   Native:  {nativeMs:F1}ms ({nsNative:F0}ns/eval)");
            api.Logger.Notification($"[Tungsten] [NativeNoise]   Speedup: {speedup:F2}x ({(speedup - 1) * 100:F0}% faster)");
            api.Logger.Notification($"[Tungsten] [NativeNoise]   Sums match: {sumManaged == sumNative}");
        }

        [LibraryImport(LibName, EntryPoint = "tungsten_evaluate_improved_xz")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial float EvaluateImprovedXZ(long seed, double x, double y, double z);

        [LibraryImport(LibName, EntryPoint = "tungsten_compute_column_noise")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ComputeColumnNoise(
            int numOctaves,
            long* octaveSeeds,
            double* frequencies,
            double verticalNoiseRelFreq,
            double distortedX,
            double distortedZ,
            double* colAmplitudes,
            double* colThresholds,
            float yDisplacement,
            float* landformWeights,
            int numLandforms,
            int mapSizeY,
            float* terrainYThresholdsFlat,
            int taperThreshold,
            double geoUpheavalAmplitude,
            byte* outSolidityBits);
    }
}
