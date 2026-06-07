# Issue: Non-conforming Aero Aesthetic Implementations in XAML Resources

## Description
During a review of the Skyweaver UI resources, several instances were found that do not conform to the established "Aero aesthetics" design guidelines. Specifically, the guidelines dictate that:
- Flat corners (`CornerRadius="0"`) should be avoided. Instead, `CornerRadius` should be bound to `{DynamicResource StandardCornerRadius}`.
- Hardcoded hex colors (e.g., `#FF000000`, `#333333`) should be avoided. Instead, dynamic resource bindings like `{DynamicResource AeroBackgroundBrush}` should be used to support theming and maintain a consistent Aero look and feel.

## Occurrences of Flat Corners (`CornerRadius="0"`)
The following files contain hardcoded `CornerRadius="0"`:
- `Skyweaver/Resources/Controls/ButtonStyles.xaml`
- `Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml`
- `Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml`
- `Skyweaver/Resources/Controls/ScrollBarStyles.xaml`

## Occurrences of Hardcoded Hex Colors
The following files (among others) contain hardcoded hex colors instead of dynamic resources:
- `Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml` (e.g., `#333333`, `#AAFFFFFF`, `#FF0099FF`)
- `Skyweaver/Resources/Controls/TreeViewStyles.xaml` (e.g., `#FF1A1F28`)
- `Skyweaver/Resources/Controls/SplitterStyles.xaml` (e.g., `#2A3540`, `#3A4550`, `#FEF3B5`)
- `Skyweaver/Resources/Controls/SliderStyles.xaml` (e.g., `#6060B0F0`, `#FFF0F0F0`, `#4080C0FF`)

## Recommended Fixes
1. Replace `CornerRadius="0"` (or other hardcoded CornerRadius values where appropriate) with `{DynamicResource StandardCornerRadius}`.
2. Replace hardcoded hex colors in Brushes, Dropshadows, and GradientStops with their appropriate theme-defined `{DynamicResource ...}` equivalents, such as `{DynamicResource AeroBackgroundBrush}`.

This issue will help track the cleanup of these UI resources to ensure the application maintains a cohesive Aero design system.
