# Issue Report: Aero Aesthetics Design Violations

## Description

The Skyweaver project and InstallationWizard contain several UI components that violate the intended "Aero aesthetics" design guidelines.

According to the design guidelines:
- Avoid hardcoded hex colors.
- Avoid flat corners (`CornerRadius="0"`).
- Instead, use theme-defined dynamic resource bindings like `{DynamicResource AeroBackgroundBrush}` and `{DynamicResource StandardCornerRadius}`.

## Specific Violations Found

### 1. Hardcoded Hex Colors

Numerous files contain hardcoded hex color values rather than using dynamic resources. Examples include:

- **InstallationWizard/MainWindow.xaml**:
  - `Color="#22FFFFFF"`, `Color="#05FFFFFF"`, `Color="#00C3FF"`, etc.
  - `Foreground="#88FFFFFF"`, `Foreground="#CCFFFFFF"`, `Foreground="#A5FFFFFF"`
  - `Background="#20000000"`, `Background="#10000000"`

- **Skyweaver/MainWindow.xaml**:
  - `Background="#FF1A1F28"`
  - `GradientStop Color="#FF2E4A6C"`
  - `Foreground="#E2E8F0"`

- **Skyweaver/Resources/Controls/ButtonStyles.xaml**:
  - `GradientStop Color="#00FFFFFF"`, `GradientStop Color="#FF1F8EAD"`
  - `Background="#15000000"`
  - `Foreground="#FF2E5C8A"`

### 2. Flat Corners (`CornerRadius="0"`)

Several UI controls use flat corners, which contradicts the expected curved, glass-like appearance of Aero design. Examples include:

- **Skyweaver/Resources/Controls/ButtonStyles.xaml**:
  - Multiple `Border` elements with `CornerRadius="0"`:
    - Line 228
    - Line 235
    - Line 240

- **Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml**:
  - Line 48: `CornerRadius="0"`
  - Line 135: `CornerRadius="0"`
  - Line 189: `CornerRadius="0"`

- **Skyweaver/Resources/Controls/ScrollBarStyles.xaml**:
  - Line 197: `CornerRadius="0"`
  - Line 587: `CornerRadius="0"`

- **Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml**:
  - Line 63: `CornerRadius="0"`
  - Line 151: `CornerRadius="0"`

- **Skyweaver/Controls/WorkflowEditorControl/Views/WorkflowEditorControl.xaml**:
  - Line 484: `CornerRadius="0"`

## Recommended Fix

1. Replace hardcoded `CornerRadius="0"` instances with `{DynamicResource StandardCornerRadius}` or an appropriately rounded value.
2. Replace hardcoded hex colors for Backgrounds, Foregrounds, and GradientStops with their corresponding semantic `{DynamicResource ...}` keys (e.g., `{DynamicResource AeroBackgroundBrush}`).
3. Review other XAML files for similar violations and standardize styling using the central Aero resource dictionary.
