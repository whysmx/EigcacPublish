using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
    }
}
