1. **Create ReadImagesTool**:
   - Implement `ReadImagesTool` in `Skyweaver/Tools/ReadImagesTool.cs`.
   - Tool description should state it reads up to 3 images concurrently.
   - It should accept paths and process up to 3 paths.
   - For each valid path, embed it as a preserved image using `$"<SkyweaverPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(path)}\" /></SkyweaverPreservedContent>"`.
   - Return concatenated XML blocks inside the tool return content using `SkyweaverToolResult.Success(content)`.

2. **Create ReadThumbnailsTool**:
   - Implement `ReadThumbnailsTool` in `Skyweaver/Tools/ReadThumbnailsTool.cs`.
   - The tool takes a list of image paths (up to 75).
   - Generates up to 3 thumbnail sheets (each containing 5x5 = up to 25 images).
   - Use `System.Drawing` or `System.Windows.Media.Imaging` to load images, resize them, and draw them on a canvas (e.g. `RenderTargetBitmap` + `DrawingVisual` or `Bitmap` from `System.Drawing.Common`).
   - Save the thumbnails to temporary paths.
   - Embed the generated thumbnail paths as preserved resources `$"<SkyweaverPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(thumbPath)}\" /></SkyweaverPreservedContent>"`.
   - Return concatenated XML blocks inside the tool return content using `SkyweaverToolResult.Success(content)`.
