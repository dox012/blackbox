<p align="center">
  <img src="icon.png" width="96" height="96" alt="黑屏图标">
</p>

# blackbox

一个常驻系统托盘的 Windows 小工具：一键把**所有显示器**盖成全黑、隐藏鼠标、并阻止系统休眠/息屏——屏幕实际仍常亮、电脑后台照常运行。配合给显示器电源指示灯贴一小块黑胶带，即可伪装成待机状态。

- 单文件、约 7 KB、**零依赖**（使用 Windows 自带 .NET Framework）
- 不安装、不写注册表、不自启、不联网，删掉 exe 即彻底清除

## 使用

| 操作 | 方法 |
|------|------|
| 启动常驻 | 双击 `blackbox.exe`，右下角出现黑色小方块图标 |
| 开 / 关黑屏 | **左键单击**托盘图标（或右键 → "黑屏"） |
| 收回黑屏 | 黑屏时按 **`Esc`**（程序仍在托盘待命，可随时再开） |
| 彻底退出 | 右键托盘图标 → "退出" |

> 黑屏期间鼠标键盘照常工作，仅画面全黑、指针隐藏；退出后系统休眠设置自动还原。

## 自行编译

源码仅一个文件 `blackbox.cs`，用 Windows 自带的 C# 编译器即可生成 exe：

```bash
csc.exe /target:winexe /out:blackbox.exe \
        /reference:System.dll \
        /reference:System.Windows.Forms.dll \
        /reference:System.Drawing.dll \
        blackbox.cs
```

编译器路径通常为 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。

## 说明

- 因 exe 无数字签名，首次运行可能被 SmartScreen 或安全软件提示，点"仍要运行"或加入信任即可——源码公开、无任何危险操作。
- 仅支持 Windows，不支持 macOS / Linux。
