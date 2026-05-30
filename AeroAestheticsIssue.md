# Un-Aero Aesthetic Violations in UI Design

## Description
This issue reports multiple instances in our XAML definitions where the UI design violates the project's 'Aero aesthetics' standards. According to our design guidelines, hardcoded hex colors and flat corners (`CornerRadius="0"`) should be avoided. Instead, dynamic resource bindings like `{DynamicResource AeroBackgroundBrush}` and `{DynamicResource StandardCornerRadius}` (or similar theme-defined resource bindings) should be utilized.

## Instances found

### Flat Corners (`CornerRadius="0"`)
The following files contain flat corner configurations which should use the standard corner radius from the theme.

- `./Skyweaver/Resources/Controls/ButtonStyles.xaml` at line 228:
  ```xml
  CornerRadius="0"
  ```
- `./Skyweaver/Resources/Controls/ButtonStyles.xaml` at line 235:
  ```xml
  CornerRadius="0"
  ```
- `./Skyweaver/Resources/Controls/ButtonStyles.xaml` at line 240:
  ```xml
  CornerRadius="0"
  ```
- `./Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml` at line 48:
  ```xml
  CornerRadius="0">
  ```
- `./Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml` at line 135:
  ```xml
  CornerRadius="0">
  ```
- `./Skyweaver/Resources/Controls/CheckBoxComboBoxStyles.xaml` at line 189:
  ```xml
  CornerRadius="0"
  ```
- `./Skyweaver/Resources/Controls/ScrollBarStyles.xaml` at line 197:
  ```xml
  CornerRadius="0">
  ```
- `./Skyweaver/Resources/Controls/ScrollBarStyles.xaml` at line 587:
  ```xml
  CornerRadius="0"/>
  ```
- `./Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml` at line 63:
  ```xml
  CornerRadius="0">
  ```
- `./Skyweaver/Resources/Controls/CustomContextMenuStyles.xaml` at line 151:
  ```xml
  CornerRadius="0">
  ```
- `./Skyweaver/Controls/WorkflowEditorControl/Views/WorkflowEditorControl.xaml` at line 484:
  ```xml
  CornerRadius="0"
  ```

### Hardcoded Hex Colors
There are significantly more occurrences of hardcoded hex colors (e.g., `#FFFFFF`, `#1A1F28`) throughout the project which violate the aesthetic rules. A script check found over 2000 instances of this. For a complete adoption of the Aero aesthetics, these need to be systematically refactored to use `DynamicResource`.

Some examples include:
- `./InstallationWizard/MainWindow.xaml`
- `./Skyweaver/Controls/ChatSessionControl/Views/ChatSessionControl.xaml`
- `./Skyweaver/Resources/Controls/CascadePreferenceImplicitStyles.xaml`
... and many more.

## Action Items
- Replace `CornerRadius="0"` with `{DynamicResource StandardCornerRadius}` or appropriate alternatives.
- Systematically replace hardcoded Hex color values with `{DynamicResource ...Brush}` equivalent bindings.
