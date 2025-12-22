using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace PublishExtension.Options
{
    public class PublishOptions : DialogPage
    {
        [Category("发布")]
        [DisplayName("后端发布路径")]
        [Description("后端发布输出目录，前端将复制到该目录下的 BSServer 子目录。")]
        public string BackendPublishPath { get; set; } = string.Empty;

        [Category("发布")]
        [DisplayName("调试模式")]
        [Description("启用后会在关键步骤记录日志，便于排查发布问题。")]
        public bool EnableDebugLogging { get; set; }

        [Category("TFS")]
        [DisplayName("集合地址")]
        [Description("可选，例如 http://10.10.10.11:8080/tfs/DefaultCollection。为空则自动识别。")]
        public string TfsCollectionUrl { get; set; } = string.Empty;

        [Category("TFS")]
        [DisplayName("服务器路径")]
        [Description("可选，例如 $/CM-WinterFresh/源代码/Trunk/Eigcac。为空则自动识别。")]
        public string TfsServerPath { get; set; } = string.Empty;

        [Category("TFS")]
        [DisplayName("用户名")]
        [Description("可选，格式：DOMAIN\\user 或 user。")]
        public string TfsUsername { get; set; } = string.Empty;

        [Category("TFS")]
        [DisplayName("密码")]
        [PasswordPropertyText(true)]
        [Description("可选，与用户名配合使用。")]
        public string TfsPassword { get; set; } = string.Empty;
    }
}
