using System;
using System.IO;
using System.Reflection;
using System.Text;
using Ferrita.Infrastructure.Mvvm;
using Ferrita.Services.Localization;

namespace Ferrita.Controls.FerritaPreferencesControl.ViewModels.Pages
{
    public sealed class OpenSourceLicensesPreferencesPageViewModel : ObservableObject
    {
        public OpenSourceLicensesPreferencesPageViewModel()
        {
            LicenseSections = CreateLicenseSections();
            LicenseText = BuildLicenseText();
            LocalizationRuntime.Instance.LanguageChanged += (_, _) => RefreshLocalizedText();
        }

        public string Title => L("OpenSourceLicenses.Page.Title", "Open Source Licenses");

        public string Description => L("OpenSourceLicenses.Page.Description", "This application itself is released under CC0, while referenced packages each follow their own open source licenses.");

        public string LicenseText { get; }

        public OpenSourceLicenseSection[] LicenseSections { get; }

        private void RefreshLocalizedText()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
        }

        private static OpenSourceLicenseSection[] CreateLicenseSections()
        {
            var sections = new OpenSourceLicenseSection[ResolvedPackages.Length + 2];
            sections[0] = new(
                "Ferrita",
                "CC0-1.0",
                "This application itself is released under CC0.",
                ReadLicenseText("CC0-1.0-LICENSE.txt"));

            for (var index = 0; index < ResolvedPackages.Length; index++)
            {
                var package = ResolvedPackages[index];
                sections[index + 1] = new(
                    $"{package.Id} {package.Version}",
                    package.LicenseName,
                    package.Note,
                    ResolveLicenseText(package));
            }

            sections[^1] = new(
                "Docnet.Core bundled native PDFium runtime",
                "BSD-3-Clause and Apache-2.0 notices",
                "Native runtime license text shipped inside the Docnet.Core package.",
                ReadLicenseText("Docnet.Core-Native-LICENSE.txt"));

            return sections;
        }

        private static string BuildLicenseText()
        {
            var builder = new StringBuilder();
            foreach (var section in CreateLicenseSections())
            {
                builder.AppendLine(section.Title);
                builder.AppendLine(section.LicenseName);
                if (!string.IsNullOrWhiteSpace(section.Note))
                {
                    builder.AppendLine(section.Note);
                }

                builder.AppendLine();
                builder.AppendLine(section.LicenseText);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string ResolveLicenseText(OpenSourcePackageEntry package)
        {
            if (!string.IsNullOrWhiteSpace(package.LicenseFileName))
            {
                return ReadLicenseText(package.LicenseFileName);
            }

            return package.LicenseName switch
            {
                "Apache-2.0" => ReadLicenseText("Apache-2.0-LICENSE.txt"),
                "MIT" => ReadLicenseText("MIT-LICENSE.txt"),
                _ when package.LicenseName.Contains("Microsoft .NET Library License", StringComparison.Ordinal) => ReadLicenseText("DotNet-Foundation-LICENSE.txt"),
                _ => ReadLicenseText("MIT-LICENSE.txt")
            };
        }

        private static string ReadLicenseText(string fileName)
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Licenses", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Licenses", fileName),
                Path.Combine(GetAssemblyDirectory(), "..", "..", "..", "..", "Licenses", fileName)
            };

            foreach (var candidatePath in candidatePaths)
            {
                var fullPath = Path.GetFullPath(candidatePath);
                if (File.Exists(fullPath))
                {
                    return File.ReadAllText(fullPath);
                }
            }

