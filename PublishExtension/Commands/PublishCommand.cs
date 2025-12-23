using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Threading;
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

        private static PublishCommand instance;
        private readonly AsyncPackage package;
        private DTE2 dte;
        private readonly SemaphoreSlim publishSemaphore = new SemaphoreSlim(1, 1);

        private PublishCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            instance = this;

            AddCommand(commandService, CommandId);
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
                await ExecuteAsync();
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
            };
            commandService.AddCommand(menuItem);
        }

        private async System.Threading.Tasks.Task ExecuteAsync()
        {
            // 防止重复发布
            if (!await publishSemaphore.WaitAsync(0))
            {
                ShowMessage("发布正在进行中，请稍候。", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (dte == null)
                {
                    dte = await package.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE2;
                    if (dte == null)
                    {
                        ShowMessage("无法获取 DTE 服务。", OLEMSGICON.OLEMSGICON_WARNING);
                        return;
                    }
                }

                if (dte.Solution == null || string.IsNullOrWhiteSpace(dte.Solution.FullName))
                {
                    ShowMessage("请先打开需要发布的解决方案。", OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }

                var options = (PublishOptions)package.GetDialogPage(typeof(PublishOptions));
                var debugEnabled = options?.EnableDebugLogging ?? false;

                LogDebug(debugEnabled, "开始发布");
                await LogOutputAsync(package, "开始发布 Eigcac.Main 和 Eigcac.BSServer...");

                // 获取两个项目
                var backendProject = FindProject("Eigcac.Main");
                var frontendProject = FindProject("Eigcac.BSServer");

                if (backendProject == null)
                {
                    ShowMessage("未找到 Eigcac.Main 项目。", OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }

                if (frontendProject == null)
                {
                    ShowMessage("未找到 Eigcac.BSServer 项目。", OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }

                // 发布后端
                await LogOutputAsync(package, "正在发布 Eigcac.Main...");
                if (!await PublishProjectAsync(backendProject, debugEnabled))
                {
                    ShowMessage("Eigcac.Main 发布失败", OLEMSGICON.OLEMSGICON_CRITICAL);
                    return;
                }

                // 发布前端
                await LogOutputAsync(package, "正在发布 Eigcac.BSServer...");
                if (!await PublishProjectAsync(frontendProject, debugEnabled))
                {
                    ShowMessage("Eigcac.BSServer 发布失败", OLEMSGICON.OLEMSGICON_CRITICAL);
                    return;
                }

                await LogOutputAsync(package, "发布完成");
                ShowMessage("发布完成", OLEMSGICON.OLEMSGICON_INFO);
            }
            finally
            {
                publishSemaphore.Release();
            }
        }

        private Project FindProject(string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Project project in dte.Solution.Projects)
            {
                if (string.Equals(project.Name, projectName, StringComparison.Ordinal))
                    return project;

                // 检查嵌套项目（解决方案文件夹）
                if (project.ProjectItems != null)
                {
                    var nestedProject = FindProjectInProjectItems(project.ProjectItems, projectName);
                    if (nestedProject != null)
                        return nestedProject;
                }
            }

            return null;
        }

        private Project FindProjectInProjectItems(ProjectItems items, string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (ProjectItem item in items)
            {
                if (item.SubProject != null)
                {
                    if (string.Equals(item.SubProject.Name, projectName, StringComparison.Ordinal))
                        return item.SubProject;

                    var nested = FindProjectInProjectItems(item.SubProject.ProjectItems, projectName);
                    if (nested != null)
                        return nested;
                }
            }

            return null;
        }

        private async System.Threading.Tasks.Task<bool> PublishProjectAsync(Project project, bool debugEnabled)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // 获取项目文件路径
                var projectPath = string.Empty;
                if (!string.IsNullOrEmpty(project.FullName))
                {
                    projectPath = project.FullName;
                }
                else if (project.Properties != null)
                {
                    try
                    {
                        var fullPathProp = project.Properties.Item("FullPath");
                        if (fullPathProp != null)
                            projectPath = fullPathProp.Value as string;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(projectPath) || !System.IO.File.Exists(projectPath))
                {
                    await LogOutputAsync(package, $"无法找到项目文件: {project.Name}");
                    return false;
                }

                var solutionDir = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
                var workingDir = System.IO.Path.GetDirectoryName(projectPath);

                await LogOutputAsync(package, $"正在发布 {project.Name} (ARM64)...");

                // 使用 dotnet publish 发布，指定 ARM64 配置
                var args = $"publish \"{projectPath}\" -p:PublishProfile=ARM64 -c Release";

                LogDebug(debugEnabled, $"执行: dotnet {args}");
                LogDebug(debugEnabled, $"工作目录: {workingDir}");

                return await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "dotnet",
                                Arguments = args,
                                WorkingDirectory = workingDir,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            }
                        };

                        process.Start();

                        // 实时读取输出
                        var outputTask = System.Threading.Tasks.Task.Run(() =>
                        {
                            while (!process.StandardOutput.EndOfStream)
                            {
                                var line = process.StandardOutput.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    LogDebug(debugEnabled, $"[{project.Name}] {line}");
                                }
                            }
                        });

                        var errorTask = System.Threading.Tasks.Task.Run(() =>
                        {
                            while (!process.StandardError.EndOfStream)
                            {
                                var line = process.StandardError.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    LogDebug(debugEnabled, $"[{project.Name} ERROR] {line}");
                                }
                            }
                        });

                        process.WaitForExit(300000); // 最多 5 分钟

                        if (!process.HasExited)
                        {
                            process.Kill();
                            LogDebug(debugEnabled, $"发布超时: {project.Name}");
                            return false;
                        }

                        System.Threading.Tasks.Task.WaitAll(outputTask, errorTask);

                        var success = process.ExitCode == 0;
                        LogDebug(debugEnabled, $"发布完成: {project.Name}, 退出码={process.ExitCode}, 成功={success}");

                        if (success)
                        {
                            _ = LogOutputAsync(package, $"{project.Name} 发布完成");
                        }
                        else
                        {
                            _ = LogOutputAsync(package, $"{project.Name} 发布失败 (退出码: {process.ExitCode})");
                        }

                        return success;
                    }
                    catch (Exception ex)
                    {
                        LogDebug(debugEnabled, $"发布异常: {project.Name}, {ex.Message}");
                        _ = LogOutputAsync(package, $"{project.Name} 发布失败: {ex.Message}");
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                await LogOutputAsync(package, $"发布 {project.Name} 失败: {ex.Message}");
                LogDebug(debugEnabled, $"发布异常: {ex}");
                return false;
            }
        }

        private static void LogDebug(bool debugEnabled, string message)
        {
            if (!debugEnabled || string.IsNullOrWhiteSpace(message))
                return;

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
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var outputWindow = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow == null)
                return;

            var paneGuid = new Guid("9C47EA07-7688-4A7C-B2C8-AD5B5B1B2521");
            outputWindow.CreatePane(ref paneGuid, "Eigcac发布", 1, 1);
            outputWindow.GetPane(ref paneGuid, out var pane);
            if (pane != null)
            {
                pane.OutputStringThreadSafe($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
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
    }
}
