//
//  PluginService.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using Remora.Plugins.Abstractions;
using Remora.Plugins.Abstractions.Attributes;
using Remora.Plugins.Errors;
using Remora.Results;

namespace Remora.Plugins.Services;

/// <summary>
/// Serves functionality related to plugins.
/// </summary>
[PublicAPI]
public sealed class PluginService : IDisposable
{
    private readonly PluginServiceOptions _options;
    private readonly PluginLoader _pluginLoader;
    private readonly PluginServiceProviderList _pluginServiceProviderList;

    // So the plugin Descriptors can be disposed of when trying to unload plugins.
    private readonly Dictionary<string, IPluginDescriptor> _pluginDescriptors;
    private FileSystemWatcher? _fileSystemWatcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginService"/> class.
    /// </summary>
    /// <param name="applicationProvider">
    /// The application's service provider.
    /// This is for fallback when all of the plugin providers do not contain a particular service.
    /// </param>
    public PluginService(IServiceProvider applicationProvider)
        : this(PluginServiceOptions.Default, applicationProvider)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginService"/> class.
    /// </summary>
    /// <param name="options">
    /// The service options, wrapped in an <see cref="IOptions{TOptions}"/>.
    /// </param>
    /// <param name="applicationProvider">
    /// The application's service provider.
    /// This is for fallback when all of the plugin providers do not contain a particular service.
    /// </param>
    [Obsolete("Prefer overload which accepts PluginServiceOptions directly.")]
    public PluginService(IOptions<PluginServiceOptions> options, IServiceProvider applicationProvider)
        : this(options.Value, applicationProvider)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginService"/> class.
    /// </summary>
    /// <param name="options">
    /// The service options.
    /// </param>
    /// <param name="applicationProvider">
    /// The application's service provider.
    /// This is for fallback when all of the plugin providers do not contain a particular service.
    /// </param>
    public PluginService(PluginServiceOptions options, IServiceProvider applicationProvider)
    {
        _options = options;
        _pluginLoader = new PluginLoader();
        _pluginDescriptors = new Dictionary<string, IPluginDescriptor>();
        _pluginServiceProviderList = new PluginServiceProviderList(applicationProvider);
    }

    /// <summary>
    /// Loads all available plugins with a specific filter and watches them for any changes.
    /// </summary>
    /// <param name="filter">The filter for files to watch within the watcher.</param>
    public void LoadPlugins(string filter)
    {
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var path = string.Empty;
        if (entryAssemblyPath is not null)
        {
            var installationDirectory = Directory.GetParent(entryAssemblyPath)
                                        ?? throw new InvalidOperationException();
            path = installationDirectory.FullName;
        }
        _fileSystemWatcher = new FileSystemWatcher(path, filter)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes
                | NotifyFilters.CreationTime
                | NotifyFilters.DirectoryName
                | NotifyFilters.FileName
                | NotifyFilters.LastAccess
                | NotifyFilters.LastWrite
                | NotifyFilters.Security
                | NotifyFilters.Size,
        };
        _fileSystemWatcher.Changed += (_, e) =>
        {
            // Unload old plugin (file changed).
            IPluginDescriptor? pluginDescriptor = _pluginDescriptors.GetValueOrDefault(
                Path.GetFileNameWithoutExtension(e.FullPath));
            _pluginDescriptors.Remove(
                Path.GetFileNameWithoutExtension(e.FullPath));
            Task.Run(async () =>
            {
                await pluginDescriptor?.StopAsync()!;
                pluginDescriptor?.Dispose();
            }).GetAwaiter().GetResult();
            UnloadPlugin(Path.GetFileNameWithoutExtension(e.FullPath));

            // Load new one.
            _pluginDescriptors.Add(
                Path.GetFileNameWithoutExtension(e.FullPath),
                LoadPlugin(e.FullPath).Entity);
        };
        _fileSystemWatcher.Created += (_, e) =>

