﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Memory;

using FFXIVClientStructs.FFXIV.Common.Configuration;
using Serilog;

namespace Dalamud.Game;

/// <summary>
/// This class represents the game's configuration.
/// </summary>
[InterfaceVersion("1.0")]
[PluginInterface]
[ServiceManager.BlockingEarlyLoadedService]
public sealed class GameConfig : IServiceType
{
    [ServiceManager.ServiceConstructor]
    private unsafe GameConfig(Framework framework)
    {
        framework.RunOnTick(() =>
        {
            Log.Information("[GameConfig] Initalizing");
            var csFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var commonConfig = &csFramework->SystemConfig.CommonSystemConfig;
            this.System = new GameConfigSection("System", framework, &commonConfig->ConfigBase);
            this.UiConfig = new GameConfigSection("UiConfig", framework, &commonConfig->UiConfig);
            this.UiControl = new GameConfigSection("UiControl", framework, () => this.UiConfig.TryGetBool("PadMode", out var padMode) && padMode ? &commonConfig->UiControlConfig + 1 : &commonConfig->UiControlConfig);
        });
    }

    /// <summary>
    /// Gets the collection of config options that persist between characters.
    /// </summary>
    public GameConfigSection System { get; private set; }

    /// <summary>
    /// Gets the collection of config options that are character specific.
    /// </summary>
    public GameConfigSection UiConfig { get; private set; }

    /// <summary>
    /// Gets the collection of config options that are control mode specific. (Mouse & Keyboard / Gamepad).
    /// </summary>
    public GameConfigSection UiControl { get; private set; }

