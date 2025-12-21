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
    }
}
