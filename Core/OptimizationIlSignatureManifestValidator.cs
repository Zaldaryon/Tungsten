using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// P9: validates critical patch target method IL signatures per game version.
    /// On mismatch, affected optimizations are disabled pre-patch to preserve vanilla behavior.
    /// </summary>
    public static class OptimizationIlSignatureManifestValidator
    {
        private static readonly SignatureRule[] Rules =
        {
            new SignatureRule(
                "PlaceholderOptimization.FillPlaceHolder",
                "EnablePlaceholderOptimization",
                "PlaceholderOptimization",
                "Vintagestory.API.Common.RegistryObject",
                "FillPlaceHolder",
                new[] { "System.String", "OrderedDictionary" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "a70d0bacda1b3f78d112b9512565435af86a957c234fde1dee7a8c6afad66c74"
                }
            ),
            new SignatureRule(
                "WildcardFastMatchOptimization.fastMatch",
                "EnableWildcardFastMatchOptimization",
                "WildcardFastMatchOptimization",
                "Vintagestory.API.Util.WildcardUtil",
                "fastMatch",
                new[] { "System.String", "System.String" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "a01792022c3e4804213f05621a476ee8fcba4056a33c7b4e64529eddced76665"
                }
            ),
            new SignatureRule(
                "PhysicsManagerListOptimization.BuildClientList",
                "EnablePhysicsManagerListOptimization",
                "PhysicsManagerListOptimization",
                "Vintagestory.Server.PhysicsManager",
                "BuildClientList",
                new[] { "System.Collections.Generic.ICollection`1" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "0bb558741ad1f8c53ac70557f76739c90264a1615d8e28d68a533da772fd544e"
                }
            ),
            new SignatureRule(
                "PhysicsManagerMethodListOptimization.SendPositionsAndAnimations",
                "EnablePhysicsManagerMethodListOptimization",
                "PhysicsManagerMethodListOptimization",
                "Vintagestory.Server.PhysicsManager",
                "SendPositionsAndAnimations",
                new[] { "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.Dictionary`2", "System.Int32", "System.Boolean" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "19c21dfc99d7711a6bdaf314235ad58671771c2590a53a48a4675eea3fd1d7a3"
                }
            ),
            new SignatureRule(
                "PhysicsManagerMethodListOptimization.SendTrackedEntitiesStateChanges",
                "EnablePhysicsManagerMethodListOptimization",
                "PhysicsManagerMethodListOptimization",
                "Vintagestory.Server.PhysicsManager",
                "SendTrackedEntitiesStateChanges",
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "8f53302c5b47abfb998df52494367dc546c62ae0c06c267776e9be249eb3dff8"
                }
            ),
            new SignatureRule(
                "ServerMainLinqOptimization.get_AllOnlinePlayers",
                "EnableServerMainLinqOptimization",
                "ServerMainLinqOptimization",
                "Vintagestory.Server.ServerMain",
                "get_AllOnlinePlayers",
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "3d31778f4f6df04cf40973cb0b284c37005a884d16451f94d0c088504fcb1202"
                }
            ),
            new SignatureRule(
                "ServerMainLinqOptimization.get_AllPlayers",
                "EnableServerMainLinqOptimization",
                "ServerMainLinqOptimization",
                "Vintagestory.Server.ServerMain",
                "get_AllPlayers",
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "45176931c4a22b4e3fe883a856f6f793c8692a8f5240c293d870756440fd9593"
                }
            ),
            new SignatureRule(
                "SendPlayerEntityDeathsOptimization.SendPlayerEntityDeaths",
                "EnableSendPlayerEntityDeathsOptimization",
                "SendPlayerEntityDeathsOptimization",
                "Vintagestory.Server.ServerSystemEntitySimulation",
                "SendPlayerEntityDeaths",
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.21.6"] = "fb4500bba9c30d855840b5ece5da14f46ff164913f6cac1b7372fa35dd8c72f1"
                }
            )
        };

        private sealed class SignatureRule
        {
            public SignatureRule(
                string ruleKey,
                string configProperty,
                string optimizationKey,
                string typeName,
                string methodName,
                string[] parameterTypeContains,
                Dictionary<string, string> expectedHashesByVersion)
            {
                RuleKey = ruleKey;
                ConfigProperty = configProperty;
                OptimizationKey = optimizationKey;
                TypeName = typeName;
                MethodName = methodName;
                ParameterTypeContains = parameterTypeContains;
                ExpectedHashesByVersion = expectedHashesByVersion;
            }

            public string RuleKey { get; }
            public string ConfigProperty { get; }
            public string OptimizationKey { get; }
            public string TypeName { get; }
            public string MethodName { get; }
            public string[] ParameterTypeContains { get; }
            public Dictionary<string, string> ExpectedHashesByVersion { get; }
        }

        public sealed class ValidationResult
        {
            public bool ManifestUnavailableForVersion { get; set; }
            public int CheckedRules { get; set; }
            public int DisabledOptimizations { get; set; }
        }

        public static ValidationResult ValidateAndApply(ICoreServerAPI api, TungstenConfig config)
        {
            var result = new ValidationResult();
            if (api == null || config == null || !config.EnableIlSignatureManifestValidation)
                return result;

            string version = NormalizeVersion(GameVersion.ShortGameVersion);
            if (!Rules.Any(r => r.ExpectedHashesByVersion.ContainsKey(version)))
            {
                api.Logger.Warning(
                    "[Tungsten] [ILSignatureManifest] No built-in manifest for game version " + version +
                    ". Validation will be disabled and vanilla-safe patching behavior will remain."
                );
                result.ManifestUnavailableForVersion = true;
                return result;
            }

            foreach (var rule in Rules)
            {
                if (!GetConfigFlag(config, rule.ConfigProperty))
                    continue;

                if (!rule.ExpectedHashesByVersion.TryGetValue(version, out string expectedHash))
                    continue;

                result.CheckedRules++;

                if (!TryResolveMethod(rule, out MethodInfo method))
                {
                    DisableOptimizationForRule(api, config, rule, "target method not found");
                    result.DisabledOptimizations++;
                    continue;
                }

                if (!TryComputeMethodHash(method, out string currentHash))
                {
                    DisableOptimizationForRule(api, config, rule, "could not compute IL hash");
                    result.DisabledOptimizations++;
                    continue;
                }

                if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    DisableOptimizationForRule(
                        api,
                        config,
                        rule,
                        "IL hash mismatch (expected " + expectedHash + ", got " + currentHash + ")"
                    );
                    result.DisabledOptimizations++;
                }
            }

            if (result.DisabledOptimizations == 0)
            {
                api.Logger.Notification(
                    "[Tungsten] [ILSignatureManifest] Validation passed (" + result.CheckedRules + " rule(s) checked)"
                );
            }
            else
            {
                api.Logger.Warning(
                    "[Tungsten] [ILSignatureManifest] Disabled " + result.DisabledOptimizations +
                    " optimization(s) due to signature mismatch; falling back to vanilla paths"
                );
            }

            return result;
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            string core = version.Split('-')[0];
            string[] parts = core.Split('.');
            if (parts.Length >= 3)
                return parts[0] + "." + parts[1] + "." + parts[2];

            return core;
        }

        private static bool TryResolveMethod(SignatureRule rule, out MethodInfo method)
        {
            method = null;
            var type = AccessTools.TypeByName(rule.TypeName);
            if (type == null)
                return false;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var candidate in type.GetMethods(flags))
            {
                if (!string.Equals(candidate.Name, rule.MethodName, StringComparison.Ordinal))
                    continue;

                var parameters = candidate.GetParameters();
                if (parameters.Length != rule.ParameterTypeContains.Length)
                    continue;

                bool match = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    string fullName = parameters[i].ParameterType.FullName ?? string.Empty;
                    if (!fullName.Contains(rule.ParameterTypeContains[i], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                    continue;

                method = candidate;
                return true;
            }

            return false;
        }

        private static bool TryComputeMethodHash(MethodInfo method, out string hash)
        {
            hash = null;
            try
            {
                var body = method.GetMethodBody();
                if (body == null)
                    return false;

                var il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                    return false;

                using var sha = SHA256.Create();
                var digest = sha.ComputeHash(il);
                hash = ToHex(digest);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static bool GetConfigFlag(TungstenConfig config, string propertyName)
        {
            var prop = typeof(TungstenConfig).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || prop.PropertyType != typeof(bool))
                return false;

            return (bool)prop.GetValue(config);
        }

        private static void DisableOptimizationForRule(
            ICoreServerAPI api,
            TungstenConfig config,
            SignatureRule rule,
            string reason)
        {
            var prop = typeof(TungstenConfig).GetProperty(rule.ConfigProperty, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || prop.PropertyType != typeof(bool))
                return;

            bool wasEnabled = (bool)prop.GetValue(config);
            if (!wasEnabled)
                return;

            prop.SetValue(config, false);
            OptimizationRuntimeCircuitBreaker.Disable(rule.OptimizationKey, "IL signature mismatch", emitLog: false);

            api.Logger.Warning(
                "[Tungsten] [ILSignatureManifest] Disabled " + rule.OptimizationKey +
                " due to rule " + rule.RuleKey + ": " + reason
            );
        }
    }
}
