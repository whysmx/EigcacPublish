using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace PublishExtension.Options
{
    public class PublishOptions : DialogPage
    {
        [Category("发布")]
        [DisplayName("调试模式")]
        [Description("启用后会在关键步骤记录日志，便于排查发布问题。")]
        public bool EnableDebugLogging { get; set; }
    }
}