    /// <summary>
    /// An exception thrown when a matching config option is not present in the config section.
    /// </summary>
    public class ConfigOptionNotFoundException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigOptionNotFoundException"/> class.
        /// </summary>
        /// <param name="sectionName">Name of the section being accessed.</param>
        /// <param name="configOptionName">Name of the config option that was not found.</param>
        public ConfigOptionNotFoundException(string sectionName, string configOptionName)
            : base($"The option '{configOptionName}' is not available in {sectionName}.")
        {
        }
    }

    /// <summary>
    /// An exception thrown when attempting to assign a value to a config option with the wrong type.
    /// </summary>
    public class IncorrectConfigTypeException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IncorrectConfigTypeException"/> class.
        /// </summary>
        /// <param name="sectionName">Name of the section being accessed.</param>
        /// <param name="configOptionName">Name of the config option that was not found.</param>
        /// <param name="correctType">The correct type for the config option.</param>
        /// <param name="incorrectType">The type that was attempted.</param>
        public IncorrectConfigTypeException(string sectionName, string configOptionName, ConfigType correctType, ConfigType incorrectType)
            : base($"The option '{configOptionName}' in {sectionName} is of the type {correctType}. Assigning {incorrectType} is invalid.")
        {
        }
    }

    /// <summary>
    /// Represents a section of the game config and contains helper functions for accessing and setting values.
    /// </summary>
    public class GameConfigSection
    {
        private readonly Framework framework;
        private readonly Dictionary<string, uint> indexMap = new();
        private readonly Dictionary<uint, string> nameMap = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="GameConfigSection"/> class.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="framework">The framework service.</param>
        /// <param name="configBase">Unmanaged ConfigBase instance.</param>
        internal unsafe GameConfigSection(string sectionName, Framework framework, ConfigBase* configBase)
            : this(sectionName, framework, () => configBase)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GameConfigSection"/> class.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="framework">The framework service.</param>
        /// <param name="getConfigBase">A function that determines which ConfigBase instance should be used.</param>
        internal GameConfigSection(string sectionName, Framework framework, GetConfigBaseDelegate getConfigBase)
        {
            this.SectionName = sectionName;
            this.framework = framework;
            this.GetConfigBase = getConfigBase;
            Log.Information("[GameConfig] Initalizing {SectionName} with {ConfigCount} entries.", this.SectionName, this.ConfigCount);
        }

        /// <summary>
        /// Delegate that gets the struct the section accesses.
        /// </summary>
        /// <returns>Pointer to unmanaged ConfigBase.</returns>
        internal unsafe delegate ConfigBase* GetConfigBaseDelegate();

        /// <summary>
        /// Gets the number of config entries contained within the section.
        /// Some entries may be empty with no data.
        /// </summary>
        public unsafe uint ConfigCount => this.GetConfigBase()->ConfigCount;

        /// <summary>
        /// Gets the name of the config section.
        /// </summary>
        public string SectionName { get; }

        private GetConfigBaseDelegate GetConfigBase { get; }

        /// <summary>
        /// Attempts to get a boolean config option.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">The returned value of the config option.</param>
        /// <returns>A value representing the success.</returns>
        public unsafe bool TryGetBool(string name, out bool value) {
            value = false;
            if (!this.TryGetIndex(name, out var index))
            {
                return false;
            }

            if (!this.TryGetEntry(index, out var entry))
            {
                return false;
            }

            value = entry->Value.UInt != 0;
            return true;
        }

        /// <summary>
        /// Get a boolean config option.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <returns>Value of the config option.</returns>
        /// <exception cref="ConfigOptionNotFoundException">Thrown if the config option is not found.</exception>
        public bool GetBool(string name) {
            if (!this.TryGetBool(name, out var value))
            {
                throw new ConfigOptionNotFoundException(this.SectionName, name);
            }

            return value;
        }

        /// <summary>
        /// Set a boolean config option.
        /// Note: Not all config options will be be immediately reflected in the game.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">New value of the config option.</param>
        /// <exception cref="ConfigOptionNotFoundException">Throw if the config option is not found.</exception>
        /// <exception cref="UnreachableException">Thrown if the name of the config option is found, but the struct was not.</exception>
        public unsafe void Set(string name, bool value) {
            if (!this.TryGetIndex(name, out var index))
            {
                throw new ConfigOptionNotFoundException(this.SectionName, name);
            }

            if (!this.TryGetEntry(index, out var entry))
            {
                throw new UnreachableException($"An unexpected error was encountered setting {name} in {this.SectionName}");
            }

            if ((ConfigType)entry->Type != ConfigType.UInt)
            {
                throw new IncorrectConfigTypeException(this.SectionName, name, (ConfigType)entry->Type, ConfigType.UInt);
            }

            entry->SetValue(value ? 1U : 0U);
        }

        /// <summary>
        /// Attempts to get an unsigned integer config value.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">The returned value of the config option.</param>
        /// <returns>A value representing the success.</returns>
        public unsafe bool TryGetUInt(string name, out uint value) {
            value = 0;
            if (!this.TryGetIndex(name, out var index))
            {
                return false;
            }

            if (!this.TryGetEntry(index, out var entry))
            {
                return false;
            }

            value = entry->Value.UInt;
            return true;
        }

        /// <summary>
        /// Get an unsigned integer config option.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <returns>Value of the config option.</returns>
        /// <exception cref="ConfigOptionNotFoundException">Thrown if the config option is not found.</exception>
        public uint GetUInt(string name) {
            if (!this.TryGetUInt(name, out var value))
            {
                throw new ConfigOptionNotFoundException(this.SectionName, name);
            }

            return value;
        }

        /// <summary>
        /// Set an unsigned integer config option.
        /// Note: Not all config options will be be immediately reflected in the game.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">New value of the config option.</param>
        /// <exception cref="ConfigOptionNotFoundException">Throw if the config option is not found.</exception>
        /// <exception cref="UnreachableException">Thrown if the name of the config option is found, but the struct was not.</exception>
        public unsafe void Set(string name, uint value) {
            this.framework.RunOnFrameworkThread(() => {
                if (!this.TryGetIndex(name, out var index))
                {
                    throw new ConfigOptionNotFoundException(this.SectionName, name);
                }

                if (!this.TryGetEntry(index, out var entry))
                {
                    throw new UnreachableException($"An unexpected error was encountered setting {name} in {this.SectionName}");
                }

                if ((ConfigType)entry->Type != ConfigType.UInt)
                {
                    throw new IncorrectConfigTypeException(this.SectionName, name, (ConfigType)entry->Type, ConfigType.UInt);
                }

                entry->SetValue(value);
            });
        }

        /// <summary>
        /// Attempts to get a float config value.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">The returned value of the config option.</param>
        /// <returns>A value representing the success.</returns>
        public unsafe bool TryGetFloat(string name, out float value) {
            value = 0;
            if (!this.TryGetIndex(name, out var index))
            {
                return false;
            }

            if (!this.TryGetEntry(index, out var entry))
            {
                return false;
            }

            value = entry->Value.Float;
            return true;
        }

        /// <summary>
        /// Get a float config option.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <returns>Value of the config option.</returns>
        /// <exception cref="ConfigOptionNotFoundException">Thrown if the config option is not found.</exception>
        public float GetFloat(string name) {
            if (!this.TryGetFloat(name, out var value))
            {
                throw new ConfigOptionNotFoundException(this.SectionName, name);
            }

            return value;
        }

        /// <summary>
        /// Set a float config option.
        /// Note: Not all config options will be be immediately reflected in the game.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">New value of the config option.</param>
        /// <exception cref="ConfigOptionNotFoundException">Throw if the config option is not found.</exception>
        /// <exception cref="UnreachableException">Thrown if the name of the config option is found, but the struct was not.</exception>
        public unsafe void Set(string name, float value) {
            this.framework.RunOnFrameworkThread(() => {
                if (!this.TryGetIndex(name, out var index))
                {
                    throw new ConfigOptionNotFoundException(this.SectionName, name);
                }

                if (!this.TryGetEntry(index, out var entry))
                {
                    throw new UnreachableException($"An unexpected error was encountered setting {name} in {this.SectionName}");
                }

                if ((ConfigType)entry->Type != ConfigType.Float)
                {
                    throw new IncorrectConfigTypeException(this.SectionName, name, (ConfigType)entry->Type, ConfigType.Float);
                }

                entry->SetValue(value);
            });
        }

        /// <summary>
        /// Attempts to get a string config value.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">The returned value of the config option.</param>
        /// <returns>A value representing the success.</returns>
        public unsafe bool TryGetString(string name, out string value) {
            value = string.Empty;
            if (!this.TryGetIndex(name, out var index))
            {
                return false;
            }

            if (!this.TryGetEntry(index, out var entry))
            {
                return false;
            }

            if (entry->Type != 4)
            {
                return false;
            }

            if (entry->Value.String == null)
            {
                return false;
            }

            value = entry->Value.String->ToString();
            return true;
        }

        /// <summary>
        /// Get a string config option.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <returns>Value of the config option.</returns>
        /// <exception cref="ConfigOptionNotFoundException">Thrown if the config option is not found.</exception>
        public string GetString(string name) {
            if (!this.TryGetString(name, out var value))
            {
                throw new ConfigOptionNotFoundException(this.SectionName, name);
            }

            return value;
        }

        /// <summary>
        /// Set a string config option.
        /// Note: Not all config options will be be immediately reflected in the game.
        /// </summary>
        /// <param name="name">Name of the config option.</param>
        /// <param name="value">New value of the config option.</param>
        /// <exception cref="ConfigOptionNotFoundException">Throw if the config option is not found.</exception>
        /// <exception cref="UnreachableException">Thrown if the name of the config option is found, but the struct was not.</exception>
        public unsafe void Set(string name, string value) {
            this.framework.RunOnFrameworkThread(() => {
                if (!this.TryGetIndex(name, out var index))
                {
                    throw new ConfigOptionNotFoundException(this.SectionName, name);
                }

                if (!this.TryGetEntry(index, out var entry))
                {
                    throw new UnreachableException($"An unexpected error was encountered setting {name} in {this.SectionName}");
                }

                if ((ConfigType)entry->Type != ConfigType.String)
                {
                    throw new IncorrectConfigTypeException(this.SectionName, name, (ConfigType)entry->Type, ConfigType.String);
                }

                entry->SetValue(value);
            });
        }

        private unsafe bool TryGetIndex(string name, out uint index) {
            if (this.indexMap.TryGetValue(name, out index))
            {
                return true;
            }

            var configBase = this.GetConfigBase();
            var e = configBase->ConfigEntry;
            for (var i = 0U; i < configBase->ConfigCount; i++, e++) {
                if (e->Name == null)
                {
                    continue;
                }

                var eName = MemoryHelper.ReadStringNullTerminated(new IntPtr(e->Name));
                if (eName.Equals(name)) {
                    this.indexMap.TryAdd(name, i);
                    this.nameMap.TryAdd(i, name);
                    index = i;
                    return true;
                }
            }

            index = 0;
            return false;
        }

        private unsafe bool TryGetEntry(uint index, out ConfigEntry* entry) {
            entry = null;
            var configBase = this.GetConfigBase();
            if (configBase->ConfigEntry == null || index >= configBase->ConfigCount)
            {
                return false;
            }

            entry = configBase->ConfigEntry;
            entry += index;
            return true;
        }
    }
}
