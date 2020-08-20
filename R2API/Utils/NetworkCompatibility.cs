﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BepInEx;
using Mono.Cecil;
using R2API.Networking;
using RoR2;

namespace R2API.Utils {
    /// <summary>
    /// Enum used for telling whether or not the mod should be needed by everyone in multiplayer games.
    /// Also can specify if the mod does not work in multiplayer.
    /// </summary>
    public enum CompatibilityLevel {
        NoNeedForSync,
        EveryoneMustHaveMod,
        BreaksMultiplayer
    }

    /// <summary>
    /// Enum used for telling whether or not the same mod version should be used by both the server and the clients.
    /// This enum is only useful if CompatibilityLevel.EveryoneMustHaveMod was chosen.
    /// </summary>
    public enum VersionStrictness {
        DifferentModVersionsAreOk,
        EveryoneNeedSameModVersion
    }

    /// <summary>
    /// Attribute to have at the top of your BaseUnityPlugin class if
    /// you want to specify if the mod should be installed by everyone in multiplayer games or not.
    /// If the mod is required to be installed by everyone, you'll need to also specify if the same mod version should be used by everyone or not.
    /// By default, it's supposed that everyone needs the mod and the same version.
    /// e.g: [NetworkCompatibility(CompatibilityLevel.NoNeedForSync, VersionStrictness.DifferentModVersionsAreOk)]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly)]
    public class NetworkCompatibility : Attribute {
        /// <summary>
        /// Used for telling whether or not the mod should be needed by everyone in multiplayer games.
        /// </summary>
        internal CompatibilityLevel CompatibilityLevel { get; }

        /// <summary>
        /// Enum used for telling whether or not the same mod version should be used by both the server and the clients.
        /// This enum is only useful if CompatibilityLevel.EveryoneMustHaveMod was chosen.
        /// </summary>
        internal VersionStrictness VersionStrictness { get; }

        public NetworkCompatibility(
            CompatibilityLevel compatibility = CompatibilityLevel.EveryoneMustHaveMod,
            VersionStrictness versionStrictness = VersionStrictness.EveryoneNeedSameModVersion) {
            CompatibilityLevel = compatibility;
            VersionStrictness = versionStrictness;
        }
    }

    /// <summary>
    /// Forward declare this attribute in your plugin assembly
    /// and use this one instead if you are already registering your plugin yourself
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class ManualNetworkRegistrationAttribute : Attribute { }

    internal class NetworkCompatibilityHandler {
        internal const char ModGuidAndModVersionSeparator = ';';

