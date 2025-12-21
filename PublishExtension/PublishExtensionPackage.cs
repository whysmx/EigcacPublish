using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using PublishExtension.Commands;
using PublishExtension.Options;

namespace PublishExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(PublishOptions), "发布", "配置", 0, 0, true)]
    public sealed class PublishExtensionPackage : AsyncPackage
    {
        public const string PackageGuidString = "f0836e0b-8b15-4d6d-a73f-37e4a7b31eb9";

        protected override async System.Threading.Tasks.Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await PublishCommand.InitializeAsync(this);
        }
    }
}
