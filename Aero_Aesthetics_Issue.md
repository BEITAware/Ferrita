# 标题：[Aero 美学审查] 梳理与修复项目中硬编码的颜色与硬角距 (CornerRadius="0")

## 问题描述

在检查和审查 `Skyweaver` 项目（尤其是基于 Aero 美学风格设计的 UI 组件）时，我们发现了大量的硬编码颜色值（Hardcoded Colors）和硬边角/平角（`CornerRadius="0"`）的现象。根据 Aero 风格的设计规范，应该避免硬编码颜色和零圆角距，转而使用在主题资源字典中定义的动态资源，例如 `{DynamicResource AeroBackgroundBrush}` 与 `{DynamicResource StandardCornerRadius}`，以保证跨界面风格的统一和可维护性。

由于 `InstallationWizard` 虽然同属于此解决方案，但存在一定的独立性，此处的关注点主要集中在 `Skyweaver` 本身的 UI（包括控件样式、对话框窗口和主窗口等）。

## 具体违规位置

### 1. 硬角距 `CornerRadius="0"` 问题
在各种控件样式或特定的界面视图中，直接指定了零宽度的圆角：
- `Skyweaver/Resources/Controls/ButtonStyles.xaml`
  - 第228行、第235行、第240行。
- `Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml`
  - 第48行、第135行、第189行。
- `Skyweaver/Resources/Controls/ScrollBarStyles.xaml`
  - 第197行、第587行。
- `Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml`
  - 第63行、第151行。
- `Skyweaver/Controls/WorkflowEditorControl/Views/WorkflowEditorControl.xaml`
  - 第484行。

### 2. 硬编码颜色（Background/Foreground/BorderBrush）问题
项目中很多控件状态（例如 Idle, Hover, Pressed）或者窗口面板背景直接使用类似于 `#FF...`，`#1A...`，或者 `#67BBDDF2` 这样的十六进制硬编码颜色，没有通过 `{DynamicResource}` 引用。

典型案例包括但不限于：
- `Skyweaver/MainWindow.xaml`
  - 第16行: `Background="#FF1A1F28"`
  - 第185行: `Background="#1A202C" BorderBrush="#3D4B66"`
  - 第210行、第223行、第242行、第250行 等。
- `Skyweaver/Windows/CreateChatSessionDialog.xaml`
  - 此对话框包含大量的硬编码前景色和背景色（如 `#18000000`、`#B0FFFFFF`、`#E0FFFFFF` 等）。
- `Skyweaver/Windows/CreateScheduledTaskDialog.xaml`
  - 此对话框包含大量的文本前景色和面板背景色（如 `#A0FFFFFF`、`#30000000` 等）。
- `Skyweaver/Windows/ShellChatWindow.xaml`
  - 第164行、第166行、第174行等硬编码背景和边框颜色。
- `Skyweaver/Resources/Controls/AeroComboBoxStyles.xaml`
  - 第79行、第120行、第141行：大量状态边框颜色 `#67BBDDF2` 等。
- `Skyweaver/Resources/Controls/ChatStyles.xaml` 与 `Skyweaver/Resources/Controls/CascadePreferenceImplicitStyles.xaml`
  - 同样使用 `#67BBDDF2` 作为边框或高亮颜色。

## 建议解决方案

1. **替换 `CornerRadius="0"`**
   - 所有的 `CornerRadius="0"` 应评估是否可修改为 `{DynamicResource StandardCornerRadius}`。
   - 若特定位置确实不需要圆角（如部分拼接或无缝贴合的局部），请补充注释说明；否则，一律使用标准资源进行替换。

2. **替换硬编码颜色**
   - 对于背景颜色，可使用 `{DynamicResource AeroBackgroundBrush}`。对于由于不透明度或颜色变化而引出的其他需求，应该在 `Skyweaver/Resources/Themes/ThemeBase.xaml` 或相关的 `MainWindowResources.xaml` 等资源字典中提取通用的 SolidColorBrush，例如 `AeroForegroundBrush`、`AeroBorderBrush`、`AeroHighlightBrush` 等等。
   - 移除所有的直接的 `Foreground="#..."` 和 `Background="#..."`。

## 受影响范围
此修改将影响绝大多数基于 WPF XAML 编写的视图文件、控件模板与资源字典。应确保替换后的样式不会破坏现有的功能展示。

## 附件信息
由于本人当前不具备直接向项目提出 Issue 的 API 访问权限，因此通过在项目根目录生成此文档并创建 Pull Request 的形式汇报此项审美审查的调查结果。
