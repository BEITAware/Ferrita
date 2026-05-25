# Non-compliant UI Design with Aero Aesthetics

## Overview

The Skyweaver project has memory guidelines emphasizing 'Aero aesthetics' for UI design. These guidelines strictly state:

> For UI design in Skyweaver ('Aero aesthetics'), avoid hardcoded hex colors and flat corners (`CornerRadius="0"`); instead, use theme-defined dynamic resource bindings like `{DynamicResource AeroBackgroundBrush}` and `{DynamicResource StandardCornerRadius}`.

After conducting a comprehensive audit of the project's XAML codebase, numerous violations of these design guidelines were identified. Hardcoded hex colors and flat corners (`CornerRadius="0"`) are prevalent across many UI components, controls, and views.

## Violations Found

### 1. Hardcoded Hex Colors

Over a thousand instances of hardcoded hex color values (`="#[0-9a-fA-F]{6,8}"`) were discovered in XAML files, rather than utilizing the `DynamicResource` bindings defined in the theme dictionary (e.g., `AeroTheme.xaml`, `ThemeBase.xaml`).

**Top Offenders (Sample of files with the most violations):**

* `InstallationWizard/Styles/AeroImplicitStyles.xaml`: 234 occurrences
* `Skyweaver/Controls/ChatSessionControl/Views/ChatSessionControl.xaml`: 185 occurrences
* `Skyweaver/Controls/WorkflowEditorControl/Views/WorkflowEditorControl.xaml`: 150 occurrences
* `Skyweaver/Windows/CreateChatSessionDialog.xaml`: 134 occurrences
* `Skyweaver/Resources/Controls/ChatStyles.xaml`: 87 occurrences
* `Skyweaver/Resources/Controls/CascadePreferenceImplicitStyles.xaml`: 84 occurrences
* `InstallationWizard/Styles/MediaStyles.xaml`: 82 occurrences
* `Skyweaver/Resources/Controls/ScrollBarStyles.xaml`: 77 occurrences
* `Skyweaver/Resources/Controls/PreferencesPanelStyles.xaml`: 72 occurrences
* `Skyweaver/Resources/Controls/ButtonStyles.xaml`: 71 occurrences

### 2. Flat Corners (`CornerRadius="0"`)

Several UI components explicitely set `CornerRadius="0"`, which contradicts the Aero aesthetic's preference for rounded corners using `{DynamicResource StandardCornerRadius}` (which is defined as `3` in `ThemeBase.xaml`).

**Files with `CornerRadius="0"`:**

* `Skyweaver/Resources/Controls/ButtonStyles.xaml`
* `Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml`
* `Skyweaver/Resources/Controls/ScrollBarStyles.xaml`
* `Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml`
* `Skyweaver/Controls/WorkflowEditorControl/Views/WorkflowEditorControl.xaml`

## Proposed Fix

1. **Review Theme Definitions:** Ensure all required colors and styles are properly defined as resources in `ThemeBase.xaml` and `AeroTheme.xaml`.
2. **Refactor Hex Colors:** Systematically replace hardcoded hex values in all XAML files with appropriate `{DynamicResource [ColorKey]}` or `{DynamicResource [BrushKey]}`.
3. **Refactor Corner Radius:** Replace all instances of `CornerRadius="0"` with `{DynamicResource StandardCornerRadius}`.
4. **Code Guidelines Update:** Reinforce the use of theme resources during PR reviews to prevent future regressions.

Please assign this issue for a phased cleanup of the codebase to align with the intended Aero aesthetic.
