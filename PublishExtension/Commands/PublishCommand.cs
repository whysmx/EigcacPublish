using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
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
        public const int CommandProjectId = 0x0101;
        public const int CommandSolutionId = 0x0102;
        public static readonly Guid CommandSet = new Guid("03000478-b1b7-4e82-9211-4a682be19a8c");
        private const string PublishProfileName = "ARM64";

        private static PublishCommand instance;
        private readonly AsyncPackage package;

        private PublishCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            instance = this;

            AddCommand(commandService, CommandId);
            AddCommand(commandService, CommandProjectId);
            AddCommand(commandService, CommandSolutionId);
        }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                await LogOutputAsync(package, "命令服务为空，无法注册命令。");
                return;
            }

            _ = new PublishCommand(package, commandService);
            await LogOutputAsync(package, "命令已注册：Eigcac发布");
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
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
            });
        }

        public static void Trigger()
        {
            instance?.Execute(null, EventArgs.Empty);
        }

        private void AddCommand(OleMenuCommandService commandService, int commandId)
        {
            var menuCommandID = new CommandID(CommandSet, commandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandID);
            menuItem.Visible = true;
            menuItem.Enabled = true;
            menuItem.BeforeQueryStatus += (_, __) =>
            {
                menuItem.Visible = true;
                menuItem.Enabled = true;
                _ = LogOutputAsync(package, $"命令状态检查: {commandId} (Visible={menuItem.Visible}, Enabled={menuItem.Enabled})");
            };
            commandService.AddCommand(menuItem);
            _ = LogOutputAsync(package, $"已添加命令: {commandId}");
        }

        private async System.Threading.Tasks.Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await package.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE2;
            if (dte == null)
            {
                await LogOutputAsync(package, "DTE 服务不可用，无法执行发布。");
                ShowMessage("无法获取 DTE 服务。", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            if (dte.Solution == null || string.IsNullOrWhiteSpace(dte.Solution.FullName))
            {
                await LogOutputAsync(package, "未检测到已打开的解决方案。");
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
            await LogOutputAsync(package, $"开始发布，解决方案目录: {solutionDir}");

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
                await LogOutputAsync(package, "发布路径为空，已取消。");
                return;
            }

            LogDebug(debugEnabled, $"发布路径: {publishPath}");
            var tfsUpdateResult = await UpdateFromTfsAsync(solutionDir, options, debugEnabled);
            if (!tfsUpdateResult.Success)
            {
                LogDebug(debugEnabled, $"TFS 更新失败: {tfsUpdateResult.Message}");
                await LogOutputAsync(package, $"TFS 更新失败: {tfsUpdateResult.Message}");
                ShowMessage(tfsUpdateResult.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            var result = await System.Threading.Tasks.Task.Run(() => PublishProjects(solutionDir, backendProject, frontendProject, publishPath, debugEnabled));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!result.Success)
            {
                LogDebug(debugEnabled, $"发布失败: {result.Message}");
                await LogOutputAsync(package, $"发布失败: {result.Message}");
                ShowMessage(result.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            LogDebug(debugEnabled, "发布完成。");
            await LogOutputAsync(package, "发布完成。");
            ShowMessage("发布完成。", OLEMSGICON.OLEMSGICON_INFO);
        }

        private async System.Threading.Tasks.Task<PublishResult> UpdateFromTfsAsync(string solutionDir, PublishOptions options, bool debugEnabled)
        {
            LogDebug(debugEnabled, "开始执行 TFS 更新。");

            var argsResult = await System.Threading.Tasks.Task.Run(() => BuildTfsGetArguments(solutionDir, options, debugEnabled));
            if (!argsResult.Success)
            {
                return PublishResult.Fail(argsResult.Message);
            }

            var tfsResult = await System.Threading.Tasks.Task.Run(() => RunProcess("tf", argsResult.Arguments, solutionDir, debugEnabled));
            if (tfsResult.Success)
            {
                return PublishResult.Successful();
            }

            if (!IsAuthFailure(tfsResult.Message) || options == null)
            {
                return PublishResult.Fail($"TFS 更新失败:\n{tfsResult.Message}");
            }

            await LogOutputAsync(package, "TFS 权限不足，正在请求账号密码。");
            var credentialResult = await PromptForCredentialsAsync(options);
            if (credentialResult == null)
            {
                return PublishResult.Fail($"TFS 更新失败:\n{tfsResult.Message}");
            }

            var retryArgsResult = await System.Threading.Tasks.Task.Run(() => BuildTfsGetArguments(solutionDir, options, debugEnabled));
            if (!retryArgsResult.Success)
            {
                return PublishResult.Fail(retryArgsResult.Message);
            }

            var retryResult = await System.Threading.Tasks.Task.Run(() => RunProcess("tf", retryArgsResult.Arguments, solutionDir, debugEnabled));
            if (!retryResult.Success)
            {
                return PublishResult.Fail($"TFS 更新失败:\n{retryResult.Message}");
            }

            return PublishResult.Successful();
        }

        private async System.Threading.Tasks.Task<CredentialResult> PromptForCredentialsAsync(PublishOptions options)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            using (var dialog = new CredentialsDialog(options?.TfsUsername ?? string.Empty))
            {
                var result = dialog.ShowDialog();
                if (result != DialogResult.OK)
                {
                    return null;
                }

                var username = dialog.Username?.Trim();
                var password = dialog.Password ?? string.Empty;
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    ShowMessage("TFS 用户名或密码为空，已取消。", OLEMSGICON.OLEMSGICON_WARNING);
                    return null;
                }

                if (options != null)
                {
                    options.TfsUsername = username;
                    options.TfsPassword = password;
                    options.SaveSettingsToStorage();
                }

                return new CredentialResult(username, password);
            }
        }

        private static bool IsAuthFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("TF30063", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("没有访问", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("no access", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("not authorized", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async System.Threading.Tasks.Task<string> GetOrPromptPublishPathAsync()
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
                    OLEMSGICON.OLEMSGICON_QUERY,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

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

        private static PublishResult PublishProjects(
            string solutionDir,
            string backendProject,
            string frontendProject,
            string backendPublishPath,
            bool debugEnabled)
        {
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

        private static CommandArgsResult BuildTfsGetArguments(string solutionDir, PublishOptions options, bool debugEnabled)
        {
            var loginArgsResult = BuildTfsLoginArgs(options);
            if (!loginArgsResult.Success)
            {
                return CommandArgsResult.Fail(loginArgsResult.Message);
            }

            var needsDetect = options == null
                || string.IsNullOrWhiteSpace(options.TfsServerPath)
                || string.IsNullOrWhiteSpace(options.TfsCollectionUrl);
            var detection = needsDetect
                ? TryDetectTfsInfo(solutionDir, options, loginArgsResult.Arguments, debugEnabled)
                : new TfsDetectionResult(string.Empty, string.Empty);
            var serverPath = options?.TfsServerPath?.Trim();
            if (string.IsNullOrWhiteSpace(serverPath))
            {
                serverPath = detection.ServerPath;
                if (!string.IsNullOrWhiteSpace(serverPath))
                {
                    LogDebug(debugEnabled, $"检测到 TFS 服务器路径: {serverPath}");
                }
            }

            var collectionUrl = options?.TfsCollectionUrl?.Trim();
            if (string.IsNullOrWhiteSpace(collectionUrl))
            {
                collectionUrl = detection.CollectionUrl;
                if (!string.IsNullOrWhiteSpace(collectionUrl))
                {
                    LogDebug(debugEnabled, $"检测到 TFS 集合地址: {collectionUrl}");
                }
            }

            var baseArgs = string.IsNullOrWhiteSpace(serverPath)
                ? "get /recursive /noprompt"
                : $"get \"{serverPath}\" /recursive /noprompt";

            var finalArgs = baseArgs;
            if (!string.IsNullOrWhiteSpace(collectionUrl))
            {
                finalArgs = $"{finalArgs} /collection:\"{collectionUrl}\"";
            }

            if (!string.IsNullOrWhiteSpace(loginArgsResult.Arguments))
            {
                finalArgs = $"{finalArgs} {loginArgsResult.Arguments}";
            }

            LogDebug(debugEnabled, $"TFS 命令: tf {finalArgs}");
            return CommandArgsResult.Successful(finalArgs);
        }

        private static CommandArgsResult BuildTfsLoginArgs(PublishOptions options)
        {
            if (options == null)
            {
                return CommandArgsResult.Successful(string.Empty);
            }

            var hasUser = !string.IsNullOrWhiteSpace(options.TfsUsername);
            var hasPassword = !string.IsNullOrWhiteSpace(options.TfsPassword);
            if (hasUser || hasPassword)
            {
                if (!hasUser || !hasPassword)
                {
                    return CommandArgsResult.Fail("TFS 用户名或密码未完整配置，请在选项中补全。");
                }

                if (options.TfsUsername.Contains("\"") || options.TfsPassword.Contains("\""))
                {
                    return CommandArgsResult.Fail("TFS 用户名或密码包含双引号，暂不支持。");
                }

                var loginValue = $"{options.TfsUsername},{options.TfsPassword}";
                var loginArg = NeedsQuotes(loginValue)
                    ? $"/login:\"{loginValue}\""
                    : $"/login:{loginValue}";
                return CommandArgsResult.Successful(loginArg);
            }

            return CommandArgsResult.Successful(string.Empty);
        }

        private static TfsDetectionResult TryDetectTfsInfo(string solutionDir, PublishOptions options, string loginArgs, bool debugEnabled)
        {
            var serverPath = string.Empty;
            var collectionUrl = string.Empty;
            var commonArgs = string.Empty;

            if (!string.IsNullOrWhiteSpace(options?.TfsCollectionUrl))
            {
                commonArgs = $"/collection:\"{options.TfsCollectionUrl}\"";
            }

            if (!string.IsNullOrWhiteSpace(loginArgs))
            {
                commonArgs = string.IsNullOrWhiteSpace(commonArgs) ? loginArgs : $"{commonArgs} {loginArgs}";
            }

            var workfoldArgs = $"workfold \"{solutionDir}\" /format:xml /noprompt";
            if (!string.IsNullOrWhiteSpace(commonArgs))
            {
                workfoldArgs = $"{workfoldArgs} {commonArgs}";
            }

            var workfoldResult = RunProcessWithOutput("tf", workfoldArgs, solutionDir, debugEnabled);
            if (workfoldResult.Success)
            {
                serverPath = ExtractServerPathFromWorkfold(workfoldResult.Output);
                collectionUrl = ExtractCollectionUrl(workfoldResult.Output);
            }
            else
            {
                LogDebug(debugEnabled, $"TFS workfold 失败: {workfoldResult.Message}");
            }

            var infoArgs = $"info \"{solutionDir}\" /format:xml /noprompt";
            if (!string.IsNullOrWhiteSpace(commonArgs))
            {
                infoArgs = $"{infoArgs} {commonArgs}";
            }

            var infoResult = RunProcessWithOutput("tf", infoArgs, solutionDir, debugEnabled);
            if (infoResult.Success)
            {
                if (string.IsNullOrWhiteSpace(serverPath))
                {
                    serverPath = ExtractServerPathFromInfo(infoResult.Output);
                }

                if (string.IsNullOrWhiteSpace(collectionUrl))
                {
                    collectionUrl = ExtractCollectionUrl(infoResult.Output);
                }
            }
            else
            {
                LogDebug(debugEnabled, $"TFS info 失败: {infoResult.Message}");
            }

            return new TfsDetectionResult(serverPath, collectionUrl);
        }

        private static string ExtractServerPathFromWorkfold(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var match = Regex.Match(output, "serverItem=\"(\\$/[^\"]+)\"", RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private static string ExtractServerPathFromInfo(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var match = Regex.Match(output, "\\$/[^\"\\s<]+");
            if (!match.Success)
            {
                return string.Empty;
            }

            var path = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(Path.GetExtension(path)))
            {
                var lastSlash = path.LastIndexOf('/');
                if (lastSlash > 1)
                {
                    path = path.Substring(0, lastSlash);
                }
            }

            return path;
        }

        private static string ExtractCollectionUrl(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var patterns = new[]
            {
                "collection=\"(https?://[^\"]+)\"",
                "collectionUri=\"(https?://[^\"]+)\"",
                "teamProjectCollection=\"(https?://[^\"]+)\"",
                "server=\"(https?://[^\"]+/tfs/[^\"]+)\"",
                "uri=\"(https?://[^\"]+/tfs/[^\"]+)\"",
                "(https?://[^\"\\s<]+/tfs/[^\"\\s<]+)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (match.Groups.Count > 1)
                    {
                        return match.Groups[1].Value.Trim();
                    }

                    return match.Value.Trim();
                }
            }

            return string.Empty;
        }

        private static bool NeedsQuotes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.IndexOfAny(new[] { ' ', '\t' }) >= 0;
        }

        private sealed class TfsDetectionResult
        {
            public TfsDetectionResult(string serverPath, string collectionUrl)
            {
                ServerPath = serverPath ?? string.Empty;
                CollectionUrl = collectionUrl ?? string.Empty;
            }

            public string ServerPath { get; }
            public string CollectionUrl { get; }
        }

        private sealed class CredentialResult
        {
            public CredentialResult(string username, string password)
            {
                Username = username ?? string.Empty;
                Password = password ?? string.Empty;
            }

            public string Username { get; }
            public string Password { get; }
        }

        private sealed class CredentialsDialog : Form
        {
            private readonly TextBox usernameBox;
            private readonly TextBox passwordBox;

            public CredentialsDialog(string username)
            {
                Text = "TFS 登录";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ClientSize = new System.Drawing.Size(380, 170);

                var userLabel = new Label
                {
                    Text = "用户名",
                    Left = 20,
                    Top = 20,
                    Width = 70
                };

                usernameBox = new TextBox
                {
                    Left = 100,
                    Top = 18,
                    Width = 250,
                    Text = username ?? string.Empty
                };

                var passLabel = new Label
                {
                    Text = "密码",
                    Left = 20,
                    Top = 60,
                    Width = 70
                };

                passwordBox = new TextBox
                {
                    Left = 100,
                    Top = 58,
                    Width = 250,
                    UseSystemPasswordChar = true
                };

                var okButton = new Button
                {
                    Text = "确定",
                    DialogResult = DialogResult.OK,
                    Left = 190,
                    Width = 75,
                    Top = 110
                };

                var cancelButton = new Button
                {
                    Text = "取消",
                    DialogResult = DialogResult.Cancel,
                    Left = 275,
                    Width = 75,
                    Top = 110
                };

                Controls.Add(userLabel);
                Controls.Add(usernameBox);
                Controls.Add(passLabel);
                Controls.Add(passwordBox);
                Controls.Add(okButton);
                Controls.Add(cancelButton);

                AcceptButton = okButton;
                CancelButton = cancelButton;
            }

            public string Username => usernameBox.Text;
            public string Password => passwordBox.Text;
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

        private static ProcessOutputResult RunProcessWithOutput(string fileName, string arguments, string workingDir, bool debugEnabled)
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
                using (var process = new System.Diagnostics.Process { StartInfo = psi })
                {
                    LogDebug(debugEnabled, $"启动进程: {fileName} {arguments}");
                    if (!process.Start())
                    {
                        return ProcessOutputResult.Fail($"无法启动进程: {fileName}", string.Empty, string.Empty);
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
                        return ProcessOutputResult.Fail(message, output, error);
                    }

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        LogDebug(debugEnabled, $"{fileName} 输出:\n{output}");
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        LogDebug(debugEnabled, $"{fileName} 错误输出:\n{error}");
                    }

                    return ProcessOutputResult.Successful(output, error);
                }
            }
            catch (Exception ex)
            {
                return ProcessOutputResult.Fail($"启动 {fileName} 失败: {ex.Message}", string.Empty, string.Empty);
            }
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
                using (var process = new System.Diagnostics.Process { StartInfo = psi })
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

        private static void LogActivity(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
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

        private static async System.Threading.Tasks.Task LogOutputAsync(AsyncPackage package, string message)
        {
            if (package == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var outputWindow = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow == null)
            {
                LogActivity($"输出窗口不可用: {message}");
                return;
            }

            var paneGuid = new Guid("9C47EA07-7688-4A7C-B2C8-AD5B5B1B2521");
            outputWindow.CreatePane(ref paneGuid, "Eigcac发布", 1, 1);
            outputWindow.GetPane(ref paneGuid, out var pane);
            if (pane != null)
            {
                pane.OutputStringThreadSafe($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
            LogActivity(message);
        }

        private void ShowMessage(string message, OLEMSGICON icon)
        {
            VsShellUtilities.ShowMessageBox(
                package,
                message,
                "发布",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
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

        private sealed class ProcessOutputResult
        {
            private ProcessOutputResult(bool success, string message, string output, string error)
            {
                Success = success;
                Message = message;
                Output = output ?? string.Empty;
                Error = error ?? string.Empty;
            }

            public bool Success { get; }
            public string Message { get; }
            public string Output { get; }
            public string Error { get; }

            public static ProcessOutputResult Successful(string output, string error)
                => new ProcessOutputResult(true, string.Empty, output, error);

            public static ProcessOutputResult Fail(string message, string output, string error)
                => new ProcessOutputResult(false, message, output, error);
        }

        private sealed class CommandArgsResult
        {
            private CommandArgsResult(bool success, string message, string arguments)
            {
                Success = success;
                Message = message;
                Arguments = arguments ?? string.Empty;
            }

            public bool Success { get; }
            public string Message { get; }
            public string Arguments { get; }

            public static CommandArgsResult Successful(string arguments)
                => new CommandArgsResult(true, string.Empty, arguments);

            public static CommandArgsResult Fail(string message)
                => new CommandArgsResult(false, message, string.Empty);
        }
    }
}
