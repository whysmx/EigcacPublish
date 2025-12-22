using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PublishExtension.Options;

namespace PublishExtension.Commands
{
    internal sealed class PublishCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("03000478-b1b7-4e82-9211-4a682be19a8c");
        private const string PublishProfileName = "ARM64";

        private readonly AsyncPackage package;

        private PublishCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            _ = new PublishCommand(package, commandService);
        }

        private async void Execute(object sender, EventArgs e)
        {
            try
            {
                await ExecuteAsync();
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var options = (PublishOptions)package.GetDialogPage(typeof(PublishOptions));
                LogDebug(options.EnableDebugLogging, $"执行失败: {ex}");
                ShowMessage($"执行失败: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte?.Solution == null || string.IsNullOrWhiteSpace(dte.Solution.FullName))
            {
                ShowMessage("请先打开需要发布的解决方案。", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
            if (string.IsNullOrWhiteSpace(solutionDir))
            {
                ShowMessage("无法获取解决方案目录。", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var options = (PublishOptions)package.GetDialogPage(typeof(PublishOptions));
            var debugEnabled = options.EnableDebugLogging;
            LogDebug(debugEnabled, $"开始发布，解决方案目录: {solutionDir}");

            var backendProject = Path.Combine(solutionDir, "Eigcac.Main", "Eigcac.Main.csproj");
            var frontendProject = Path.Combine(solutionDir, "Eigcac.BSServer", "Eigcac.BSServer.csproj");
            LogDebug(debugEnabled, $"后端项目路径: {backendProject}");
            LogDebug(debugEnabled, $"前端项目路径: {frontendProject}");

            if (!File.Exists(backendProject) || !File.Exists(frontendProject))
            {
                ShowMessage("未找到 Eigcac.Main 或 Eigcac.BSServer 项目文件。", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var publishPath = await GetOrPromptPublishPathAsync();
            if (string.IsNullOrWhiteSpace(publishPath))
            {
                LogDebug(debugEnabled, "发布路径为空，取消发布。");
                return;
            }

            LogDebug(debugEnabled, $"发布路径: {publishPath}");
            var result = await Task.Run(() => PublishAll(solutionDir, backendProject, frontendProject, publishPath, debugEnabled));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!result.Success)
            {
                LogDebug(debugEnabled, $"发布失败: {result.Message}");
                ShowMessage(result.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            LogDebug(debugEnabled, "发布完成。");
            ShowMessage("发布完成。", OLEMSGICON.OLEMSGICON_INFO);
        }

        private async Task<string> GetOrPromptPublishPathAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var options = (PublishOptions)package.GetDialogPage(typeof(PublishOptions));
            var currentPath = options.BackendPublishPath;

            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                var useSaved = VsShellUtilities.ShowMessageBox(
                    package,
                    $"使用已保存的发布路径？\n{currentPath}\n选择“否”可重新选择。",
                    "发布",
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGICON.OLEMSGICON_QUERY,
                    0);

                if (useSaved == (int)VSConstants.MessageBoxResult.IDYES)
                {
                    return currentPath;
                }
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择后端发布输出目录";
                dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return null;
                }

                options.BackendPublishPath = dialog.SelectedPath;
                options.SaveSettingsToStorage();
                return dialog.SelectedPath;
            }
        }

        private static PublishResult PublishAll(
            string solutionDir,
            string backendProject,
            string frontendProject,
            string backendPublishPath,
            bool debugEnabled)
        {
            LogDebug(debugEnabled, "开始执行 TFS 更新。");
            var tfsResult = RunProcess("tf", "get /recursive /noprompt", solutionDir, debugEnabled);
            if (!tfsResult.Success)
            {
                return PublishResult.Fail($"TFS 更新失败:\n{tfsResult.Message}");
            }

            Directory.CreateDirectory(backendPublishPath);

            var backendPublishDir = EnsureTrailingSeparator(backendPublishPath);
            var backendArgs = $"publish \"{backendProject}\" -p:PublishProfile={PublishProfileName} -p:PublishDir=\"{backendPublishDir}\"";
            LogDebug(debugEnabled, $"开始发布后端: {backendArgs}");
            var backendResult = RunProcess("dotnet", backendArgs, solutionDir, debugEnabled);
            if (!backendResult.Success)
            {
                return PublishResult.Fail($"后端发布失败:\n{backendResult.Message}");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"EigcacBSServerPublish_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var frontendPublishDir = EnsureTrailingSeparator(tempDir);
                var frontendArgs = $"publish \"{frontendProject}\" -p:PublishProfile={PublishProfileName} -p:PublishDir=\"{frontendPublishDir}\"";
                LogDebug(debugEnabled, $"开始发布前端: {frontendArgs}");
                var frontendResult = RunProcess("dotnet", frontendArgs, solutionDir, debugEnabled);
                if (!frontendResult.Success)
                {
                    return PublishResult.Fail($"前端发布失败:\n{frontendResult.Message}");
                }

                var frontendTarget = Path.Combine(backendPublishPath, "BSServer");
                LogDebug(debugEnabled, $"清理前端目标目录: {frontendTarget}");
                ClearDirectory(frontendTarget);
                LogDebug(debugEnabled, $"复制前端产物: {tempDir} -> {frontendTarget}");
                CopyDirectory(tempDir, frontendTarget);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        LogDebug(debugEnabled, $"清理临时目录: {tempDir}");
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            return PublishResult.Successful();
        }

        private static void ClearDirectory(string targetDir)
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                return;
            }

            var directoryInfo = new DirectoryInfo(targetDir);
            foreach (var file in directoryInfo.GetFiles())
            {
                file.IsReadOnly = false;
                file.Delete();
            }

            foreach (var dir in directoryInfo.GetDirectories())
            {
                dir.Delete(true);
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                var targetSubDir = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, targetSubDir);
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            if (!path.EndsWith(separator, StringComparison.Ordinal))
            {
                return path + separator;
            }

            return path;
        }

        private static ProcessResult RunProcess(string fileName, string arguments, string workingDir, bool debugEnabled)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = psi })
                {
                    LogDebug(debugEnabled, $"启动进程: {fileName} {arguments}");
                    if (!process.Start())
                    {
                        return ProcessResult.Fail($"无法启动进程: {fileName}");
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        var message = string.IsNullOrWhiteSpace(error) ? output : error;
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            message = $"{fileName} 返回码: {process.ExitCode}";
                        }
                        return ProcessResult.Fail(message);
                    }

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        LogDebug(debugEnabled, $"{fileName} 输出:\n{output}");
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        LogDebug(debugEnabled, $"{fileName} 错误输出:\n{error}");
                    }

                    return ProcessResult.Successful();
                }
            }
            catch (Exception ex)
            {
                return ProcessResult.Fail($"启动 {fileName} 失败: {ex.Message}");
            }
        }

        private static void LogDebug(bool debugEnabled, string message)
        {
            if (!debugEnabled || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                ActivityLog.LogInformation("PublishExtension", message);
            }
            catch
            {
                // Ignore logging errors.
            }
        }

        private void ShowMessage(string message, OLEMSGICON icon)
        {
            VsShellUtilities.ShowMessageBox(
                package,
                message,
                "发布",
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                icon,
                0);
        }

        private sealed class PublishResult
        {
            private PublishResult(bool success, string message)
            {
                Success = success;
                Message = message;
            }

            public bool Success { get; }
            public string Message { get; }

            public static PublishResult Successful() => new PublishResult(true, string.Empty);
            public static PublishResult Fail(string message) => new PublishResult(false, message);
        }

        private sealed class ProcessResult
        {
            private ProcessResult(bool success, string message)
            {
                Success = success;
                Message = message;
            }

            public bool Success { get; }
            public string Message { get; }

            public static ProcessResult Successful() => new ProcessResult(true, string.Empty);
            public static ProcessResult Fail(string message) => new ProcessResult(false, message);
        }
    }
}