        internal void BuildModList(PluginScanner pluginScanner) {
            var modList = new HashSet<string>();

            var scanForBepinExUnityPlugins = new PluginScanner.ClassScanRequest(typeof(BaseUnityPlugin).FullName,
                whenRequestIsDone: null, oneMatchPerAssembly: false,
                foundOnAssemblyTypes: (type, attributes) => {
                    var haveNetworkCompatAttribute = attributes.FirstOrDefault(attribute =>
                        attribute.AttributeType.FullName == typeof(NetworkCompatibility).FullName) != null;

                    var bepinPluginAttribute = attributes.FirstOrDefault(attribute =>
                        attribute.AttributeType.FullName == typeof(BepInPlugin).FullName);

                    var (modGuid, modVersion) = PluginScanner.GetBepinPluginInfo(bepinPluginAttribute?.ConstructorArguments);

                    var haveManualRegistrationAttribute = type.Module.Assembly.CustomAttributes?.FirstOrDefault(a =>
                        a.AttributeType.FullName == typeof(ManualNetworkRegistrationAttribute).FullName) != null;

                    // By default, any plugins that don't have the NetworkCompatibility attribute and
                    // don't have the ManualNetworkRegistration attribute are added to the networked mod list
                    if (!haveNetworkCompatAttribute) {
                        if (bepinPluginAttribute != null){
                            if (!haveManualRegistrationAttribute) {
                                modList.Add(modGuid + ModGuidAndModVersionSeparator + modVersion);
                            }
                            else {
                                R2API.Logger.LogDebug($"Found {nameof(ManualNetworkRegistrationAttribute)} type. Ignoring.");
                            }
                        }
                        else {
                            R2API.Logger.LogDebug($"Found {nameof(BaseUnityPlugin)} type but no {nameof(BepInPlugin)} attribute");
                        }
                    }
                });

            pluginScanner.AddScanRequest(scanForBepinExUnityPlugins);

            var scanRequestForNetworkCompatAttr = new PluginScanner.AttributeScanRequest(attributeTypeFullName: typeof(NetworkCompatibility).FullName,
                attributeTargets: AttributeTargets.Assembly | AttributeTargets.Class,
                CallWhenAssembliesAreScanned,
                oneMatchPerAssembly: true,
                foundOnAssemblyAttributes: (assembly, arguments) => {
                    TryGetNetworkCompatibilityArguments(arguments, out var compatibilityLevel, out var versionStrictness);

                    if (compatibilityLevel == CompatibilityLevel.EveryoneMustHaveMod) {
                        modList.Add(versionStrictness == VersionStrictness.EveryoneNeedSameModVersion
                            ? assembly.Name.FullName
                            : assembly.Name.Name);
                    }
                }, foundOnAssemblyTypes: (type, arguments) => {
                    TryGetNetworkCompatibilityArguments(arguments, out var compatibilityLevel, out var versionStrictness);

                    if (compatibilityLevel == CompatibilityLevel.EveryoneMustHaveMod) {
                        var bepinPluginAttribute = type.CustomAttributes.FirstOrDefault(attr =>
                            attr.AttributeType.Resolve().IsSubtypeOf(typeof(BepInPlugin)));

                        if (bepinPluginAttribute != null) {
                            var (modGuid, modVersion) = PluginScanner.GetBepinPluginInfo(bepinPluginAttribute.ConstructorArguments);
                            modList.Add(versionStrictness == VersionStrictness.EveryoneNeedSameModVersion
                                ? modGuid + ModGuidAndModVersionSeparator + modVersion
                                : modGuid);
                        }
                        else {
                            throw new Exception($"Could not find corresponding {nameof(BepInPlugin)} Attribute of your plugin, " +
                                                $"make sure that the {nameof(NetworkCompatibility)} attribute is " +
                                                $"on the same class as the {nameof(BepInPlugin)} attribute. " +
                                                $"If you don't have a plugin that has a class heriting from {nameof(BaseUnityPlugin)}, " +
                                                $"put the {nameof(NetworkCompatibility)} attribute as an Assembly attribute instead");
                        }
                    }
                }, attributeMustBeOnTypeFullName: typeof(BaseUnityPlugin).FullName);

            pluginScanner.AddScanRequest(scanRequestForNetworkCompatAttr);

            void CallWhenAssembliesAreScanned() {
                if (modList.Count != 0) {
                    if (IsR2APIAffectingNetwork()) {
                        modList.Add(R2API.PluginGUID + ModGuidAndModVersionSeparator + R2API.PluginVersion);
                    }
                    var sortedModList = modList.ToList();
                    sortedModList.Sort();
                    R2API.Logger.LogInfo("[NetworkCompatibility] Adding to the networkModList : ");
                    foreach (var mod in sortedModList) {
                        R2API.Logger.LogInfo(mod);
                        NetworkModCompatibilityHelper.networkModList = NetworkModCompatibilityHelper.networkModList.Append(mod);
                    }
                }
            }
        }

        internal static bool IsR2APIAffectingNetwork() {
            return APISubmoduleHandler.IsLoaded(nameof(NetworkingAPI));
        }

        private static void TryGetNetworkCompatibilityArguments(IList<CustomAttributeArgument> attributeArguments,
            out CompatibilityLevel compatibilityLevel, out VersionStrictness versionStrictness) {
            compatibilityLevel = CompatibilityLevel.EveryoneMustHaveMod;
            versionStrictness = VersionStrictness.EveryoneNeedSameModVersion;

            if (attributeArguments != null && attributeArguments.Count > 0) {
                if (attributeArguments[0].Value is int) {
                    compatibilityLevel = (CompatibilityLevel)attributeArguments[0].Value;
                }
                if (attributeArguments[1].Value is int) {
                    versionStrictness = (VersionStrictness)attributeArguments[1].Value;
                }
            }
        }
    }
}
