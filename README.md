# 发布 VSIX

VS 扩展：点击“发布”按钮后，自动执行 TFS 更新，并发布 `Eigcac.Main` 和 `Eigcac.BSServer`。

## 安装

1) 从 Release 下载 `PublishExtension.vsix`  
2) 双击 VSIX，选择要安装的 VS 版本（2019/2022/2026）  
3) 安装完成后重启 Visual Studio

## 使用

1) 从 Release 下载 `PublishExtension.vsix` 并安装到 VS 2019/2022/2026  
2) 在 VS 中打开需要发布的解决方案  
3) 点击“Eigcac发布”命令（位置：`工具` 菜单、`项目` 菜单、解决方案右键菜单；快捷键 `Ctrl+Alt+Shift+P`）  
4) 插件会执行 TFS `get`，发布后端与前端，前端内容会拷贝到后端的 `BSServer` 目录

## 配置项修改

在 VS 中打开：`工具` → `选项` → `发布` → `配置`  
可修改后端发布目录（首次选择后会记住，也可在此处手动修改）。

## 更新扩展

1) 在 VS 中打开：`扩展` → `管理扩展` → `已安装`，卸载旧版本  
2) 重启 Visual Studio  
3) 从 Release 下载最新 `PublishExtension.vsix` 并双击安装  
4) 安装完成后重启 Visual Studio

## 构建

在 Windows 上构建：
```
msbuild PublishExtension.sln /t:Rebuild /p:Configuration=Release
```

产物位于：`PublishExtension/bin/Release/*.vsix`

## 发布 Release

打 tag（例如 `v1.0.1`）推送到 GitHub，会自动设置 VSIX 版本并创建 Release，附带 VSIX 产物。