            // Load Plugin (file created).
            _pluginDescriptors.Add(
                Path.GetFileNameWithoutExtension(e.FullPath),
                LoadPlugin(e.FullPath).Entity);
        _fileSystemWatcher.Deleted += (_, e) =>
        {
            // Unload plugin (file deleted).
            IPluginDescriptor? pluginDescriptor = _pluginDescriptors.GetValueOrDefault(
                Path.GetFileNameWithoutExtension(e.FullPath));
            _pluginDescriptors.Remove(
                Path.GetFileNameWithoutExtension(e.FullPath));
            Task.Run(async () =>
            {
                await pluginDescriptor?.StopAsync()!;
                pluginDescriptor?.Dispose();
            }).GetAwaiter().GetResult();
            UnloadPlugin(Path.GetFileNameWithoutExtension(e.FullPath));
        };
        _fileSystemWatcher.Renamed += (_, e) =>
        {
            // Unload old plugin (file renamed).
            IPluginDescriptor? pluginDescriptor = _pluginDescriptors.GetValueOrDefault(
                Path.GetFileNameWithoutExtension(e.OldFullPath));
            _pluginDescriptors.Remove(
                Path.GetFileNameWithoutExtension(e.OldFullPath));
            Task.Run(async () =>
            {
                await pluginDescriptor?.StopAsync()!;
                pluginDescriptor?.Dispose();
            }).GetAwaiter().GetResult();
            UnloadPlugin(Path.GetFileNameWithoutExtension(e.OldFullPath));

            // Load new one.
            _pluginDescriptors.Add(
                Path.GetFileNameWithoutExtension(e.FullPath),
                LoadPlugin(e.FullPath).Entity);
        };
        var assemblyPaths = GetPluginAssemblyPaths();
        foreach (var assemblyPath in assemblyPaths)
        {
            _pluginDescriptors.Add(
                Path.GetFileNameWithoutExtension(assemblyPath),
                LoadPlugin(assemblyPath).Entity);
        }
    }

    /// <summary>
    /// Gets the search paths to plugin assemblies.
    /// </summary>
    /// <returns>
    /// The search paths to plugin assemblies.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// When the parent directory is null.
    /// </exception>
    [Pure]
    private IEnumerable<string> GetPluginAssemblyPaths()
    {
        var searchPaths = new List<string>();

        if (_options.ScanAssemblyDirectory)
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;

            if (entryAssemblyPath is not null)
            {
                var installationDirectory = Directory.GetParent(entryAssemblyPath)
                                            ?? throw new InvalidOperationException();

                searchPaths.Add(installationDirectory.FullName);
            }
        }

        searchPaths.AddRange(_options.PluginSearchPaths);

        return searchPaths.Select
        (
            searchPath => Directory.EnumerateFiles
            (
                searchPath,
                "*.dll",
                SearchOption.AllDirectories
            )
        ).SelectMany(a => a);
    }

    /// <summary>
    /// Loads a specific plugin and it's services.
    /// </summary>
    /// <param name="assemblyPath">
    /// The path to the plugin assembly to load.
    /// </param>
    /// <returns>
    /// The loaded plugin's descriptor instance.
    /// </returns>
    [Pure]
    private Result<IPluginDescriptor> LoadPlugin(string assemblyPath)
    {
        RemoraPluginAttribute pluginAttribute;
        (pluginAttribute, _) = _pluginLoader.LoadPlugin(assemblyPath);
        var descriptor = (IPluginDescriptor?)Activator.CreateInstance
        (
            pluginAttribute.PluginDescriptor
        );
        if (descriptor is null)
        {
            return default;
        }
        _pluginServiceProviderList.CreateProvider(
            Path.GetFileNameWithoutExtension(assemblyPath),
            descriptor.Services);
        var startResult = Task.Run(() => descriptor.StartAsync()).GetAwaiter().GetResult();
        if (!startResult.IsSuccess)
        {
            return new PluginInitializationFailed(descriptor, startResult.Error.Message);
        }

        return Result<IPluginDescriptor>.FromSuccess(descriptor);
    }

    /// <summary>
    /// Unloads a specific plugin and it's services.
    /// </summary>
    /// <param name="pluginName">The plugin to unload.</param>
    /// <remarks>
    /// Note: Unload the Plugin's Descriptor before calling this
    /// otherwise unloading the plugin might fail.
    /// </remarks>
    private void UnloadPlugin(string pluginName)
    {
        // first dispose of the plugin's created service provider.
        _pluginServiceProviderList.DisposeProvider(pluginName);

        // now we unload the plugin.
        _pluginLoader.UnloadPlugin(pluginName);
    }

    /// <inheritdoc />
    public void Dispose()
        => _fileSystemWatcher?.Dispose();
}
