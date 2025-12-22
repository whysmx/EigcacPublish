using System;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.CommandBars;
using PublishExtension.Commands;
using PublishExtension.Options;

namespace PublishExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(PublishOptions), "发布", "配置", 0, 0, true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class PublishExtensionPackage : AsyncPackage
    {
        public const string PackageGuidString = "f0836e0b-8b15-4d6d-a73f-37e4a7b31eb9";
        private const string MenuTag = "EigcacPublishMenu";
        private const string MenuButtonTag = "EigcacPublishButton";

        private CommandBarButton menuButton;

        protected override async System.Threading.Tasks.Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await PublishCommand.InitializeAsync(this);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow != null)
            {
                var paneGuid = new Guid("9C47EA07-7688-4A7C-B2C8-AD5B5B1B2521");
                outputWindow.CreatePane(ref paneGuid, "Eigcac发布", 1, 1);
            }
            var uiShell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            if (uiShell != null)
            {
                uiShell.UpdateCommandUI(1);
                ActivityLog.LogInformation("PublishExtension", "已调用 UpdateCommandUI。");
            }
            var cmdNameMapping = await GetServiceAsync(typeof(SVsCmdNameMapping)) as IVsCmdNameMapping;
            if (cmdNameMapping != null)
            {
                LogCommandName(cmdNameMapping, PublishCommand.CommandSet, PublishCommand.CommandId);
                LogCommandName(cmdNameMapping, PublishCommand.CommandSet, PublishCommand.CommandProjectId);
                LogCommandName(cmdNameMapping, PublishCommand.CommandSet, PublishCommand.CommandSolutionId);
            }
            await EnsureTopLevelMenuAsync();
            try
            {
                ActivityLog.LogInformation("PublishExtension", "包已初始化，准备加载菜单与命令。");
                var resources = GetType().Assembly.GetManifestResourceNames();
                var hasMenu = false;
                foreach (var name in resources)
                {
                    if (string.Equals(name, "Menus.ctmenu", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Menus.ctmenu.resources", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMenu = true;
                    }
                }
                ActivityLog.LogInformation("PublishExtension", $"程序集资源数: {resources.Length}, 是否包含 Menus.ctmenu: {hasMenu}");
            }
            catch
            {
                // Ignore logging errors.
            }
        }

        private static void LogCommandName(IVsCmdNameMapping mapping, Guid guid, int id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmdGuid = guid;
            var hr = mapping.MapGUIDIDToName(ref cmdGuid, (uint)id, VSCMDNAMEOPTS.CNO_GETBOTH, out var name);
            ActivityLog.LogInformation("PublishExtension", $"命令映射: {guid} {id} hr=0x{hr:X8} name={name ?? "<null>"}");
        }

        private async System.Threading.Tasks.Task EnsureTopLevelMenuAsync()
        {
            var dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null)
            {
                ActivityLog.LogInformation("PublishExtension", "无法获取 DTE，跳过顶部菜单创建。");
                return;
            }

            try
            {
                var commandBars = dte.CommandBars as CommandBars;
                var menuBar = commandBars?["MenuBar"];
                if (menuBar == null)
                {
                    ActivityLog.LogInformation("PublishExtension", "未找到 MenuBar，跳过顶部菜单创建。");
                    return;
                }

                CommandBarPopup popup = null;
                foreach (CommandBarControl control in menuBar.Controls)
                {
                    if (control is CommandBarPopup popupControl && string.Equals(popupControl.Tag, MenuTag, StringComparison.Ordinal))
                    {
                        popup = popupControl;
                        break;
                    }
                }

                if (popup == null)
                {
                    popup = (CommandBarPopup)menuBar.Controls.Add(MsoControlType.msoControlPopup, Type.Missing, Type.Missing, menuBar.Controls.Count + 1, true);
                    popup.Caption = "Eigcac发布";
                    popup.Tag = MenuTag;
                }

                CommandBarButton button = null;
                foreach (CommandBarControl control in popup.Controls)
                {
                    if (control is CommandBarButton buttonControl && string.Equals(buttonControl.Tag, MenuButtonTag, StringComparison.Ordinal))
                    {
                        button = buttonControl;
                        break;
                    }
                }

                if (button == null)
                {
                    button = (CommandBarButton)popup.Controls.Add(MsoControlType.msoControlButton, Type.Missing, Type.Missing, 1, true);
                    button.Caption = "执行发布";
                    button.Tag = MenuButtonTag;
                }

                button.Visible = true;
                button.Enabled = true;
                menuButton = button;
                menuButton.Click += OnMenuButtonClick;
                ActivityLog.LogInformation("PublishExtension", "已创建顶部菜单 Eigcac发布。");
            }
            catch (Exception ex)
            {
                ActivityLog.LogInformation("PublishExtension", $"创建顶部菜单失败: {ex.Message}");
            }
        }

        private void OnMenuButtonClick(CommandBarButton ctrl, ref bool cancelDefault)
        {
            PublishCommand.Trigger();
        }
    }
}
