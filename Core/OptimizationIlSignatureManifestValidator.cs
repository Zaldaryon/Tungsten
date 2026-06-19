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
                    ["1.22.0"] = "0f575bbe8ede0f4c566fd08fc25a611c99a1468d002b81799b5f2dc5b27e0e51",
                    ["1.22.1"] = "0f575bbe8ede0f4c566fd08fc25a611c99a1468d002b81799b5f2dc5b27e0e51",
                    ["1.22.2"] = "0f575bbe8ede0f4c566fd08fc25a611c99a1468d002b81799b5f2dc5b27e0e51",
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
                    ["1.22.0"] = "d5dfce4cea3876094958abb0bb6e3a2558d677064b53040d9707d5646fffa8a3",
                    ["1.22.1"] = "d5dfce4cea3876094958abb0bb6e3a2558d677064b53040d9707d5646fffa8a3",
                    ["1.22.2"] = "bb457f7496d56510381470d2aa0dc299be5d2e72186456e55302fb4fb25e980b",
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
                    ["1.22.0"] = "88257caeef4c5ecf41636faa4123b0c35bcc15bb556460404c262dede0a0894b",
                    ["1.22.1"] = "7cf08c594dfe4ad948a4391bbae67d6465529d265b4dc34d103881ceaad01be6",
                    ["1.22.2"] = "7cf08c594dfe4ad948a4391bbae67d6465529d265b4dc34d103881ceaad01be6",
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
                    ["1.22.0"] = "42af6abea0aee2256492c6eaf2d7d7d96840b50c9d815ce4a7247d2aeea94e43",
                    ["1.22.1"] = "8480d81efb78fe5d72426591ce38240867e9a567e693e5e0d221177bdee98f37",
                    ["1.22.2"] = "8480d81efb78fe5d72426591ce38240867e9a567e693e5e0d221177bdee98f37",
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
                    ["1.22.0"] = "2138c1ae039e8f2edcad0eeb29e93b7037822c6ecebe3d7ab834bb878fe07c50",
                    ["1.22.1"] = "c99cf93b04cfc42fab42cb531d2866bf3ecaff210a9c181823bde850ce0a1402",
                    ["1.22.2"] = "c99cf93b04cfc42fab42cb531d2866bf3ecaff210a9c181823bde850ce0a1402",
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
                    ["1.22.0"] = "de19a81e378d25b015bfde2b7baada28fa7cfb3900162a83cb97907528c5adbf",
                    ["1.22.1"] = "04761f2984e3f2388d04cd921ddad4d9ddd95694363e25c5585dad33c3e57d45",
                    ["1.22.2"] = "04761f2984e3f2388d04cd921ddad4d9ddd95694363e25c5585dad33c3e57d45",
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
                    ["1.22.0"] = "c54657eca1f30c2d3e93bb6821927d23da810de1d3b8f0df297c75cd2010e9da",
                    ["1.22.1"] = "c54657eca1f30c2d3e93bb6821927d23da810de1d3b8f0df297c75cd2010e9da",
                    ["1.22.2"] = "c54657eca1f30c2d3e93bb6821927d23da810de1d3b8f0df297c75cd2010e9da",
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
                    ["1.22.0"] = "08ce00027d7e57ee441928dec4308439a1771056f4f717db22868dced47de73e",
                    ["1.22.1"] = "c5a35d8573b55f468d8c257525bd2f7878da8cc9df4e4f813e14ce5042cc90e1",
                    ["1.22.2"] = "1a93b11d4c408a1bd10b263ac21bc1c2024e028cd23012d92d88863f9d55cf0c",
                }
            ),
            new SignatureRule(
                "GetEntitiesAroundOptimization.GetEntitiesAround",
                "EnableGetEntitiesAroundOptimization",
                "GetEntitiesAroundOptimization",
                "Vintagestory.Common.GameMain",
                "GetEntitiesAround",
                new[] { "Vec3d", "System.Single", "System.Single", "ActionConsumable" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "ebd01502a9cea4f01f3c8bf677db6e1e49267bf869e034a068d4be7c24a3d02c",
                    ["1.22.1"] = "7190524459f36a8d8b8fc4a457a524e511eee5a7cb9bc5a2641fe09b9decb53d",
                    ["1.22.2"] = "81b39ece1447fef29b13fe10b63c5e3714ef04ac59d0a4db6b29095d2aaba104",
                }
            ),
            new SignatureRule(
                "EntityDespawnPacketOptimization.GetEntityDespawnPacket",
                "EnableEntityDespawnPacketOptimization",
                "EntityDespawnPacketOptimization",
                "Vintagestory.Server.ServerPackets",
                "GetEntityDespawnPacket",
                new[] { "List`1" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "3bebe44bc125da1cb7c93ed9bce983069ae9b0334e01aae5c69f455f6377c30c",
                    ["1.22.1"] = "096c244584a9585537a15b516b38f46f61cd8cfc74f9ca661546a3ce31b88e4a",
                    ["1.22.2"] = "096c244584a9585537a15b516b38f46f61cd8cfc74f9ca661546a3ce31b88e4a",
                }
            ),
            new SignatureRule(
                "RecipeBaseLinqOptimization.MergeStacks",
                "EnableRecipeBaseLinqOptimization",
                "RecipeBaseLinqOptimization",
                "Vintagestory.API.Common.RecipeBase",
                "MergeStacks",
                new[] { "ItemSlot[]", "List`1" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "7b14ada503401c3eaf5b2e203939b144d3a4b9e148b7bdf25da21f5080edc7c6",
                    ["1.22.1"] = "7b14ada503401c3eaf5b2e203939b144d3a4b9e148b7bdf25da21f5080edc7c6",
                    ["1.22.2"] = "d9b9a24aa61b4c1e48dc68fd6da99bdb6c7ed2cf9005c6ee9dd2ff8025a2bad4",
                }
            ),
            new SignatureRule(
                "RecipeBaseLinqOptimization.MatchWildcardIngredients",
                "EnableRecipeBaseLinqOptimization",
                "RecipeBaseLinqOptimization",
                "Vintagestory.API.Common.RecipeBase",
                "MatchWildcardIngredients",
                new[] { "List`1", "IRecipeIngredient[]" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "6039589d0b1f8d9b35c9b7489ced4c752bf06b89fb5ddecc332bb93b4f833a2d",
                    ["1.22.1"] = "6039589d0b1f8d9b35c9b7489ced4c752bf06b89fb5ddecc332bb93b4f833a2d",
                    ["1.22.2"] = "5cb31db0ccdd96a40af8526970617313cdf2dd13ea96b77d18c2ad2e0a460cfd",
                }
            ),
            new SignatureRule(
                "BroadcastLinqOptimization.BroadcastArbitraryPacket_Bytes",
                "EnableBroadcastLinqOptimization",
                "BroadcastLinqOptimization",
                "Vintagestory.Server.ServerMain",
                "BroadcastArbitraryPacket",
                new[] { "System.Byte[]", "IServerPlayer[]" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "62bf8f516349401f62c08edb7f05d6f80ed2f883e864f5e0e6bfdf9e2a54fe75",
                    ["1.22.1"] = "da0a46725b35e331ba3ff57aa95888cc43c0a68c65dc94778a601350f1bf53c8",
                    ["1.22.2"] = "da0a46725b35e331ba3ff57aa95888cc43c0a68c65dc94778a601350f1bf53c8",
                }
            ),
            new SignatureRule(
                "BroadcastLinqOptimization.BroadcastArbitraryPacket_Packet",
                "EnableBroadcastLinqOptimization",
                "BroadcastLinqOptimization",
                "Vintagestory.Server.ServerMain",
                "BroadcastArbitraryPacket",
                new[] { "Packet_Server", "IServerPlayer[]" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "2f8a7e80c03924e23b50d43e07b302d321311649acbd21072acb9873d64ae7fc",
                    ["1.22.1"] = "7240eda18e88e7170bf0afecc35fa00c2d60091d391b89d256469b22b010951b",
                    ["1.22.2"] = "7240eda18e88e7170bf0afecc35fa00c2d60091d391b89d256469b22b010951b",
                }
            ),
            new SignatureRule(
                "BroadcastLinqOptimization.BroadcastArbitraryUdpPacket",
                "EnableBroadcastLinqOptimization",
                "BroadcastLinqOptimization",
                "Vintagestory.Server.ServerMain",
                "BroadcastArbitraryUdpPacket",
                new[] { "Packet_UdpPacket", "IServerPlayer[]" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "49670938fec1d038459d989fe8002a1e97a36b31557a4781b556052c85252f3e",
                    ["1.22.1"] = "bc4fb30217220ac9d1ac997ed3e39544d7e0d570dd2ca04664eb8f48fa43ba5a",
                    ["1.22.2"] = "bc4fb30217220ac9d1ac997ed3e39544d7e0d570dd2ca04664eb8f48fa43ba5a",
                }
            ),
            new SignatureRule(
                "BulkEntityAttributesPacketOptimization.GetBulkEntityAttributesPacket",
                "EnableBulkEntityAttributesPacketOptimization",
                "BulkEntityAttributesPacketOptimization",
                "Vintagestory.Server.ServerPackets",
                "GetBulkEntityAttributesPacket",
                new[] { "List`1", "List`1" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "3f740e57fa3470dd68a1ed9342652a8ce209c58ad1e3145ca57d3fc772141705",
                    ["1.22.1"] = "dc1654ab869aa51fed18576d3e9a55c50eb6310ad09e066a4d26fed31d5441a6",
                    ["1.22.2"] = "dc1654ab869aa51fed18576d3e9a55c50eb6310ad09e066a4d26fed31d5441a6",
                }
            ),
            new SignatureRule(
                "GetPlayersAroundOptimization.GetPlayersAround",
                "EnableGetPlayersAroundOptimization",
                "GetPlayersAroundOptimization",
                "Vintagestory.Server.ServerMain",
                "GetPlayersAround",
                new[] { "Vec3d", "System.Single", "System.Single", "ActionConsumable" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1.22.0"] = "708ae19216c06dbeb7cb38b47e18b6eb1feb2c1be997a75583af9bb520978036",
                    ["1.22.1"] = "d49b4982acedce4d6f905963065cd7b03cb6a0bc2652fb0f5f493dd13cdac04e",
                    ["1.22.2"] = "d49b4982acedce4d6f905963065cd7b03cb6a0bc2652fb0f5f493dd13cdac04e",
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

        /// <summary>
        /// Dumps all current IL hashes for the running game version.
        /// Use via /tungsten manifest dump to capture hashes for a new game version.
        /// </summary>
        public static void DumpCurrentHashes(ICoreServerAPI api)
        {
            string version = NormalizeVersion(GameVersion.ShortGameVersion);
            api.Logger.Notification("[Tungsten] [ILSignatureManifest] Dumping hashes for version " + version + ":");

            foreach (var rule in Rules)
            {
                if (!TryResolveMethod(rule, out MethodInfo method))
                {
                    api.Logger.Warning("[Tungsten] [ILSignatureManifest]   " + rule.RuleKey + " = NOT FOUND");
                    continue;
                }

                if (!TryComputeMethodHash(method, out string hash))
                {
                    api.Logger.Warning("[Tungsten] [ILSignatureManifest]   " + rule.RuleKey + " = HASH FAILED");
                    continue;
                }

                api.Logger.Notification("[Tungsten] [ILSignatureManifest]   [\"" + version + "\"] = \"" + hash + "\", // " + rule.RuleKey);
            }
        }
    }
}
