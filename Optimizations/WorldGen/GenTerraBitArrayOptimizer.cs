using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Phase A2: Replaces BitArray virtual calls in GenTerra.generate() and its Parallel.For lambda
    /// with inline bit manipulation via direct byte[] access.
    /// 
    /// Eliminates per-access: virtual dispatch, bounds checking.
    /// Estimated gain: 3-5% on GenTerra (~400K bit operations per chunk).
    /// 
    /// .NET 10 BitArray layout: internal byte[] _array, private int _bitLength, private int _version.
    /// All three fields are accessed to maintain full behavioral equivalence with vanilla BitArray.
    /// </summary>
    public class GenTerraBitArrayOptimizer
    {
        private readonly ICoreServerAPI api;

        public GenTerraBitArrayOptimizer(ICoreServerAPI api)
        {
            this.api = api;
        }

        public void ApplyPatches(Harmony harmony)
        {
            // Verify all three UnsafeAccessors work at startup
            var testBits = new BitArray(17); // 17 bits → 3 bytes logical, 4 bytes allocated (aligned to int)
            testBits[0] = true;
            testBits[7] = true;
            testBits[16] = true;
            try
            {
                byte[] arr = GetArray(testBits);
                int bitLen = GetBitLength(testBits);
                ref int ver = ref GetVersion(testBits);

                if (arr == null || arr.Length == 0)
                {
                    api.Logger.Warning("[Tungsten] [GenTerraBitArray] _array accessor failed. Aborting.");
                    return;
                }
                if (bitLen != 17)
                {
                    api.Logger.Warning($"[Tungsten] [GenTerraBitArray] _bitLength mismatch: expected 17, got {bitLen}. Aborting.");
                    return;
                }
                if ((arr[0] & 1) == 0 || (arr[0] & (1 << 7)) == 0 || (arr[2] & 1) == 0)
                {
                    api.Logger.Warning("[Tungsten] [GenTerraBitArray] Bit layout verification failed. Aborting.");
                    return;
                }
                // Verify version field is writable
                int prevVer = ver;
                ver++;
                if (GetVersion(testBits) != prevVer + 1)
                {
                    api.Logger.Warning("[Tungsten] [GenTerraBitArray] _version accessor not writable. Aborting.");
                    return;
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[Tungsten] [GenTerraBitArray] Startup probe failed: {ex.Message}. Aborting.");
                return;
            }

            // Self-test: compare Fast* methods against vanilla BitArray for multiple sizes
            if (!RunEquivalenceSelfTest())
            {
                api.Logger.Warning("[Tungsten] [GenTerraBitArray] Equivalence self-test FAILED. Aborting.");
                return;
            }

            var genTerraType = AccessTools.TypeByName("Vintagestory.ServerMods.GenTerra");
            if (genTerraType == null)
            {
                api.Logger.Warning("[Tungsten] [GenTerraBitArray] GenTerra type not found");
                return;
            }

            // Patch generate() — catches Phase 4 get_Item calls (sequential reads after Parallel.For)
            var generateMethod = AccessTools.Method(genTerraType, "generate");
            if (generateMethod == null)
            {
                api.Logger.Warning("[Tungsten] [GenTerraBitArray] generate() method not found");
                return;
            }

            harmony.Patch(generateMethod,
                transpiler: new HarmonyMethod(typeof(GenTerraBitArrayOptimizer), nameof(ReplaceBitArrayOps_Transpiler)));

            // Patch the Parallel.For lambda — catches SetAll + set_Item (the hot path, ~400K ops/chunk)
            // Strategy: find any nested type method that contains BitArray.SetAll or set_Item calls.
            // This is robust against different compiler naming conventions.
            var setAllMethodInfo = AccessTools.Method(typeof(BitArray), "SetAll", new[] { typeof(bool) });
            var setItemMethodInfo = AccessTools.Method(typeof(BitArray), "set_Item", new[] { typeof(int), typeof(bool) });

            int lambdaPatched = 0;
            foreach (var nestedType in genTerraType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                foreach (var method in nestedType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    try
                    {
                        var body = method.GetMethodBody();
                        if (body == null) continue;
                    }
                    catch { continue; }

                    // Check if this method's IL references BitArray methods
                    if (!MethodCallsBitArrayOps(method, setAllMethodInfo, setItemMethodInfo)) continue;

                    harmony.Patch(method,
                        transpiler: new HarmonyMethod(typeof(GenTerraBitArrayOptimizer), nameof(ReplaceBitArrayOps_Transpiler)));
                    lambdaPatched++;
                }
            }

            if (lambdaPatched == 0)
            {
                api.Logger.Warning("[Tungsten] [GenTerraBitArray] Parallel.For lambda not found. Only Phase 4 reads patched.");
            }
            else
            {
                api.Logger.Notification($"[Tungsten] [GenTerraBitArray] Patched generate() + {lambdaPatched} lambda(s)");
            }
        }

        /// <summary>
        /// Shared transpiler: replaces all BitArray callvirt with direct byte[] operations.
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> ReplaceBitArrayOps_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var logger = TungstenMod.Instance?.Api?.Logger;

            var setAllMethod = AccessTools.Method(typeof(BitArray), "SetAll", new[] { typeof(bool) });
            var setItemMethod = AccessTools.Method(typeof(BitArray), "set_Item", new[] { typeof(int), typeof(bool) });
            var getItemMethod = AccessTools.Method(typeof(BitArray), "get_Item", new[] { typeof(int) });

            if (setAllMethod == null || setItemMethod == null || getItemMethod == null)
            {
                logger?.Warning("[Tungsten] [GenTerraBitArray] BitArray methods not resolved. Transpiler aborted.");
                return instructions;
            }

            int replacedSetAll = 0, replacedSetItem = 0, replacedGetItem = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Callvirt || codes[i].operand is not MethodInfo m)
                    continue;

                if (m == setAllMethod)
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(GenTerraBitArrayOptimizer), nameof(FastSetAll));
                    replacedSetAll++;
                }
                else if (m == setItemMethod)
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(GenTerraBitArrayOptimizer), nameof(FastSetBit));
                    replacedSetItem++;
                }
                else if (m == getItemMethod)
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(GenTerraBitArrayOptimizer), nameof(FastGetBit));
                    replacedGetItem++;
                }
            }

            int total = replacedSetAll + replacedSetItem + replacedGetItem;
            if (total > 0)
            {
                logger?.Notification($"[Tungsten] [GenTerraBitArray] Transpiled: SetAll={replacedSetAll}, set_Item={replacedSetItem}, get_Item={replacedGetItem}");
            }

            return codes;
        }

        // --- UnsafeAccessor declarations for BitArray internals (.NET 10) ---

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_array")]
        private static extern ref byte[] GetArray(BitArray bits);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_bitLength")]
        private static extern ref int GetBitLength(BitArray bits);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
        private static extern ref int GetVersion(BitArray bits);

        /// <summary>
        /// Exhaustive equivalence self-test: exercises all Fast* methods against vanilla BitArray
        /// for representative sizes (matching real GenTerra mapSizeY values).
        /// Tests: SetAll(false), SetAll(true), set_Item(true/false), get_Item, version tracking.
        /// </summary>
        private bool RunEquivalenceSelfTest()
        {
            int[] testSizes = { 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 128, 256, 384, 512 };

            foreach (int size in testSizes)
            {
                var vanilla = new BitArray(size);
                var fast = new BitArray(size);

                // Test SetAll(true) + verify all bits
                vanilla.SetAll(true);
                FastSetAll(fast, true);
                for (int i = 0; i < size; i++)
                {
                    if (vanilla[i] != FastGetBit(fast, i))
                    {
                        api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: SetAll(true) bit {i} mismatch at size {size}");
                        return false;
                    }
                }

                // Verify backing arrays are bit-identical
                byte[] vArr = GetArray(vanilla);
                byte[] fArr = GetArray(fast);
                for (int b = 0; b < vArr.Length; b++)
                {
                    if (vArr[b] != fArr[b])
                    {
                        api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: backing byte[{b}] differs after SetAll(true) at size {size}: vanilla=0x{vArr[b]:X2} fast=0x{fArr[b]:X2}");
                        return false;
                    }
                }

                // Test SetAll(false) + verify all bits
                vanilla.SetAll(false);
                FastSetAll(fast, false);
                for (int i = 0; i < size; i++)
                {
                    if (vanilla[i] != FastGetBit(fast, i))
                    {
                        api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: SetAll(false) bit {i} mismatch at size {size}");
                        return false;
                    }
                }

                // Test set_Item pattern (simulates GenTerra: set scattered bits to true)
                var rng = new Random(size);
                for (int i = 0; i < size; i++)
                {
                    if (rng.Next(3) == 0) // ~33% of bits set, simulating terrain solidity
                    {
                        vanilla[i] = true;
                        FastSetBit(fast, i, true);
                    }
                }

                for (int i = 0; i < size; i++)
                {
                    if (vanilla[i] != FastGetBit(fast, i))
                    {
                        api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: scattered set bit {i} mismatch at size {size}");
                        return false;
                    }
                }

                // Test set_Item(false) — clear some bits back
                for (int i = 0; i < size; i += 3)
                {
                    vanilla[i] = false;
                    FastSetBit(fast, i, false);
                }

                for (int i = 0; i < size; i++)
                {
                    if (vanilla[i] != FastGetBit(fast, i))
                    {
                        api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: clear bit {i} mismatch at size {size}");
                        return false;
                    }
                }

                // Verify version tracking
                int vVer = GetVersion(vanilla);
                int fVer = GetVersion(fast);
                if (vVer != fVer)
                {
                    api.Logger.Error($"[Tungsten] [GenTerraBitArray] SelfTest FAIL: version mismatch at size {size}: vanilla={vVer} fast={fVer}");
                    return false;
                }
            }

            api.Logger.Notification($"[Tungsten] [GenTerraBitArray] Self-test passed ({testSizes.Length} sizes, all bit-identical)");
            return true;
        }

        /// <summary>
        /// Checks if a method's IL contains calls to BitArray.SetAll or BitArray.set_Item.
        /// Used to identify the Parallel.For lambda regardless of compiler naming convention.
        /// </summary>
        private static bool MethodCallsBitArrayOps(MethodInfo method, MethodInfo setAll, MethodInfo setItem)
        {
            try
            {
                // Use Harmony's IL reader to inspect method body
                var instructions = PatchProcessor.GetOriginalInstructions(method);
                foreach (var instr in instructions)
                {
                    if (instr.operand is MethodInfo called && (called == setAll || called == setItem))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // --- Replacement methods (full behavioral equivalence with vanilla BitArray) ---

        /// <summary>
        /// Replaces BitArray.SetAll(bool). Exact vanilla semantics:
        /// - Operates on logical byte range only
        /// - Clears unused high bits at int boundary (for CopyTo/And/Or correctness)
        /// - Increments _version
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastSetAll(BitArray bits, bool value)
        {
            byte[] arr = GetArray(bits);
            int bitLength = GetBitLength(bits);
            int byteCount = (int)(((uint)bitLength + 7u) >> 3);

            if (!value)
            {
                arr.AsSpan(0, byteCount).Clear();
            }
            else
            {
                arr.AsSpan(0, byteCount).Fill(0xFF);
                // Clear unused high bits at int boundary (vanilla uses ClearHighExtraBits)
                uint extraBits = (uint)bitLength & 31u;
                if (extraBits != 0)
                {
                    int intIndex = (int)((uint)(bitLength - 1) / 32u);
                    var intSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(arr.AsSpan());
                    intSpan[intIndex] &= (1 << (int)extraBits) - 1;
                }
            }

            GetVersion(bits)++;
        }

        /// <summary>
        /// Replaces BitArray.set_Item(int, bool). Direct bit manipulation.
        /// Eliminates: virtual dispatch, bounds check. Preserves _version increment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastSetBit(BitArray bits, int index, bool value)
        {
            byte[] arr = GetArray(bits);
            int byteIndex = index >> 3;
            byte mask = (byte)(1 << (index & 7));

            if (value)
                arr[byteIndex] |= mask;
            else
                arr[byteIndex] &= (byte)~mask;

            GetVersion(bits)++;
        }

        /// <summary>
        /// Replaces BitArray.get_Item(int). Direct bit read.
        /// Eliminates: virtual dispatch, bounds check. No _version change (read-only).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FastGetBit(BitArray bits, int index)
        {
            byte[] arr = GetArray(bits);
            return (arr[index >> 3] & (1 << (index & 7))) != 0;
        }
    }
}