            return $"License text file not found: {fileName}";
        }

        private static string GetAssemblyDirectory()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrWhiteSpace(location)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static readonly OpenSourcePackageEntry[] ResolvedPackages =
        {
            new("Docnet.Core", "2.6.0", "MIT", "Bundled PDFium/native runtime license text is included as its own section."),
            new("HtmlAgilityPack", "1.11.72", "MIT"),
            new("Humanizer.Core", "2.14.1", "MIT"),
            new("Microsoft.Bcl.AsyncInterfaces", "9.0.0", "MIT"),
            new("Microsoft.CodeAnalysis", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.Analyzers", "3.11.0", "MIT"),
            new("Microsoft.CodeAnalysis.Common", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.CSharp", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.CSharp.Scripting", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.CSharp.Workspaces", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.Scripting", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.Scripting.Common", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.VisualBasic", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.VisualBasic.Workspaces", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CodeAnalysis.Workspaces.Common", "4.14.0", "MIT", "", "Microsoft.CodeAnalysis-LICENSE.txt"),
            new("Microsoft.CSharp", "4.7.0", "MIT"),

            new("Microsoft.Extensions.Configuration.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.Diagnostics.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.FileProviders.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.Hosting.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.Logging.Abstractions", "10.0.2", "MIT"),
            new("Microsoft.Extensions.Options", "10.0.2", "MIT"),
            new("Microsoft.Extensions.Primitives", "10.0.2", "MIT"),
            new("Microsoft.NETCore.Platforms", "1.1.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("Microsoft.NETCore.Targets", "1.1.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("Microsoft.Win32.SystemEvents", "8.0.0", "MIT"),
            new("Newtonsoft.Json", "13.0.3", "MIT", "", "Newtonsoft.Json-LICENSE.txt"),
            new("OpenAI", "2.9.1", "MIT"),
            new("OpenCvSharp4", "4.11.0.20250507", "Apache-2.0", "", "OpenCvSharp4-LICENSE.txt"),
            new("OpenCvSharp4.runtime.win", "4.11.0.20250507", "Apache-2.0", "", "OpenCvSharp4-LICENSE.txt"),
            new("runtime.native.System", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("Sdcb.PaddleInference", "3.0.1", "Apache-2.0"),
            new("Sdcb.PaddleInference.runtime.win64.openblas", "3.1.0.54", "Apache-2.0"),
            new("Sdcb.PaddleOCR", "3.0.1", "Apache-2.0"),
            new("Sdcb.PaddleOCR.Models.LocalV5", "3.0.0", "Apache-2.0"),
            new("System.Buffers", "4.5.1", "MIT"),
            new("System.ClientModel", "1.9.0", "MIT"),
            new("System.Collections.Immutable", "9.0.0", "MIT"),
            new("System.Composition", "9.0.0", "MIT"),
            new("System.Composition.AttributedModel", "9.0.0", "MIT"),
            new("System.Composition.Convention", "9.0.0", "MIT"),
            new("System.Composition.Hosting", "9.0.0", "MIT"),
            new("System.Composition.Runtime", "9.0.0", "MIT"),
            new("System.Composition.TypedParts", "9.0.0", "MIT"),
            new("System.Diagnostics.DiagnosticSource", "10.0.2", "MIT"),
            new("System.Drawing.Common", "8.0.11", "MIT"),
            new("System.Globalization", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.IO", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.IO.Hashing", "10.0.0-preview.4.25258.110", "MIT"),
            new("System.IO.Pipelines", "10.0.4", "MIT"),
            new("System.Memory", "4.6.3", "MIT"),
            new("System.Memory.Data", "10.0.1", "MIT"),
            new("System.Net.ServerSentEvents", "10.0.2", "MIT"),
            new("System.Numerics.Tensors", "10.0.0-preview.4.25258.110", "MIT"),
            new("System.Numerics.Vectors", "4.5.0", "MIT"),
            new("System.Reflection", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Reflection.Extensions", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Reflection.Metadata", "9.0.0", "MIT"),
            new("System.Reflection.Primitives", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Resources.ResourceManager", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Runtime", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Runtime.CompilerServices.Unsafe", "6.0.0", "MIT"),
            new("System.Runtime.Handles", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Runtime.InteropServices", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Runtime.InteropServices.RuntimeInformation", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Text.Encoding", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Text.Encoding.CodePages", "7.0.0", "MIT"),
            new("System.Text.Encodings.Web", "10.0.4", "MIT"),
            new("System.Text.Json", "10.0.4", "MIT"),
            new("System.Threading", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Threading.Channels", "7.0.0", "MIT"),
            new("System.Threading.Tasks", "4.3.0", "Microsoft .NET Library License / MIT-family package license URL"),
            new("System.Threading.Tasks.Extensions", "4.5.4", "MIT"),
            new("TreeSitter.DotNet", "1.3.0", "MIT"),
            new("YamlDotNet", "16.3.0", "MIT")
        };
    }

    public sealed record OpenSourcePackageEntry(string Id, string Version, string LicenseName, string Note = "", string LicenseFileName = "");

    public sealed record OpenSourceLicenseSection(string Title, string LicenseName, string Note, string LicenseText);
}
