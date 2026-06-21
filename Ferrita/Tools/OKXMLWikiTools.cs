using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ferrita.Services.FerritaTools;
using Ferrita.Services.Directories;
using Ferrita.Services.AerialCityRag;
using Ferrita.Controls.EmbeddingModelConfigurationControl.Services;
using Ferrita.Controls.EmbeddingModelConfigurationControl.Models;
using OKXML;
using AerialCity.Embedding;
using AerialCity.Core.Primitives;
using AerialCity.Database;
using AerialCity;
using AerialCity.Retrieval;

namespace Ferrita.Tools
{
    public sealed class OKCreateWikiTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKCreateWiki";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在知识首选项目录下创建一个新 Wiki 文件夹，并初始化 OKXML 数据库与元数据文件。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiName",
                    "要创建的 Wiki 的名称（如 'tech_wiki'）。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Description",
                    "Wiki 的简介描述（可选）。",
                    FerritaToolParameterType.String,
                    isRequired: false),
                new FerritaToolParameterDefinition(
                    "Author",
                    "Wiki 的作者（可选）。",
                    FerritaToolParameterType.String,
                    isRequired: false)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "在 Ferrita 知识首选项目录下创建一个新 Wiki 文件夹。它将生成对应的元数据 XML 并初始化底层数据库。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiName = arguments.GetString("WikiName")?.Trim();
                if (string.IsNullOrEmpty(wikiName))
                {
                    return FerritaToolResult.Failure("WikiName 参数缺失或为空。");
                }

                string? description = arguments.GetString("Description")?.Trim();
                string? author = arguments.GetString("Author")?.Trim();

                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                await using var manager = new OKWikiManager(wikiRootPath, wikiName, description: description, author: author);
                await manager.InitializeAsync(cancellationToken);

                return FerritaToolResult.Success($"Wiki '{wikiName}' 创建成功。目录路径: {wikiRootPath}");
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"创建 Wiki 失败: {ex.Message}");
            }
        }
    }

    public sealed class OKAddPageTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKAddPage";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "为指定的 Wiki 添加一个 Markdown 页面。引导 LLM 在页面中只使用一个一级大标题，使用数个二级标题。此工具不会立刻触发嵌入计算与图生成。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiName",
                    "Wiki 的名称。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "PageName",
                    "要添加的页面名称（不需要带 .md 扩展名）。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Content",
                    "页面的 Markdown 内容。应当包含且仅包含一个一级标题 (#) 和数个二级标题 (##)。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "为指定的 Wiki 添加页面。页面名称仅在这里使用，其他的操作要求精确的页面 URL (如 ok.[wikiName].wiki/[pageName]_1.0)。请在生成的 Markdown 中且仅包含一个一级大标题，并包含数个二级标题。此操作不立刻触发嵌入和图生成。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiName = arguments.GetString("WikiName")?.Trim();
                string? pageName = arguments.GetString("PageName")?.Trim();
                string? content = arguments.GetString("Content");

                if (string.IsNullOrEmpty(wikiName) || string.IsNullOrEmpty(pageName) || content == null)
                {
                    return FerritaToolResult.Failure("缺少必需的参数（WikiName, PageName, Content）。");
                }

                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                if (!Directory.Exists(wikiRootPath))
                {
                    return FerritaToolResult.Failure($"指定的 Wiki '{wikiName}' 不存在，请先使用 OKCreateWiki 创建。");
                }

                string normalizedContent = content.Replace("\r\n", "\n");

                await using var manager = new OKWikiManager(wikiRootPath, wikiName);
                manager.CreatePage(pageName, normalizedContent, "1.0");

                string pageUrl = $"ok.{wikiName}.wiki/{pageName}_1.0";
                return FerritaToolResult.Success($"页面 '{pageName}' 已成功添加到 Wiki '{wikiName}'。精确 URL: {pageUrl}", new Dictionary<string, object?> { ["url"] = pageUrl });
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"添加页面失败: {ex.Message}");
            }
        }
    }

    public sealed class OKReadPageTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKReadPage";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "读取指定 URL 的 Wiki 页面 Markdown 内容。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "Url",
                    "页面的精确 URL，格式为 ok.[wikiName].wiki/[pageName]_[version] 或 ok.[wikiName].wiki/[pageName]。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "根据页面的精确 URL (如 ok.[wikiName].wiki/[pageName]_[version])，读取该页面在文件系统中的 Markdown 内容。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? url = arguments.GetString("Url")?.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    return FerritaToolResult.Failure("Url 参数缺失或为空。");
                }

                var (wikiName, pageName, version) = OKXMLWikiToolHelpers.ParsePageUrl(url);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                string mdPath = Path.Combine(wikiRootPath, "Documents", $"{pageName}.md");
                string xmlPath = Path.Combine(wikiRootPath, "Documents", $"{pageName}.xml");

                if (!File.Exists(mdPath))
                {
                    return FerritaToolResult.Failure($"页面文件不存在：{mdPath}");
                }

                string mdContent = await File.ReadAllTextAsync(mdPath, cancellationToken);
                string message = mdContent;

                if (File.Exists(xmlPath))
                {
                    var metadata = OKXMLWikiToolHelpers.DeserializeXml<OKDocumentMetadata>(xmlPath);
                    if (version != null && metadata.Version != version)
                    {
                        message = $"[提示: 本地最新版本为 {metadata.Version}，而请求的版本为 {version}。以下是本地最新内容。]\n\n" + mdContent;
                    }
                }

                return FerritaToolResult.Success(message);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"读取页面失败: {ex.Message}");
            }
        }
    }

    public sealed class OKReadPageHashTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKReadPageHash";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "读取指定 URL 的 Wiki 页面 Markdown 内容，并额外展示所有段落的 Hash 列表。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "Url",
                    "页面的精确 URL，格式为 ok.[wikiName].wiki/[pageName]_[version] 或 ok.[wikiName].wiki/[pageName]。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "根据页面的精确 URL 读取该页面，与普通读取不同的是会展示页面中所有段落的 Hash。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? url = arguments.GetString("Url")?.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    return FerritaToolResult.Failure("Url 参数缺失或为空。");
                }

                var (wikiName, pageName, version) = OKXMLWikiToolHelpers.ParsePageUrl(url);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                string mdPath = Path.Combine(wikiRootPath, "Documents", $"{pageName}.md");
                string xmlPath = Path.Combine(wikiRootPath, "Documents", $"{pageName}.xml");

                if (!File.Exists(mdPath) || !File.Exists(xmlPath))
                {
                    return FerritaToolResult.Failure($"页面文件或元数据不存在（MD: {File.Exists(mdPath)}, XML: {File.Exists(xmlPath)}）。");
                }

                string mdContent = await File.ReadAllTextAsync(mdPath, cancellationToken);
                var metadata = OKXMLWikiToolHelpers.DeserializeXml<OKDocumentMetadata>(xmlPath);

                var sb = new StringBuilder();
                sb.AppendLine("### 页面段落 Hash 列表：");
                foreach (var entry in metadata.TitleHashes)
                {
                    sb.AppendLine($"- `{entry.Title}` | Hash: `{entry.Hash}`");
                }
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                if (version != null && metadata.Version != version)
                {
                    sb.AppendLine($"[提示: 本地最新版本为 {metadata.Version}，而请求的版本为 {version}。以下是本地最新内容。]");
                    sb.AppendLine();
                }
                sb.AppendLine(mdContent);

                return FerritaToolResult.Success(sb.ToString());
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"读取页面段落 Hash 失败: {ex.Message}");
            }
        }
    }

    public sealed class OKRewritePageTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKRewritePage";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "增量重写指定 URL 的页面。此工具将接入增量版本机制并更新引用，但不立刻触发嵌入和图生成。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "Url",
                    "页面的精确 URL。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Content",
                    "新的页面 Markdown 内容。应当且仅包含一个一级大标题和数个二级标题。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "根据页面精确 URL 增量重写页面。会废弃现有版本的 Segment，提升版本，更新引用该页面的链接和数据库元数据，但不会立刻触发嵌入计算和建图。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? url = arguments.GetString("Url")?.Trim();
                string? content = arguments.GetString("Content");

                if (string.IsNullOrEmpty(url) || content == null)
                {
                    return FerritaToolResult.Failure("参数缺失（Url, Content）。");
                }

                var (wikiName, pageName, version) = OKXMLWikiToolHelpers.ParsePageUrl(url);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                await using var manager = new OKWikiManager(wikiRootPath, wikiName);
                await manager.InitializeAsync(cancellationToken);

                string newVersion = await OKXMLWikiToolHelpers.UpdatePageWithoutEmbeddingAsync(manager, pageName, content, cancellationToken);

                string newUrl = $"ok.{wikiName}.wiki/{pageName}_{newVersion}";
                return FerritaToolResult.Success($"页面 '{pageName}' 已成功重写。新版本 URL: {newUrl}", new Dictionary<string, object?> { ["url"] = newUrl });
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"重写页面失败: {ex.Message}");
            }
        }
    }

    public sealed class OKRewriteSectionTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKRewriteSection";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "增量重写页面中的特定段落。需要传入段落的 Hash。此工具将接入增量版本机制并更新引用，但不立刻触发嵌入和图生成。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "Url",
                    "页面的精确 URL。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "SectionHash",
                    "需要重写的段落 Hash 值（从 OKReadPageHash 工具获取）。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Content",
                    "该段落的新内容（需要包含对应的二级标题行）。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "重写指定页面中指定 Hash 的段落。会自动替换对应段落正文，废弃旧 Segment，提升新页面版本并更新引用，不立刻触发嵌入计算和图生成。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? url = arguments.GetString("Url")?.Trim();
                string? sectionHash = arguments.GetString("SectionHash")?.Trim();
                string? content = arguments.GetString("Content");

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sectionHash) || content == null)
                {
                    return FerritaToolResult.Failure("参数缺失（Url, SectionHash, Content）。");
                }

                var (wikiName, pageName, version) = OKXMLWikiToolHelpers.ParsePageUrl(url);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                string mdPath = Path.Combine(wikiRootPath, "Documents", $"{pageName}.md");
                if (!File.Exists(mdPath))
                {
                    return FerritaToolResult.Failure($"指定的页面文件不存在：{mdPath}");
                }

                string originalContent = await File.ReadAllTextAsync(mdPath, cancellationToken);
                string updatedContent = OKXMLWikiToolHelpers.ReplaceSection(originalContent, sectionHash, content);

                await using var manager = new OKWikiManager(wikiRootPath, wikiName);
                await manager.InitializeAsync(cancellationToken);

                string newVersion = await OKXMLWikiToolHelpers.UpdatePageWithoutEmbeddingAsync(manager, pageName, updatedContent, cancellationToken);

                string newUrl = $"ok.{wikiName}.wiki/{pageName}_{newVersion}";
                return FerritaToolResult.Success($"段落已成功重写。页面提升到新版本 URL: {newUrl}", new Dictionary<string, object?> { ["url"] = newUrl });
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"重写段落失败: {ex.Message}");
            }
        }
    }

    public sealed class OKFinalizeWikiTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKFinalizeWiki";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "为 Wiki 收口。对所有未嵌入的页面执行向量嵌入和图生成。嵌入模型与最大并发从 Ferrita 首选项自动获取。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiName",
                    "Wiki 的名称。",
                    FerritaToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "为指定的 Wiki 收口，触发所有新添加或已被重写（尚未嵌入）的页面的嵌入向量计算和实体图维护。最大嵌入并发以及使用的 API 嵌入模型会从 Ferrita 语义搜索首选项中自动获取。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiName = arguments.GetString("WikiName")?.Trim();
                if (string.IsNullOrEmpty(wikiName))
                {
                    return FerritaToolResult.Failure("WikiName 参数缺失或为空。");
                }

                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                if (!Directory.Exists(wikiRootPath))
                {
                    return FerritaToolResult.Failure($"指定的 Wiki 目录不存在：{wikiRootPath}");
                }

                var ragConfig = new AerialCityRagConfigurationRepository().Load();
                int maxConcurrency = ragConfig.EmbeddingConcurrency;
                if (maxConcurrency <= 0)
                {
                    maxConcurrency = 4;
                }

                var embeddingModelRepository = new EmbeddingModelConfigurationRepository(new EmbeddingModelConfigurationPathProvider());
                var models = embeddingModelRepository.Load();
                var selectedModel = models.FirstOrDefault(m => m.Key == ragConfig.SelectedEmbeddingModelKey);

                if (selectedModel == null)
                {
                    selectedModel = models.FirstOrDefault(m => m.IsFullyConfigured);
                }

                if (selectedModel == null)
                {
                    return FerritaToolResult.Failure("未找到已完全配置的嵌入模型，请在 Ferrita 语义搜索首选项中进行配置。");
                }

                OKEmbeddingConfig config;
                if (selectedModel.InterfaceSettings is OpenAiEmbeddingModelSettings openai)
                {
                    config = new OKEmbeddingConfig
                    {
                        ApiKey = openai.ApiKey,
                        Model = openai.ModelId,
                        ApiType = EmbeddingApiType.OpenAI,
                        BaseUrl = openai.BaseUrl,
                        Dimensions = selectedModel.Dimensions,
                        Normalize = selectedModel.Normalize
                    };
                }
                else if (selectedModel.InterfaceSettings is GoogleEmbeddingModelSettings google)
                {
                    var parameters = new Dictionary<string, object?>();
                    if (google.UseTaskType)
                    {
                        parameters["taskType"] = google.TaskType;
                    }
                    config = new OKEmbeddingConfig
                    {
                        ApiKey = google.ApiKey,
                        Model = google.ModelId,
                        ApiType = EmbeddingApiType.Google,
                        BaseUrl = google.BaseUrl,
                        Dimensions = selectedModel.Dimensions,
                        Normalize = selectedModel.Normalize,
                        Parameters = parameters
                    };
                }
                else
                {
                    return FerritaToolResult.Failure($"不支持的嵌入模型接口类型: '{selectedModel.InterfaceSettings.InterfaceType}'");
                }

                var concurrentProvider = new OKXMLWikiToolHelpers.ConcurrentEmbeddingProvider(config, maxConcurrency);
                await using var manager = new OKWikiManager(wikiRootPath, wikiName, concurrentProvider);
                await manager.InitializeAsync(cancellationToken);

                string documentsPath = Path.Combine(wikiRootPath, "Documents");
                if (!Directory.Exists(documentsPath))
                {
                    return FerritaToolResult.Success("未在 Wiki 中找到 Documents 目录，无页面需处理。");
                }

                var xmlFiles = Directory.GetFiles(documentsPath, "*.xml");
                var pendingPages = new List<string>();

                foreach (var xmlFile in xmlFiles)
                {
                    string name = Path.GetFileNameWithoutExtension(xmlFile);
                    if (name.Equals(wikiName, StringComparison.OrdinalIgnoreCase)) continue;

                    var metadata = OKXMLWikiToolHelpers.DeserializeXml<OKDocumentMetadata>(xmlFile);
                    if (!metadata.Embedded && !metadata.Deprecated)
                    {
                        pendingPages.Add(name);
                    }
                }

                if (pendingPages.Count == 0)
                {
                    return FerritaToolResult.Success("没有未嵌入的页面。Wiki 收口完成。");
                }

                var dbLock = new SemaphoreSlim(1, 1);

                var tasks = pendingPages.Select(page => 
                    OKXMLWikiToolHelpers.ProcessPageEmbedAndGraphCustomAsync(manager, page, dbLock, cancellationToken)
                );

                await Task.WhenAll(tasks);

                return FerritaToolResult.Success($"Wiki '{wikiName}' 收口完成。成功嵌入和构建图的页面数量: {pendingPages.Count}。已使用模型: {selectedModel.DisplayName}，最大并发: {maxConcurrency}。");
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"收口 Wiki 失败: {ex.Message}");
            }
        }
    }

    public sealed class OKSemanticSearchTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKSemanticSearch";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "对指定的 OKXML Wiki 进行语义（向量余弦相似度）搜索。需要提供 Wiki URL、搜索 query 和 topk。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiUrl",
                    "Wiki 的 URL，可以指代 Wiki 根目录（如 ok.[wikiName].wiki）或任何页面 URL。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Query",
                    "语义搜索的查询内容。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "TopK",
                    "返回的最多结果数量。默认是 10。",
                    FerritaToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "在指定的 OKXML Wiki 范围内，使用语义相似度（余弦相似度）检索所有段落，并按得分由高到低返回 TopK 个段落。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiUrl = arguments.GetString("WikiUrl")?.Trim();
                string? query = arguments.GetString("Query")?.Trim();
                int topK = arguments.GetInteger("TopK", 10);

                if (string.IsNullOrEmpty(wikiUrl) || string.IsNullOrEmpty(query))
                {
                    return FerritaToolResult.Failure("必需的参数（WikiUrl, Query）为空。");
                }

                var (wikiName, _, _) = OKXMLWikiToolHelpers.ParseWikiUrl(wikiUrl);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                if (!Directory.Exists(wikiRootPath))
                {
                    return FerritaToolResult.Failure($"指定的 Wiki 目录不存在：{wikiRootPath}");
                }

                var configTuple = OKXMLWikiToolHelpers.GetEmbeddingConfig();
                if (configTuple == null)
                {
                    return FerritaToolResult.Failure("未找到已完全配置的嵌入模型，请在 Ferrita 语义搜索首选项中进行配置。");
                }

                var config = configTuple.Value.Config;
                string databasePath = Path.Combine(wikiRootPath, "Database", wikiName);
                if (!Directory.Exists(databasePath))
                {
                    return FerritaToolResult.Failure($"Wiki 数据库尚未初始化：{databasePath}");
                }

                var request = new ApiRetrievalRequest
                {
                    ApiKey = config.ApiKey,
                    BaseUrl = config.BaseUrl,
                    ApiType = config.ApiType,
                    Model = config.Model,
                    Dimensions = config.Dimensions,
                    Normalize = config.Normalize,
                    Parameters = config.Parameters ?? new(),
                    Method = RetrievalMethod.Cosine,
                    DatabasePath = Path.Combine(wikiRootPath, "Database"),
                    DatabaseName = wikiName,
                    TextQuery = query,
                    TopK = topK * 2
                };

                var apiService = new ApiRetrievalService();
                var rawResults = await apiService.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

                var validResults = rawResults
                    .Where(r => !(r.Segment.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "true"))
                    .Take(topK)
                    .ToList();

                string formattedMessage = OKXMLWikiToolHelpers.FormatSearchResults("语义 (余弦相似度)", wikiName, query, validResults);
                return FerritaToolResult.Success(formattedMessage);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"语义搜索失败: {ex.Message}");
            }
        }
    }

    public sealed class OKBM25SearchTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKBM25Search";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "对指定的 OKXML Wiki 进行 BM25 检索。需要提供 Wiki URL、搜索 query 和 topk。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiUrl",
                    "Wiki 的 URL，可以指代 Wiki 根目录（如 ok.[wikiName].wiki）或任何页面 URL。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "Query",
                    "BM25 检索的查询内容。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "TopK",
                    "返回的最多结果数量。默认是 10。",
                    FerritaToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "在指定的 OKXML Wiki 范围内，使用 BM25 词频算法对所有的段落进行文本检索，并返回 TopK 个段落。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiUrl = arguments.GetString("WikiUrl")?.Trim();
                string? query = arguments.GetString("Query")?.Trim();
                int topK = arguments.GetInteger("TopK", 10);

                if (string.IsNullOrEmpty(wikiUrl) || string.IsNullOrEmpty(query))
                {
                    return FerritaToolResult.Failure("必需的参数（WikiUrl, Query）为空。");
                }

                var (wikiName, _, _) = OKXMLWikiToolHelpers.ParseWikiUrl(wikiUrl);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                if (!Directory.Exists(wikiRootPath))
                {
                    return FerritaToolResult.Failure($"指定的 Wiki 目录不存在：{wikiRootPath}");
                }

                string databasePath = Path.Combine(wikiRootPath, "Database", wikiName);
                if (!Directory.Exists(databasePath))
                {
                    return FerritaToolResult.Failure($"Wiki 数据库尚未初始化：{databasePath}");
                }

                var configTuple = OKXMLWikiToolHelpers.GetEmbeddingConfig();
                ApiRetrievalRequest request;
                if (configTuple != null)
                {
                    var config = configTuple.Value.Config;
                    request = new ApiRetrievalRequest
                    {
                        ApiKey = config.ApiKey,
                        BaseUrl = config.BaseUrl,
                        ApiType = config.ApiType,
                        Model = config.Model,
                        Dimensions = config.Dimensions,
                        Normalize = config.Normalize,
                        Parameters = config.Parameters ?? new(),
                        Method = RetrievalMethod.BM25,
                        DatabasePath = Path.Combine(wikiRootPath, "Database"),
                        DatabaseName = wikiName,
                        TextQuery = query,
                        TopK = topK * 2
                    };
                }
                else
                {
                    request = new ApiRetrievalRequest
                    {
                        Method = RetrievalMethod.BM25,
                        DatabasePath = Path.Combine(wikiRootPath, "Database"),
                        DatabaseName = wikiName,
                        TextQuery = query,
                        TopK = topK * 2
                    };
                }

                var apiService = new ApiRetrievalService();
                var rawResults = await apiService.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

                var validResults = rawResults
                    .Where(r => !(r.Segment.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "true"))
                    .Take(topK)
                    .ToList();

                string formattedMessage = OKXMLWikiToolHelpers.FormatSearchResults("BM25 文本检索", wikiName, query, validResults);
                return FerritaToolResult.Success(formattedMessage);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"BM25 检索失败: {ex.Message}");
            }
        }
    }

    public sealed class OKRelationSearchTool : IFerritaTool, IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "OKRelationSearch";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "对指定的 OKXML Wiki 进行基于关系向量的检索。利用语义偏置算法（向量2 - 向量1 + 向量3）得到目标向量并执行余弦相似度搜索。",
            "KnowledgeWikiOKXML",
            [
                new FerritaToolParameterDefinition(
                    "WikiUrl",
                    "Wiki 的 URL，可以指代 Wiki 根目录（如 ok.[wikiName].wiki）或任何页面 URL。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "StartAnchor",
                    "关系起始锚点的词条 URL (如 ok.[wikiName].wiki/[pageName]_[version]) 或者是关系起始文本描述。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "EndAnchor",
                    "关系终止锚点的词条 URL 或者是关系终止文本描述。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "QueryStartDescription",
                    "要查询的关系起始描述（向量3，纯文本描述）。",
                    FerritaToolParameterType.String,
                    isRequired: true),
                new FerritaToolParameterDefinition(
                    "TopK",
                    "返回的最多结果数量。默认是 10。",
                    FerritaToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            return "执行高维空间下的语义关系匹配：计算 向量2 (终止锚点) - 向量1 (起始锚点) 获得关系向量，再计算 向量3 (查询起始描述) + 关系向量 得到目标向量，使用目标向量对所有 Wiki 段落进行余弦相似度匹配并返回 TopK。";
        }

        public async Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? wikiUrl = arguments.GetString("WikiUrl")?.Trim();
                string? startAnchor = arguments.GetString("StartAnchor")?.Trim();
                string? endAnchor = arguments.GetString("EndAnchor")?.Trim();
                string? queryStartDescription = arguments.GetString("QueryStartDescription")?.Trim();
                int topK = arguments.GetInteger("TopK", 10);

                if (string.IsNullOrEmpty(wikiUrl) || string.IsNullOrEmpty(startAnchor) || 
                    string.IsNullOrEmpty(endAnchor) || string.IsNullOrEmpty(queryStartDescription))
                {
                    return FerritaToolResult.Failure("必需参数为空。");
                }

                var (wikiName, _, _) = OKXMLWikiToolHelpers.ParseWikiUrl(wikiUrl);
                string knowledgeDirPath = OKXMLWikiToolHelpers.GetKnowledgeDirectoryPath();
                string wikiRootPath = Path.Combine(knowledgeDirPath, wikiName);

                if (!Directory.Exists(wikiRootPath))
                {
                    return FerritaToolResult.Failure($"指定的 Wiki 目录不存在：{wikiRootPath}");
                }

                var configTuple = OKXMLWikiToolHelpers.GetEmbeddingConfig();
                if (configTuple == null)
                {
                    return FerritaToolResult.Failure("未找到已完全配置的嵌入模型，请在 Ferrita 语义搜索首选项中进行配置。");
                }

                var config = configTuple.Value.Config;
                var embeddingProvider = new OKXMLWikiToolHelpers.ConcurrentEmbeddingProvider(config, configTuple.Value.MaxConcurrency);

                // 初始化 OKWikiManager 来访问底层的图/文档段落数据库
                await using var manager = new OKWikiManager(wikiRootPath, wikiName, embeddingProvider);
                await manager.InitializeAsync(cancellationToken);

                // 计算向量 1
                EmbeddingVector v1 = await GetAnchorVectorAsync(manager, startAnchor, embeddingProvider, cancellationToken);
                // 计算向量 2
                EmbeddingVector v2 = await GetAnchorVectorAsync(manager, endAnchor, embeddingProvider, cancellationToken);
                // 计算向量 3
                EmbeddingVector v3 = await embeddingProvider.EmbedTextAsync(queryStartDescription, cancellationToken);

                // 校验维度
                if (v1.Dimensions != v2.Dimensions || v1.Dimensions != v3.Dimensions)
                {
                    return FerritaToolResult.Failure($"维度不匹配：v1({v1.Dimensions}), v2({v2.Dimensions}), v3({v3.Dimensions})。");
                }

                // 向量计算: 目标向量 = v3 + v2 - v1
                int dims = v1.Dimensions;
                float[] targetValues = new float[dims];
                for (int i = 0; i < dims; i++)
                {
                    targetValues[i] = v3[i] + v2[i] - v1[i];
                }
                var targetVector = new EmbeddingVector(targetValues).Normalize();

                // 遍历数据库中的所有 Segment 并计算余弦相似度
                var resultsList = new List<RetrievalResult>();
                await foreach (var id in manager.Database.ListSegmentIdsAsync(cancellationToken))
                {
                    var seg = await manager.Database.ReadSegmentAsync(id, cancellationToken);
                    if (seg != null &&
                        seg.Embedding.HasValue &&
                        !(seg.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "true"))
                    {
                        float score = targetVector.CosineSimilarity(seg.Embedding.Value);
                        resultsList.Add(new RetrievalResult
                        {
                            Segment = seg,
                            Score = score
                        });
                    }
                }

                var topResults = resultsList
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .ToList();

                var formattedMessage = FormatRelationSearchResults(wikiName, startAnchor, endAnchor, queryStartDescription, topResults);
                return FerritaToolResult.Success(formattedMessage);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure($"关系搜索失败: {ex.Message}");
            }
        }

        private static async Task<EmbeddingVector> GetAnchorVectorAsync(
            OKWikiManager manager, 
            string anchor, 
            IEmbeddingProvider embeddingProvider, 
            CancellationToken ct)
        {
            if (anchor.StartsWith("ok.", StringComparison.OrdinalIgnoreCase))
            {
                // 如果是词条 URL，我们解析并在数据库中寻找它的段落
                var (_, pageName, version) = OKXMLWikiToolHelpers.ParseWikiUrl(anchor);
                if (string.IsNullOrEmpty(pageName))
                {
                    throw new ArgumentException($"无效的词条 URL: {anchor}");
                }

                string targetVersion = version!;
                if (string.IsNullOrEmpty(targetVersion))
                {
                    // 从 XML 获取最新版本
                    string xmlPath = Path.Combine(manager.DocumentsPath, $"{pageName}.xml");
                    if (File.Exists(xmlPath))
                    {
                        var metadata = OKXMLWikiToolHelpers.DeserializeXml<OKDocumentMetadata>(xmlPath);
                        targetVersion = metadata.Version;
                    }
                    else
                    {
                        throw new FileNotFoundException($"找不到词条 '{pageName}' 的 XML 元数据文件以确定版本。");
                    }
                }

                // 遍历获取该词条非废弃段落的所有嵌入向量
                var vectors = new List<EmbeddingVector>();
                await foreach (var id in manager.Database.ListSegmentIdsAsync(ct))
                {
                    var seg = await manager.Database.ReadSegmentAsync(id, ct);
                    if (seg != null &&
                        seg.Metadata.TryGetValue("PageName", out var pNameObj) && pNameObj?.ToString() == pageName &&
                        seg.Metadata.TryGetValue("Version", out var verObj) && verObj?.ToString() == targetVersion &&
                        seg.Metadata.TryGetValue("Deprecated", out var depObj) && depObj?.ToString() == "false" &&
                        seg.Embedding.HasValue)
                    {
                        vectors.Add(seg.Embedding.Value);
                    }
                }

                if (vectors.Count == 0)
                {
                    throw new InvalidOperationException($"未在数据库中找到词条 '{pageName}' 版本为 '{targetVersion}' 的任何段落嵌入向量。请确保该词条已经收口。");
                }

                // 平均嵌入向量
                int dims = vectors[0].Dimensions;
                float[] avgValues = new float[dims];
                foreach (var vec in vectors)
                {
                    for (int i = 0; i < dims; i++)
                    {
                        avgValues[i] += vec[i];
                    }
                }
                for (int i = 0; i < dims; i++)
                {
                    avgValues[i] /= vectors.Count;
                }

                return new EmbeddingVector(avgValues);
            }
            else
            {
                // 普通描述文本，直接进行嵌入
                return await embeddingProvider.EmbedTextAsync(anchor, ct);
            }
        }

        private static string FormatRelationSearchResults(
            string wikiName,
            string startAnchor,
            string endAnchor,
            string queryStartDescription,
            IReadOnlyList<RetrievalResult> results)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"--- 关系检索结果 (Vector Relation Search) ---");
            builder.AppendLine($"Wiki 名称: {wikiName}");
            builder.AppendLine($"已知关系: [{startAnchor}] -> [{endAnchor}]");
            builder.AppendLine($"查询起点: [{queryStartDescription}]");
            builder.AppendLine($"目标向量的匹配数: {results.Count}");
            builder.AppendLine();

            if (results.Count == 0)
            {
                builder.AppendLine("没有找到相关的 Wiki 段落。");
                return builder.ToString().TrimEnd();
            }

            for (var index = 0; index < results.Count; index++)
            {
                var item = results[index];
                builder.AppendLine($"{index + 1}. [余弦相似度: {item.Score.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}]");
                
                string pageName = item.Segment.Metadata.TryGetValue("PageName", out var pName) ? pName?.ToString() ?? "" : "";
                string version = item.Segment.Metadata.TryGetValue("Version", out var ver) ? ver?.ToString() ?? "" : "";
                
                builder.AppendLine($"词条 URL: ok.{wikiName}.wiki/{pageName}{(string.IsNullOrEmpty(version) ? "" : "_" + version)}");
                if (item.Segment.Metadata.TryGetValue("ContentHash", out var hash))
                {
                    builder.AppendLine($"段落 Hash: {hash}");
                }
                builder.AppendLine($"偏移量: {item.Segment.StartOffset}-{item.Segment.EndOffset}");
                builder.AppendLine("内容:");
                
                string cleanContent = item.Segment.Content.Trim();
                if (cleanContent.Length > 800)
                {
                    cleanContent = cleanContent.Substring(0, 800) + "...";
                }
                builder.AppendLine(cleanContent);
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
    }

    internal static class OKXMLWikiToolHelpers
    {
        public static (OKEmbeddingConfig Config, int MaxConcurrency)? GetEmbeddingConfig()
        {
            var ragConfig = new AerialCityRagConfigurationRepository().Load();
            int maxConcurrency = ragConfig.EmbeddingConcurrency;
            if (maxConcurrency <= 0)
            {
                maxConcurrency = 4;
            }

            var embeddingModelRepository = new EmbeddingModelConfigurationRepository(new EmbeddingModelConfigurationPathProvider());
            var models = embeddingModelRepository.Load();
            var selectedModel = models.FirstOrDefault(m => m.Key == ragConfig.SelectedEmbeddingModelKey);

            if (selectedModel == null)
            {
                selectedModel = models.FirstOrDefault(m => m.IsFullyConfigured);
            }

            if (selectedModel == null)
            {
                return null;
            }

            OKEmbeddingConfig config;
            if (selectedModel.InterfaceSettings is OpenAiEmbeddingModelSettings openai)
            {
                config = new OKEmbeddingConfig
                {
                    ApiKey = openai.ApiKey,
                    Model = openai.ModelId,
                    ApiType = EmbeddingApiType.OpenAI,
                    BaseUrl = openai.BaseUrl,
                    Dimensions = selectedModel.Dimensions,
                    Normalize = selectedModel.Normalize
                };
            }
            else if (selectedModel.InterfaceSettings is GoogleEmbeddingModelSettings google)
            {
                var parameters = new Dictionary<string, object?>();
                if (google.UseTaskType)
                {
                    parameters["taskType"] = google.TaskType;
                }
                config = new OKEmbeddingConfig
                {
                    ApiKey = google.ApiKey,
                    Model = google.ModelId,
                    ApiType = EmbeddingApiType.Google,
                    BaseUrl = google.BaseUrl,
                    Dimensions = selectedModel.Dimensions,
                    Normalize = selectedModel.Normalize,
                    Parameters = parameters
                };
            }
            else
            {
                return null;
            }

            return (config, maxConcurrency);
        }

        public static (string WikiName, string? PageName, string? Version) ParseWikiUrl(string url)
        {
            var match = Regex.Match(url, @"^ok\.([\p{L}\p{N}_-]+)\.wiki(?:\/([\p{L}\p{N}_-]+?))?(?:_([0-9]+(?:\.[0-9]+)*))?$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new ArgumentException($"无效的 Wiki URL 格式：'{url}'。应当符合 'ok.[wikiName].wiki' 或者是 'ok.[wikiName].wiki/[pageName]_[version]'。");
            }

            string wikiName = match.Groups[1].Value;
            string? pageName = match.Groups[2].Success ? match.Groups[2].Value : null;
            string? version = match.Groups[3].Success ? match.Groups[3].Value : null;

            return (wikiName, pageName, version);
        }

        public static string FormatSearchResults(string method, string wikiName, string query, IReadOnlyList<RetrievalResult> results)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"--- {method} 搜索结果 ---");
            builder.AppendLine($"Wiki 名称: {wikiName}");
            builder.AppendLine($"查询词: {query}");
            builder.AppendLine($"匹配数: {results.Count}");
            builder.AppendLine();

            if (results.Count == 0)
            {
                builder.AppendLine("没有找到相关的 Wiki 段落。");
                return builder.ToString().TrimEnd();
            }

            for (var index = 0; index < results.Count; index++)
            {
                var item = results[index];
                builder.AppendLine($"{index + 1}. [得分: {item.Score.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}]");
                
                string pageName = item.Segment.Metadata.TryGetValue("PageName", out var pName) ? pName?.ToString() ?? "" : "";
                string version = item.Segment.Metadata.TryGetValue("Version", out var ver) ? ver?.ToString() ?? "" : "";
                
                builder.AppendLine($"词条 URL: ok.{wikiName}.wiki/{pageName}{(string.IsNullOrEmpty(version) ? "" : "_" + version)}");
                if (item.Segment.Metadata.TryGetValue("ContentHash", out var hash))
                {
                    builder.AppendLine($"段落 Hash: {hash}");
                }
                builder.AppendLine($"偏移量: {item.Segment.StartOffset}-{item.Segment.EndOffset}");
                builder.AppendLine("内容:");
                
                string cleanContent = item.Segment.Content.Trim();
                if (cleanContent.Length > 800)
                {
                    cleanContent = cleanContent.Substring(0, 800) + "...";
                }
                builder.AppendLine(cleanContent);
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        public static string GetKnowledgeDirectoryPath()
        {
            var path = FerritaDirectoryRuntime.Instance.GetConfiguration().KnowledgeDirectoryPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "Knowledge");
            }
            return Path.GetFullPath(path);
        }

        public static (string WikiName, string PageName, string? Version) ParsePageUrl(string url)
        {
            var match = Regex.Match(url, @"^ok\.([\p{L}\p{N}_-]+)\.wiki\/([\p{L}\p{N}_-]+?)(?:_([0-9]+(?:\.[0-9]+)*))?$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new ArgumentException($"无效的页面 URL 格式：'{url}'。应当符合 'ok.[wikiName].wiki/[pageName]_[version]' 或者是 'ok.[wikiName].wiki/[pageName]'。");
            }

            string wikiName = match.Groups[1].Value;
            string pageName = match.Groups[2].Value;
            string? version = match.Groups[3].Success ? match.Groups[3].Value : null;

            return (wikiName, pageName, version);
        }

        public static T DeserializeXml<T>(string filePath)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return (T)serializer.Deserialize(fs)!;
        }

        public static void SerializeXml<T>(string filePath, T obj)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            serializer.Serialize(fs, obj);
        }

        public static string BumpVersion(string version)
        {
            int lastDotIndex = version.LastIndexOf('.');
            if (lastDotIndex >= 0 && lastDotIndex < version.Length - 1)
            {
                string prefix = version.Substring(0, lastDotIndex);
                string lastPart = version.Substring(lastDotIndex + 1);
                if (int.TryParse(lastPart, System.Globalization.CultureInfo.InvariantCulture, out int lastNum))
                {
                    return $"{prefix}.{lastNum + 1}";
                }
            }

            if (int.TryParse(version, System.Globalization.CultureInfo.InvariantCulture, out int singleNum))
            {
                return $"{singleNum}.1";
            }

            return version + ".1";
        }

        public static async Task<string> UpdatePageWithoutEmbeddingAsync(OKWikiManager manager, string pageName, string newContent, CancellationToken ct)
        {
            string xmlPath = Path.Combine(manager.DocumentsPath, $"{pageName}.xml");
            if (!File.Exists(xmlPath))
            {
                manager.CreatePage(pageName, newContent);
                return "1.0";
            }

            var oldMetadata = DeserializeXml<OKDocumentMetadata>(xmlPath);
            string oldVersion = oldMetadata.Version;

            await foreach (var id in manager.Database.ListSegmentIdsAsync(ct))
            {
                var seg = await manager.Database.ReadSegmentAsync(id, ct);
                if (seg != null &&
                    seg.Metadata.TryGetValue("PageName", out var pNameObj) && pNameObj?.ToString() == pageName &&
                    seg.Metadata.TryGetValue("Version", out var verObj) && verObj?.ToString() == oldVersion)
                {
                    seg.Metadata["Deprecated"] = "true";
                    await manager.Engine.Update().Invoke(manager.Database, id, seg, ct);
                }
            }

            string newVersion = BumpVersion(oldVersion);

            foreach (var filePath in Directory.GetFiles(manager.DocumentsPath, "*.md"))
            {
                string otherPageName = Path.GetFileNameWithoutExtension(filePath);
                if (otherPageName.Equals(pageName, StringComparison.OrdinalIgnoreCase)) continue;

                string mdText = await File.ReadAllTextAsync(filePath, ct);
                string pattern = $@"ok\.[\p{{L}}\p{{N}}_-]+\.wiki\/{Regex.Escape(pageName)}_{Regex.Escape(oldVersion)}(?![0-9.])";

                if (Regex.IsMatch(mdText, pattern))
                {
                    string newMdText = Regex.Replace(mdText, pattern, m =>
                    {
                        string val = m.Value;
                        int idx = val.LastIndexOf('_');
                        return val.Substring(0, idx + 1) + newVersion;
                    });

                    await File.WriteAllTextAsync(filePath, newMdText, ct);

                    string otherXmlPath = Path.Combine(manager.DocumentsPath, $"{otherPageName}.xml");
                    if (File.Exists(otherXmlPath))
                    {
                        var otherMeta = DeserializeXml<OKDocumentMetadata>(otherXmlPath);
                        otherMeta.DocumentHash = OKWikiManager.CalculateSha256ShortHash(newMdText.Replace("\r\n", "\n"));
                        SerializeXml(otherXmlPath, otherMeta);
                    }

                    await foreach (var id in manager.Database.ListSegmentIdsAsync(ct))
                    {
                        var seg = await manager.Database.ReadSegmentAsync(id, ct);
                        if (seg != null &&
                            seg.Metadata.TryGetValue("PageName", out var nameVal) && nameVal?.ToString() == otherPageName &&
                            seg.Metadata.TryGetValue("OkLinks", out var okLinksVal) && okLinksVal != null)
                        {
                            string linksStr = okLinksVal.ToString()!;
                            var links = linksStr.Split(';');
                            bool anyUpdated = false;
                            for (int i = 0; i < links.Length; i++)
                            {
                                if (links[i].EndsWith($"/{pageName}_{oldVersion}", StringComparison.OrdinalIgnoreCase))
                                {
                                    links[i] = links[i].Substring(0, links[i].Length - oldVersion.Length) + newVersion;
                                    anyUpdated = true;
                                }
                            }

                            if (anyUpdated)
                            {
                                seg.Metadata["OkLinks"] = string.Join(";", links);
                                await manager.Engine.Update().Invoke(manager.Database, id, seg, ct);
                            }
                        }
                    }
                }
            }

            manager.CreatePage(pageName, newContent, newVersion);
            return newVersion;
        }

        public static string ReplaceSection(string originalContent, string targetHash, string newSectionContent)
        {
            var titleHashes = OKWikiManager.CalculateTitleHashes(originalContent);
            var entry = titleHashes.FirstOrDefault(e => e.Hash == targetHash);
            if (entry == null)
            {
                throw new ArgumentException($"找不到 Hash 为 {targetHash} 的段落。");
            }

            var lines = originalContent.Split('\n');
            var lineInfos = new List<ToolLineInfo>();
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                lineInfos.Add(new ToolLineInfo
                {
                    LineIndex = i,
                    Text = line,
                    IsHeader = match.Success,
                    HeaderLevel = match.Success ? match.Groups[1].Length : 0
                });
            }

            var headers = lineInfos.Where(l => l.IsHeader).ToList();
            var targetHeader = headers.FirstOrDefault(h => h.Text.Trim() == entry.Title.Trim());
            if (targetHeader == null)
            {
                throw new ArgumentException($"无法在页面中定位标题 '{entry.Title}'。");
            }

            int targetIndex = headers.IndexOf(targetHeader);
            int endLineIdx = lines.Length;
            for (int j = targetIndex + 1; j < headers.Count; j++)
            {
                if (headers[j].HeaderLevel <= targetHeader.HeaderLevel)
                {
                    endLineIdx = headers[j].LineIndex;
                    break;
                }
            }

            var newLines = new List<string>();
            newLines.AddRange(lines.Take(targetHeader.LineIndex));

            var splitNewContent = newSectionContent.Replace("\r\n", "\n").Split('\n');
            newLines.AddRange(splitNewContent);

            newLines.AddRange(lines.Skip(endLineIdx));

            return string.Join("\n", newLines);
        }

        public static async Task ProcessPageEmbedAndGraphCustomAsync(
            OKWikiManager manager, 
            string pageName, 
            SemaphoreSlim dbLock, 
            CancellationToken ct)
        {
            string xmlPath = Path.Combine(manager.DocumentsPath, $"{pageName}.xml");
            if (!File.Exists(xmlPath))
            {
                throw new FileNotFoundException($"找不到页面 {pageName} 的元数据 xml 文件。");
            }

            var metadata = DeserializeXml<OKDocumentMetadata>(xmlPath);
            if (metadata.Deprecated)
            {
                return;
            }

            string mdPath = Path.Combine(manager.DocumentsPath, $"{pageName}.md");
            if (!File.Exists(mdPath))
            {
                throw new FileNotFoundException($"找不到页面 {pageName} 的 markdown 文件。");
            }

            string mdContent = await File.ReadAllTextAsync(mdPath, ct);
            string normalizedContent = mdContent.Replace("\r\n", "\n");

            var blocks = SplitBySecondLevelTitles(normalizedContent, metadata.TitleHashes, metadata.DocumentHash);

            const string CollectionName = "OKXML_Documents";

            foreach (var block in blocks)
            {
                var okLinks = OKWikiManager.ExtractOkLinks(block.Content);
                string cleanedContent = OKWikiManager.StripOkLinks(block.Content);

                var segment = new Segment(SegmentKind.TextPassage, cleanedContent)
                {
                    SourceUri = Path.Combine("Documents", $"{pageName}.md"),
                    StartOffset = block.StartOffset,
                    EndOffset = block.EndOffset,
                    CollectionName = CollectionName
                };

                segment.Metadata["ContentHash"] = block.Hash;
                segment.Metadata["PageName"] = pageName;
                segment.Metadata["Version"] = metadata.Version;
                segment.Metadata["Deprecated"] = "false";
                segment.Metadata["Embedded"] = "true";
                segment.Metadata["OkLinks"] = string.Join(";", okLinks);

                await manager.Engine.EmbedSegment().Invoke(segment, ct);

                await dbLock.WaitAsync(ct);
                try
                {
                    await manager.Engine.Insert().Invoke(manager.Database, segment, ct);
                }
                finally
                {
                    dbLock.Release();
                }
            }

            metadata.Embedded = true;
            SerializeXml(xmlPath, metadata);

            await dbLock.WaitAsync(ct);
            try
            {
                await manager.MaintainGraphAsync(pageName, ct);
            }
            finally
            {
                dbLock.Release();
            }
        }

        private static List<ToolSegmentBlock> SplitBySecondLevelTitles(string normalizedContent, List<TitleHashEntry> titleHashes, string docHash)
        {
            var lines = normalizedContent.Split('\n');
            var headers = new List<ToolLineInfo>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (match.Success)
                {
                    headers.Add(new ToolLineInfo
                    {
                        LineIndex = i,
                        Text = line,
                        IsHeader = true,
                        HeaderLevel = match.Groups[1].Length
                    });
                }
            }

            var blockList = new List<ToolSegmentBlock>();
            var h2List = headers.Where(h => h.HeaderLevel == 2).ToList();

            if (h2List.Count == 0)
            {
                var okLinks = OKWikiManager.ExtractOkLinks(normalizedContent);
                blockList.Add(new ToolSegmentBlock
                {
                    Title = string.Empty,
                    Content = normalizedContent,
                    Hash = docHash,
                    StartOffset = 0,
                    EndOffset = normalizedContent.Length,
                    OkLinks = okLinks
                });
            }
            else
            {
                int firstH2Idx = h2List[0].LineIndex;
                if (firstH2Idx > 0)
                {
                    var introLines = lines.Take(firstH2Idx);
                    string introContent = string.Join("\n", introLines);
                    if (!string.IsNullOrWhiteSpace(introContent))
                    {
                        var okLinks = OKWikiManager.ExtractOkLinks(introContent);
                        blockList.Add(new ToolSegmentBlock
                        {
                            Title = string.Empty,
                            Content = introContent,
                            Hash = OKWikiManager.CalculateSha256ShortHash(introContent),
                            StartOffset = 0,
                            EndOffset = introContent.Length,
                            OkLinks = okLinks
                        });
                    }
                }

                for (int i = 0; i < h2List.Count; i++)
                {
                    var currentH2 = h2List[i];
                    int nextH2Idx = lines.Length;
                    if (i + 1 < h2List.Count)
                    {
                        nextH2Idx = h2List[i + 1].LineIndex;
                    }

                    var blockLines = lines.Skip(currentH2.LineIndex).Take(nextH2Idx - currentH2.LineIndex);
                    string blockContent = string.Join("\n", blockLines);

                    int startOffset = GetCharacterOffset(lines, currentH2.LineIndex);
                    int endOffset = startOffset + blockContent.Length;

                    string titleText = currentH2.Text.Trim();
                    var hashEntry = titleHashes.FirstOrDefault(th => th.Title == titleText);
                    string hash = hashEntry?.Hash ?? OKWikiManager.CalculateSha256ShortHash(blockContent);

                    var okLinks = OKWikiManager.ExtractOkLinks(blockContent);

                    blockList.Add(new ToolSegmentBlock
                    {
                        Title = titleText,
                        Content = blockContent,
                        Hash = hash,
                        StartOffset = startOffset,
                        EndOffset = endOffset,
                        OkLinks = okLinks
                    });
                }
            }

            return blockList;
        }

        private static int GetCharacterOffset(string[] lines, int lineIndex)
        {
            int offset = 0;
            for (int i = 0; i < lineIndex; i++)
            {
                offset += lines[i].Length + 1;
            }
            return offset;
        }

        public class ToolSegmentBlock
        {
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public string Hash { get; set; } = string.Empty;
            public int StartOffset { get; set; }
            public int EndOffset { get; set; }
            public List<string> OkLinks { get; set; } = new();
        }

        public class ToolLineInfo
        {
            public int LineIndex { get; set; }
            public string Text { get; set; } = string.Empty;
            public bool IsHeader { get; set; }
            public int HeaderLevel { get; set; }
        }

        public sealed class ConcurrentEmbeddingProvider : IEmbeddingProvider
        {
            private readonly ApiEmbeddingProvider _inner;
            private readonly SemaphoreSlim _semaphore;

            public ConcurrentEmbeddingProvider(OKEmbeddingConfig config, int maxConcurrency)
            {
                _inner = new ApiEmbeddingProvider(config);
                _semaphore = new SemaphoreSlim(maxConcurrency > 0 ? maxConcurrency : 4);
            }

            public int Dimensions => _inner.Dimensions;

            public async Task<EmbeddingVector> EmbedTextAsync(string text, CancellationToken ct = default)
            {
                await _semaphore.WaitAsync(ct);
                try
                {
                    return await _inner.EmbedTextAsync(text, ct);
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            public async Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            {
                var tasks = texts.Select(text => EmbedTextAsync(text, ct));
                return await Task.WhenAll(tasks);
            }

            public async Task<EmbeddingVector> EmbedBinaryAsync(ReadOnlyMemory<byte> data, string mimeType, CancellationToken ct = default)
            {
                await _semaphore.WaitAsync(ct);
                try
                {
                    return await _inner.EmbedBinaryAsync(data, mimeType, ct);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }
    }
}
