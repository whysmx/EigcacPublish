# 发布 VSIX

VS 扩展：点击“发布”按钮后，自动执行 TFS 更新，并发布 `Eigcac.Main` 和 `Eigcac.BSServer`。

## 构建

在 Windows 上构建：
```
msbuild PublishExtension.sln /t:Rebuild /p:Configuration=Release
```

产物位于：`PublishExtension/bin/Release/*.vsix`
