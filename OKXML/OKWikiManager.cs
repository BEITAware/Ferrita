using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using AerialCity;
using AerialCity.Core.Primitives;
using AerialCity.Database;
using AerialCity.Database.Schema;
using AerialCity.Embedding;
using AerialCity.Core.Storage;
using AerialCity.GraphStore.Model;

namespace OKXML;

/// <summary>
/// 管理 OKXML 知识库的管理器，封装页面管理、嵌入、增量修改与图维护的核心流程。
/// </summary>
public class OKWikiManager : IAsyncDisposable
{
    /// <summary>
    /// Wiki 的根目录路径。
    /// </summary>
    public string WikiRootPath { get; }

    /// <summary>
    /// Wiki 的名称。
    /// </summary>
    public string WikiName { get; }

    /// <summary>
    /// 存储 AerialCity 数据库的目录。
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 存储 Markdown 文档的目录。
    /// </summary>
    public string DocumentsPath { get; }

    /// <summary>
    /// 关联的 AerialCity 引擎实例。
    /// </summary>
    public AerialCityEngine Engine { get; }

    /// <summary>
    /// 当前 Wiki 对应的 AerialCity 数据库实例。
    /// </summary>
    public AerialDatabase Database { get; private set; } = null!;

    private const string CollectionName = "OKXML_Documents";

    /// <summary>
    /// 初始化一个新的 OKWikiManager 实例。
    /// </summary>
    /// <param name="wikiRootPath">Wiki 根目录。</param>
    /// <param name="wikiName">Wiki 名称（如果尚不存在元数据文件则必须提供；否则将从现有的 [WikiName].xml 中自动提取）。</param>
    /// <param name="embeddingProvider">嵌入向量模型提供程序，如果为 null，将默认构建一个 Dummy 向量模型。</param>
    /// <param name="description">Wiki 的简介描述（可选）。</param>
    /// <param name="author">Wiki 的作者名字（可选）。</param>
    public OKWikiManager(string wikiRootPath, string? wikiName = null, IEmbeddingProvider? embeddingProvider = null, string? description = null, string? author = null)
    {
        WikiRootPath = Path.GetFullPath(wikiRootPath);
        DatabasePath = Path.Combine(WikiRootPath, "Database");
        DocumentsPath = Path.Combine(WikiRootPath, "Documents");

        // 确保基本文件夹结构存在
        Directory.CreateDirectory(WikiRootPath);
        Directory.CreateDirectory(DatabasePath);
        Directory.CreateDirectory(DocumentsPath);

        // 确定 WikiName 并加载/初始化元数据
        WikiName = ResolveWikiName(WikiRootPath, wikiName);

        EnsureWikiMetadataFile(description, author);

        // 配置 AerialCityEngine
        var builder = new AerialCityBuilder()
            .WithStoragePath(DatabasePath);

        if (embeddingProvider != null)
        {
            builder.WithEmbeddingProvider(embeddingProvider);
        }
        else
        {
            builder.WithEmbeddingProvider(new DummyEmbeddingProvider());
        }

        Engine = builder.Build();
    }

    /// <summary>
    /// 使用 AerialCity 真实 API 嵌入服务初始化 OKWikiManager 实例。
    /// 支持 OpenAI 兼容接口和 Google Gemini 接口。
    /// </summary>
    /// <param name="wikiRootPath">Wiki 根目录。</param>
    /// <param name="embeddingConfig">嵌入服务配置（API Key、模型名等）。</param>
    /// <param name="wikiName">Wiki 名称（如果尚不存在元数据文件则必须提供；否则将从现有的 [WikiName].xml 中自动提取）。</param>
    /// <param name="description">Wiki 的简介描述（可选）。</param>
    /// <param name="author">Wiki 的作者名字（可选）。</param>
    public OKWikiManager(string wikiRootPath, OKEmbeddingConfig embeddingConfig, string? wikiName = null, string? description = null, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(embeddingConfig);

        WikiRootPath = Path.GetFullPath(wikiRootPath);
        DatabasePath = Path.Combine(WikiRootPath, "Database");
        DocumentsPath = Path.Combine(WikiRootPath, "Documents");

        // 确保基本文件夹结构存在
        Directory.CreateDirectory(WikiRootPath);
        Directory.CreateDirectory(DatabasePath);
        Directory.CreateDirectory(DocumentsPath);

        // 确定 WikiName 并加载/初始化元数据
        WikiName = ResolveWikiName(WikiRootPath, wikiName);

        EnsureWikiMetadataFile(description, author);

        // 使用真实 API 嵌入提供程序配置 AerialCityEngine
        var apiProvider = new ApiEmbeddingProvider(embeddingConfig);

        var builder = new AerialCityBuilder()
            .WithStoragePath(DatabasePath)
            .WithEmbeddingProvider(apiProvider);

        Engine = builder.Build();
    }

