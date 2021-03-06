// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Settings;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace Microsoft.PythonTools.Interpreter {
    /// <summary>
    /// Finds all the interpreters located under the current workspace folder,
    /// as well as those referenced from the curent workspace folder settings.
    /// </summary>
    [InterpreterFactoryId(FactoryProviderName)]
    [Export(typeof(IPythonInterpreterFactoryProvider))]
    [Export(typeof(WorkspaceInterpreterFactoryProvider))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    class WorkspaceInterpreterFactoryProvider : IPythonInterpreterFactoryProvider, IDisposable {
        private readonly IVsFolderWorkspaceService _workspaceService;
        private IWorkspace _workspace;
        private IWorkspaceSettingsManager _workspaceSettingsMgr;
        private readonly Dictionary<string, PythonInterpreterInformation> _factories = new Dictionary<string, PythonInterpreterInformation>();
        internal const string FactoryProviderName = WorkspaceInterpreterFactoryConstants.FactoryProviderName;
        private FileSystemWatcher _folderWatcher;
        private Timer _folderWatcherTimer;
        private bool _pythonExecutableCreated;
        private int _ignoreNotifications;
        private bool _initialized;

        private static readonly Version[] ExcludedVersions = new[] {
            new Version(2, 5),
            new Version(3, 0)
        };

        internal event EventHandler DiscoveryStarted;

        [ImportingConstructor]
        public WorkspaceInterpreterFactoryProvider(
            [Import] IVsFolderWorkspaceService workspaceService = null
        ) {
            _workspaceService = workspaceService;
            _workspaceService.OnActiveWorkspaceChanged += OnActiveWorkspaceChanged;
        }

        protected void Dispose(bool disposing) {
            if (disposing) {
                _workspaceService.OnActiveWorkspaceChanged -= OnActiveWorkspaceChanged;
                if (_workspaceSettingsMgr != null) {
                    _workspaceSettingsMgr.OnWorkspaceSettingsChanged -= OnSettingsChanged;
                }
                _folderWatcher?.Dispose();
                _folderWatcherTimer?.Dispose();
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~WorkspaceInterpreterFactoryProvider() {
            Dispose(false);
        }

        private void EnsureInitialized() {
            lock (_factories) {
                if (!_initialized) {
                    _initialized = true;
                    InitializeWorkspace();
                    DiscoverInterpreterFactories();
                }
            }
        }

        private Task OnActiveWorkspaceChanged(object sender, EventArgs e) {
            InitializeWorkspace();
            DiscoverInterpreterFactories();
            return Task.CompletedTask;
        }

        private void InitializeWorkspace() {
            lock (_factories) {
                // Cleanup state associated with the previous workspace, if any
                if (_workspaceSettingsMgr != null) {
                    _workspaceSettingsMgr.OnWorkspaceSettingsChanged -= OnSettingsChanged;
                }

                _folderWatcher?.Dispose();
                _folderWatcher = null;
                _folderWatcherTimer?.Dispose();
                _folderWatcherTimer = null;

                // Setup new workspace
                _workspace = _workspaceService.CurrentWorkspace;
                _workspaceSettingsMgr = _workspace?.GetSettingsManager();
                if (_workspaceSettingsMgr != null) {
                    _workspaceSettingsMgr.OnWorkspaceSettingsChanged += OnSettingsChanged;
                }

                if (_workspace != null) {
                    try {
                        _folderWatcher = new FileSystemWatcher(_workspace.Location, "*.*");
                        _folderWatcher.Created += OnFileCreated;
                        _folderWatcher.EnableRaisingEvents = true;
                        _folderWatcher.IncludeSubdirectories = true;
                    } catch (ArgumentException) {
                    } catch (IOException) {
                    }
                    _folderWatcherTimer = new Timer(OnFileChangesTimerElapsed);
                }
            }
        }

        private Task OnSettingsChanged(object sender, EventArgs e) {
            DiscoverInterpreterFactories();
            return Task.CompletedTask;
        }

        internal void DiscoverInterpreterFactories() {
            if (Volatile.Read(ref _ignoreNotifications) > 0) {
                return;
            }

            DiscoveryStarted?.Invoke(this, EventArgs.Empty);

            // Discover the available interpreters...
            bool anyChanged = false;

            IWorkspace workspace = null;
            lock (_factories) {
                workspace = _workspace;
            }

            List<PythonInterpreterInformation> found;
            try {
                found = FindWorkspaceInterpreters(workspace)
                    .Where(i => !ExcludedVersions.Contains(i.Configuration.Version))
                    .ToList();
            } catch (ObjectDisposedException) {
                // We are aborting, so silently return with no results.
                return;
            }

            var uniqueIds = new HashSet<string>(found.Select(i => i.Configuration.Id));

            // Then update our cached state with the lock held.
            lock (_factories) {
                foreach (var info in found) {
                    PythonInterpreterInformation existingInfo;
                    if (!_factories.TryGetValue(info.Configuration.Id, out existingInfo) ||
                        info.Configuration != existingInfo.Configuration) {

                        _factories[info.Configuration.Id] = info;
                        anyChanged = true;
                    }
                }

                // Remove any factories we had before and no longer see...
                foreach (var unregistered in _factories.Keys.Except(uniqueIds).ToArray()) {
                    _factories.Remove(unregistered);
                    anyChanged = true;
                }
            }

            if (anyChanged) {
                OnInterpreterFactoriesChanged();
            }
        }

        private static IEnumerable<PythonInterpreterInformation> FindWorkspaceInterpreters(IWorkspace workspace) {
            var found = new List<PythonInterpreterInformation>();

            if (workspace != null) {
                // First look in workspace subfolders
                found.AddRange(FindInterpretersInSubFolders(workspace.Location).Where(p => p != null));

                // Then look at the currently set interpreter path,
                // because it may point to a folder outside of the workspace,
                // or in a deep subfolder that we don't look into.
                var interpreter = workspace.GetInterpreter();
                if (PathUtils.IsValidPath(interpreter) && !Path.IsPathRooted(interpreter)) {
                    interpreter = workspace.MakeRooted(interpreter);
                }

                // Make sure it wasn't already discovered
                if (File.Exists(interpreter) && !found.Any(p => PathUtils.IsSamePath(p.Configuration.InterpreterPath, interpreter))) {
                    var info = CreateEnvironmentInfo(interpreter);
                    if (info != null) {
                        found.Add(info);
                    }
                }
            }

            return found;
        }

        private static IEnumerable<PythonInterpreterInformation> FindInterpretersInSubFolders(string workspaceFolder) {
            foreach (var dir in PathUtils.EnumerateDirectories(workspaceFolder, recurse: false)) {
                var file = PathUtils.FindFile(dir, "python.exe", depthLimit: 1);
                if (!string.IsNullOrEmpty(file)) {
                    yield return CreateEnvironmentInfo(file);
                }
            }
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e) {
            lock (_factories) {
                try {
                    if (string.Compare(Path.GetFileName(e.FullPath), "python.exe", StringComparison.OrdinalIgnoreCase) == 0) {
                        _pythonExecutableCreated = true;
                    }
                    _folderWatcherTimer?.Change(1000, Timeout.Infinite);
                } catch (ObjectDisposedException) {
                }
            }
        }

        private void OnFileChangesTimerElapsed(object state) {
            try {
                bool shouldDiscover;

                lock (_factories) {
                    _folderWatcherTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    shouldDiscover = _pythonExecutableCreated;
                    _pythonExecutableCreated = false;
                }

                if (shouldDiscover) {
                    DiscoverInterpreterFactories();
                }
            } catch (ObjectDisposedException) {
            }
        }

        private static PythonInterpreterInformation CreateEnvironmentInfo(string interpreterPath) {
            if (!File.Exists(interpreterPath)) {
                return null;
            }

            var prefixPath = PrefixFromSysPrefix(interpreterPath);
            if (prefixPath == null) {
                return null;
            }

            var arch = CPythonInterpreterFactoryProvider.ArchitectureFromExe(interpreterPath);
            var version = CPythonInterpreterFactoryProvider.VersionFromSysVersionInfo(interpreterPath);

            var name = Path.GetFileName(prefixPath);
            var description = name;
            var vendor = Strings.WorkspaceEnvironmentDescription;
            var vendorUrl = string.Empty;
            var supportUrl = string.Empty;
            var windowsInterpreterPath = Path.Combine(Path.GetDirectoryName(interpreterPath), WorkspaceInterpreterFactoryConstants.WindowsExecutable);
            if (!File.Exists(windowsInterpreterPath)) {
                windowsInterpreterPath = string.Empty;
            }

            var config = new InterpreterConfiguration(
                WorkspaceInterpreterFactoryConstants.GetInterpreterId(WorkspaceInterpreterFactoryConstants.EnvironmentCompanyName, name),
                description,
                prefixPath,
                interpreterPath,
                windowsInterpreterPath,
                WorkspaceInterpreterFactoryConstants.PathEnvironmentVariableName,
                arch,
                version,
                InterpreterUIMode.CannotBeDefault | InterpreterUIMode.CannotBeConfigured
            );

            config.SwitchToFullDescription();

            var unique = new PythonInterpreterInformation(
                config,
                vendor,
                vendorUrl,
                supportUrl
            );
            return unique;
        }

        private static string PrefixFromSysPrefix(string interpreterPath) {
            // Interpreter executable may be under scripts folder (ex: virtual envs)
            // or directly in the prefix path folder (ex: installed env)
            using (var output = ProcessOutput.RunHiddenAndCapture(
                interpreterPath, "-c", "import sys; print(sys.prefix)"
            )) {
                output.Wait();
                if (output.ExitCode == 0) {
                    var result = output.StandardOutputLines.FirstOrDefault() ?? "";
                    if (Directory.Exists(result)) {
                        return result;
                    }
                }
            }

            return null;
        }

        #region IPythonInterpreterProvider Members

        public IEnumerable<InterpreterConfiguration> GetInterpreterConfigurations() {
            EnsureInitialized();

            lock (_factories) {
                return _factories.Values.Select(x => x.Configuration).ToArray();
            }
        }

        public IPythonInterpreterFactory GetInterpreterFactory(string id) {
            EnsureInitialized();

            PythonInterpreterInformation info;
            lock (_factories) {
                _factories.TryGetValue(id, out info);
            }

            return info?.GetOrCreateFactory(CreateFactory);
        }

        private static IPythonInterpreterFactory CreateFactory(PythonInterpreterInformation info) {
            return InterpreterFactoryCreator.CreateInterpreterFactory(
                info.Configuration,
                new InterpreterFactoryCreationOptions {
                    WatchFileSystem = true,
                }
            );
        }

        private EventHandler _interpFactoriesChanged;
        public event EventHandler InterpreterFactoriesChanged {
            add {
                EnsureInitialized();
                _interpFactoriesChanged += value;
            }
            remove {
                _interpFactoriesChanged -= value;
            }
        }

        private void OnInterpreterFactoriesChanged() {
            _interpFactoriesChanged?.Invoke(this, EventArgs.Empty);
        }

        public object GetProperty(string id, string propName) {
            PythonInterpreterInformation info;

            switch (propName) {
                case PythonRegistrySearch.CompanyPropertyKey:
                    lock (_factories) {
                        if (_factories.TryGetValue(id, out info)) {
                            return info.Vendor;
                        }
                    }
                    break;
                case "PersistInteractive":
                    return true;
            }

            return null;
        }

        #endregion

        private sealed class DiscoverOnDispose : IDisposable {
            private readonly WorkspaceInterpreterFactoryProvider _provider;

            public DiscoverOnDispose(WorkspaceInterpreterFactoryProvider provider) {
                _provider = provider;
                Interlocked.Increment(ref _provider._ignoreNotifications);
            }

            public void Dispose() {
                if (Interlocked.Decrement(ref _provider._ignoreNotifications) == 0) {
                    _provider.DiscoverInterpreterFactories();
                }
            }
        }

        internal IDisposable SuppressDiscoverFactories() {
            return new DiscoverOnDispose(this);
        }
    }
}