    /// <summary>
    /// 异步初始化数据库和集合。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 创建数据库，明确指定 Storage 路径并禁用 WAL 以提升测试及并发稳定性
        var dbOptions = new DatabaseOptions
        {
            Name = WikiName,
            Storage = new StorageOptions
            {
                BasePath = DatabasePath,
                EnableWal = false
            }
        };
        Database = await Engine.CreateDatabase().Invoke(dbOptions, ct);

        // 初始化 Collection
        var getCollection = Engine.GetCollection();
        var collection = getCollection(Database, CollectionName);
        if (collection == null)
        {
            var createCollection = Engine.CreateCollection();
            createCollection(Database, new CollectionSchema
            {
                Name = CollectionName,
                VectorDimensions = 768, // 默认为 768 维
                Description = "Collection of OKXML documents and page segments."
            });
        }
    }

    /// <summary>
    /// 创建新页面服务。
    /// </summary>
    /// <param name="pageName">页面名（不包含扩展名）。</param>
    /// <param name="content">Clean 的 Markdown 内容或包含 OK 命名空间链接的内容。</param>
    /// <param name="version">页面的初始版本，默认 1.0。</param>
    public void CreatePage(string pageName, string content, string version = "1.0")
    {
        string mdPath = Path.Combine(DocumentsPath, $"{pageName}.md");
        string xmlPath = Path.Combine(DocumentsPath, $"{pageName}.xml");

        // 统一换行符
        string normalizedContent = content.Replace("\r\n", "\n");

        // 1. 写入 Markdown 文档到文件系统
        File.WriteAllText(mdPath, normalizedContent, Encoding.UTF8);

        // 2. 算 Hash 关系
        string docHash = CalculateSha256ShortHash(normalizedContent);
        var titleHashes = CalculateTitleHashes(normalizedContent);

        // 3. 构建元数据并保存
        var metadata = new OKDocumentMetadata
        {
            Id = Guid.NewGuid().ToString(),
            PageName = pageName,
            Embedded = false,
            Deprecated = false,
            Version = version,
            DocumentHash = docHash,
            TitleHashes = titleHashes
        };

        SerializeXml(xmlPath, metadata);
    }

    /// <summary>
    /// 嵌入页面服务：对特定页面分块，计算嵌入并写入 AerialCity 数据库。
    /// </summary>
    public async Task EmbedPageAsync(string pageName, CancellationToken ct = default)
    {
        string xmlPath = Path.Combine(DocumentsPath, $"{pageName}.xml");
        if (!File.Exists(xmlPath))
        {
            throw new FileNotFoundException($"找不到页面 {pageName} 的元数据 xml 文件。");
        }

        var metadata = DeserializeXml<OKDocumentMetadata>(xmlPath);

        // 已经废弃的页面不要重复嵌入
        if (metadata.Deprecated)
        {
            return;
        }

        string mdPath = Path.Combine(DocumentsPath, $"{pageName}.md");
        if (!File.Exists(mdPath))
        {
            throw new FileNotFoundException($"找不到页面 {pageName} 的 markdown 文件。");
        }

        string mdContent = await File.ReadAllTextAsync(mdPath, ct);
        string normalizedContent = mdContent.Replace("\r\n", "\n");

        // 按照二级标题进行分块
        var blocks = SplitBySecondLevelTitles(normalizedContent, metadata.TitleHashes, docHash: metadata.DocumentHash);

        // 写入每个分块到 AerialCity 数据库
        int blockIdx = 0;
        foreach (var block in blocks)
        {
            blockIdx++;
            Console.WriteLine($"[DEBUG] 正在处理分块 {blockIdx}/{blocks.Count}, 标题: '{block.Title}', 长度: {block.Content.Length}");

            // 提取该分块包含的 OK 命名空间链接列表
            var okLinks = ExtractOkLinks(block.Content);

            // 去除 OK 链接本体以保护嵌入向量本身
            string cleanedContent = StripOkLinks(block.Content);

            var segment = new Segment(SegmentKind.TextPassage, cleanedContent)
            {
                SourceUri = Path.Combine("Documents", $"{pageName}.md"),
                StartOffset = block.StartOffset,
                EndOffset = block.EndOffset,
                CollectionName = CollectionName
            };

            // 存储 Custom 字段和元数据信息
            segment.Metadata["ContentHash"] = block.Hash;
            segment.Metadata["PageName"] = pageName;
            segment.Metadata["Version"] = metadata.Version;
            segment.Metadata["Deprecated"] = "false";
            segment.Metadata["Embedded"] = "true";
            segment.Metadata["OkLinks"] = string.Join(";", okLinks);

            // 填充嵌入向量并插入
            Console.WriteLine($"[DEBUG] 开始计算嵌入向量 (EmbedSegment)...");
            await Engine.EmbedSegment().Invoke(segment, ct);
            Console.WriteLine($"[DEBUG] 嵌入向量计算完成，开始插入数据库 (Insert)...");
            await Engine.Insert().Invoke(Database, segment, ct);
            Console.WriteLine($"[DEBUG] 插入数据库完成。");
        }

        // 修改文档的已嵌入属性并保存
        metadata.Embedded = true;
        SerializeXml(xmlPath, metadata);
    }

    /// <summary>
    /// 维护图：检索特定文档中的 ok 命名空间所引的文档，并在图数据库中建立连接关系。
    /// </summary>
    public async Task MaintainGraphAsync(string pageName, CancellationToken ct = default)
    {
        string xmlPath = Path.Combine(DocumentsPath, $"{pageName}.xml");
        if (!File.Exists(xmlPath)) return;

        var metadata = DeserializeXml<OKDocumentMetadata>(xmlPath);
        string currentVersion = metadata.Version;

        // 1. 获取当前页面在当前版本且非废弃的所有 Segment
        var pageSegments = new List<Segment>();
        await foreach (var id in Database.ListSegmentIdsAsync(ct))
        {
            var seg = await Database.ReadSegmentAsync(id, ct);
            if (seg != null &&
                seg.Metadata.TryGetValue("PageName", out var pNameObj) && pNameObj?.ToString() == pageName &&
                seg.Metadata.TryGetValue("Version", out var verObj) && verObj?.ToString() == currentVersion &&
                seg.Metadata.TryGetValue("Deprecated", out var depObj) && depObj?.ToString() == "false")
            {
                pageSegments.Add(seg);
            }
        }

        // 2. 遍历各段，根据 OkLinks 字段所指向的目标建立图连接
        foreach (var srcSeg in pageSegments)
        {
            if (srcSeg.Metadata.TryGetValue("OkLinks", out var okLinksObj) && okLinksObj != null)
            {
                var links = okLinksObj.ToString()!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var link in links)
                {
                    var (targetPage, targetVer) = ParseOkLink(link);
                    if (!string.IsNullOrEmpty(targetPage) && !string.IsNullOrEmpty(targetVer))
                    {
                        var targetSegId = await FindFirstSegmentIdAsync(targetPage, targetVer, ct);
                        if (targetSegId != null)
                        {
                            // 建立 DependsOn 连接关系
                            Engine.AddEdge().Invoke(Database, srcSeg.Id, targetSegId.Value, EdgeKind.DependsOn);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 修改页面服务（增量修改机制）：废弃先前嵌入，提升版本，创建新文档，更新引用者链接，并在图数据库中进行增量图维护。
    /// </summary>
    public async Task UpdatePageAsync(string pageName, string newContent, CancellationToken ct = default)
    {
        string xmlPath = Path.Combine(DocumentsPath, $"{pageName}.xml");
        if (!File.Exists(xmlPath))
        {
            // 如果不存在，我们视为新创建
            CreatePage(pageName, newContent);
            await EmbedPageAsync(pageName, ct);
            await MaintainGraphAsync(pageName, ct);
            return;
        }

        var oldMetadata = DeserializeXml<OKDocumentMetadata>(xmlPath);
        string oldVersion = oldMetadata.Version;

        // 1. 将 AerialCity 数据库中先前该页面版本的 Segment 标注为已废弃
        await foreach (var id in Database.ListSegmentIdsAsync(ct))
        {
            var seg = await Database.ReadSegmentAsync(id, ct);
            if (seg != null &&
                seg.Metadata.TryGetValue("PageName", out var pNameObj) && pNameObj?.ToString() == pageName &&
                seg.Metadata.TryGetValue("Version", out var verObj) && verObj?.ToString() == oldVersion)
            {
                seg.Metadata["Deprecated"] = "true";
                await Engine.Update().Invoke(Database, id, seg, ct);
            }
        }

        // 2. 提升版本号（比如从 1.0 提升到 1.1）
        string newVersion = BumpVersion(oldVersion);

        // 3. 自动化修改所有其他文档中指向这个文档的链接，将其修改为最新版本
        var updatedReferrers = new List<string>();
        foreach (var filePath in Directory.GetFiles(DocumentsPath, "*.md"))
        {
            string otherPageName = Path.GetFileNameWithoutExtension(filePath);
            if (otherPageName.Equals(pageName, StringComparison.OrdinalIgnoreCase)) continue;

            string mdText = await File.ReadAllTextAsync(filePath, ct);
            // 匹配格式：ok.[wiki名称].wiki/pageName_oldVersion
            string pattern = $@"ok\.[\p{{L}}\p{{N}}_-]+\.wiki\/{Regex.Escape(pageName)}_{Regex.Escape(oldVersion)}(?![0-9.])";

            if (Regex.IsMatch(mdText, pattern))
            {
                // 更新 md 的链接文本为新版本
                string newMdText = Regex.Replace(mdText, pattern, m =>
                {
                    string val = m.Value;
                    int idx = val.LastIndexOf('_');
                    return val.Substring(0, idx + 1) + newVersion;
                });

                await File.WriteAllTextAsync(filePath, newMdText, ct);

                // 更新引用者的文档级 XML 中的 DocumentHash，但是不修改引用者自身的版本
                string otherXmlPath = Path.Combine(DocumentsPath, $"{otherPageName}.xml");
                if (File.Exists(otherXmlPath))
                {
                    var otherMeta = DeserializeXml<OKDocumentMetadata>(otherXmlPath);
                    otherMeta.DocumentHash = CalculateSha256ShortHash(newMdText.Replace("\r\n", "\n"));
                    SerializeXml(otherXmlPath, otherMeta);
                }

                // 在 AerialCity 数据库中，把引用者以前写入的 Segment 里的 OkLinks 字段也增量更新为指向最新版本
                await foreach (var id in Database.ListSegmentIdsAsync(ct))
                {
                    var seg = await Database.ReadSegmentAsync(id, ct);
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
                            await Engine.Update().Invoke(Database, id, seg, ct);
                        }
                    }
                }

                updatedReferrers.Add(otherPageName);
            }
        }

        // 4. 创建新版本的页面文件（直接覆盖在文件系统中的原 Page）
        CreatePage(pageName, newContent, newVersion);

        // 5. 将新版本页面执行嵌入
        await EmbedPageAsync(pageName, ct);

        // 6. 增量维护图关系
        // (a) 建立新文档指向其依赖项的连接
        await MaintainGraphAsync(pageName, ct);

        // (b) 建立引用者连接指向该新页面版本的首个 Segment
        var newPageFirstSegId = await FindFirstSegmentIdAsync(pageName, newVersion, ct);
        if (newPageFirstSegId != null)
        {
            foreach (var referrer in updatedReferrers)
            {
                await foreach (var id in Database.ListSegmentIdsAsync(ct))
                {
                    var seg = await Database.ReadSegmentAsync(id, ct);
                    if (seg != null &&
                        seg.Metadata.TryGetValue("PageName", out var refName) && refName?.ToString() == referrer &&
                        seg.Metadata.TryGetValue("OkLinks", out var okLinksObj) && okLinksObj != null)
                    {
                        var links = okLinksObj.ToString()!.Split(';');
                        if (links.Any(l => l.EndsWith($"/{pageName}_{newVersion}", StringComparison.OrdinalIgnoreCase)))
                        {
                            Engine.AddEdge().Invoke(Database, seg.Id, newPageFirstSegId.Value, EdgeKind.DependsOn);
                        }
                    }
                }
            }
        }
    }

    #region Helpers & Utility Methods

    /// <summary>
    /// 对内容计算 SHA-256 并生成简短的 12 位 Hex 字符。
    /// </summary>
    public static string CalculateSha256ShortHash(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        StringBuilder sb = new();
        for (int i = 0; i < 6; i++) // 6 bytes = 12 hex chars
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 去除 Markdown 内容中的所有 OK 命名空间链接本体。
    /// </summary>
    public static string StripOkLinks(string content)
    {
        // 1. 去除格式：[显示文本](ok.wikiName.wiki/PageName_Version) -> 显示文本
        string result = Regex.Replace(content, @"\[([^\]]+)\]\(ok\.[\p{L}\p{N}_-]+\.wiki\/[\p{L}\p{N}_-]+_[0-9]+(?:\.[0-9]+)*\)", "$1");

        // 2. 去除裸链接形式的 OK 命名空间链接本体：ok.wikiName.wiki/PageName_Version -> 空字符
        result = Regex.Replace(result, @"ok\.[\p{L}\p{N}_-]+\.wiki\/[\p{L}\p{N}_-]+_[0-9]+(?:\.[0-9]+)*", "");

        return result;
    }

    /// <summary>
    /// 检索文本中所有 ok 命名空间链接。
    /// </summary>
    public static List<string> ExtractOkLinks(string content)
    {
        var matches = Regex.Matches(content, @"ok\.[\p{L}\p{N}_-]+\.wiki\/[\p{L}\p{N}_-]+_[0-9]+(?:\.[0-9]+)*");
        var list = new List<string>();
        foreach (Match match in matches)
        {
            list.Add(match.Value);
        }
        return list.Distinct().ToList();
    }

    /// <summary>
    /// 解析 OK 链接，提取目标 PageName 与 Version。
    /// </summary>
    public static (string PageName, string Version) ParseOkLink(string link)
    {
        var match = Regex.Match(link, @"^ok\.[\p{L}\p{N}_-]+\.wiki\/([\p{L}\p{N}_-]+?)_([0-9]+(?:\.[0-9]+)*)$");
        if (match.Success)
        {
            return (match.Groups[1].Value, match.Groups[2].Value);
        }
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// 自动计算文档中所有层级标题所控制区域的 Hash 键值对。
    /// </summary>
    public static List<TitleHashEntry> CalculateTitleHashes(string normalizedContent)
    {
        var lines = normalizedContent.Split('\n');
        var lineInfos = new List<LineInfo>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (match.Success)
            {
                lineInfos.Add(new LineInfo
                {
                    LineIndex = i,
                    Text = line,
                    IsHeader = true,
                    HeaderLevel = match.Groups[1].Length
                });
            }
            else
            {
                lineInfos.Add(new LineInfo
                {
                    LineIndex = i,
                    Text = line,
                    IsHeader = false
                });
            }
        }

        var titleHashes = new List<TitleHashEntry>();
        var headers = lineInfos.Where(l => l.IsHeader).ToList();

        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            int endLineIdx = lines.Length;

            // 重叠区域范围查找：向后找到第一个级别高于或等同于当前级别的标题行
            for (int j = i + 1; j < headers.Count; j++)
            {
                if (headers[j].HeaderLevel <= header.HeaderLevel)
                {
                    endLineIdx = headers[j].LineIndex;
                    break;
                }
            }

            var regionLines = lines.Skip(header.LineIndex).Take(endLineIdx - header.LineIndex);
            string regionContent = string.Join("\n", regionLines);
            string regionHash = CalculateSha256ShortHash(regionContent);

            titleHashes.Add(new TitleHashEntry
            {
                Title = header.Text.Trim(),
                Hash = regionHash
            });
        }

        return titleHashes;
    }

    /// <summary>
    /// 按照二级标题对内容进行分块切分。
    /// </summary>
    private static List<SegmentBlock> SplitBySecondLevelTitles(string normalizedContent, List<TitleHashEntry> titleHashes, string docHash)
    {
        var lines = normalizedContent.Split('\n');
        var headers = new List<LineInfo>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (match.Success)
            {
                headers.Add(new LineInfo
                {
                    LineIndex = i,
                    Text = line,
                    IsHeader = true,
                    HeaderLevel = match.Groups[1].Length
                });
            }
        }

        var blockList = new List<SegmentBlock>();
        var h2List = headers.Where(h => h.HeaderLevel == 2).ToList();

        if (h2List.Count == 0)
        {
            // 没有二级标题，整篇文档作为唯一的块
            var okLinks = ExtractOkLinks(normalizedContent);
            blockList.Add(new SegmentBlock
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
            // 有二级标题
            // 1. 获取第一个二级标题之前的导言/序言部分（如有）
            int firstH2Idx = h2List[0].LineIndex;
            if (firstH2Idx > 0)
            {
                var introLines = lines.Take(firstH2Idx);
                string introContent = string.Join("\n", introLines);
                if (!string.IsNullOrWhiteSpace(introContent))
                {
                    var okLinks = ExtractOkLinks(introContent);
                    blockList.Add(new SegmentBlock
                    {
                        Title = string.Empty,
                        Content = introContent,
                        Hash = CalculateSha256ShortHash(introContent),
                        StartOffset = 0,
                        EndOffset = introContent.Length,
                        OkLinks = okLinks
                    });
                }
            }

            // 2. 切分每一个二级标题控制的非重叠段落
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
                string hash = hashEntry?.Hash ?? CalculateSha256ShortHash(blockContent);

                var okLinks = ExtractOkLinks(blockContent);

                blockList.Add(new SegmentBlock
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
            offset += lines[i].Length + 1; // 考虑换行符 \n 占 1 字符
        }
        return offset;
    }

    private static string BumpVersion(string version)
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

    private async Task<AerialId?> FindFirstSegmentIdAsync(string pageName, string version, CancellationToken ct)
    {
        Segment? bestSeg = null;
        int minOffset = int.MaxValue;

        await foreach (var id in Database.ListSegmentIdsAsync(ct))
        {
            var seg = await Database.ReadSegmentAsync(id, ct);
            if (seg != null &&
                seg.Metadata.TryGetValue("PageName", out var pName) && pName?.ToString() == pageName &&
                seg.Metadata.TryGetValue("Version", out var ver) && ver?.ToString() == version &&
                seg.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "false")
            {
                if (seg.StartOffset < minOffset)
                {
                    minOffset = seg.StartOffset;
                    bestSeg = seg;
                }
            }
        }

        return bestSeg?.Id;
    }

    private static string? DetectWikiName(string rootPath)
    {
        var files = Directory.GetFiles(rootPath, "*.xml");
        foreach (var file in files)
        {
            // 过滤掉 Documents 中的 XML 或者是子目录的
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("Database", StringComparison.OrdinalIgnoreCase) || 
                name.Equals("Documents", StringComparison.OrdinalIgnoreCase)) continue;

            // 尝试读取，如果是 OKWikiMetadata，则是 Wiki 元数据
            try
            {
                var serializer = new XmlSerializer(typeof(OKWikiMetadata));
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                var meta = (OKWikiMetadata)serializer.Deserialize(fs)!;
                if (!string.IsNullOrEmpty(meta.WikiName))
                {
                    return meta.WikiName;
                }
            }
            catch
            {
                // 忽略解析错误
            }
        }
        return null;
    }

    /// <summary>
    /// 解析 WikiName：先从已有元数据文件检测，若无则使用 wikiName 参数。
    /// </summary>
    private static string ResolveWikiName(string rootPath, string? wikiName)
    {
        string? detectedWikiName = DetectWikiName(rootPath);
        if (!string.IsNullOrEmpty(detectedWikiName))
        {
            return detectedWikiName;
        }

        if (string.IsNullOrEmpty(wikiName))
        {
            throw new ArgumentException("Wiki 元数据文件不存在且未提供 wikiName 参数来新建 Wiki。");
        }
        return wikiName;
    }

    private void EnsureWikiMetadataFile(string? description = null, string? author = null)
    {
        string xmlPath = Path.Combine(WikiRootPath, $"{WikiName}.xml");
        if (!File.Exists(xmlPath))
        {
            var meta = new OKWikiMetadata
            {
                WikiName = WikiName,
                CreatedAt = DateTime.UtcNow,
                Description = description,
                Author = author
            };
            SerializeXml(xmlPath, meta);
        }
    }

    private static T DeserializeXml<T>(string filePath)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return (T)serializer.Deserialize(fs)!;
    }

    private static void SerializeXml<T>(string filePath, T obj)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        serializer.Serialize(fs, obj);
    }

    #endregion

    /// <summary>
    /// 异步释放资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Database != null)
        {
            await Database.DisposeAsync();
        }
        Engine.Dispose();
        GC.SuppressFinalize(this);
    }

    private class SegmentBlock
    {
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public List<string> OkLinks { get; set; } = new();
    }

    private class LineInfo
    {
        public int LineIndex { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsHeader { get; set; }
        public int HeaderLevel { get; set; }
    }
}
