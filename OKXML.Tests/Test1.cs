using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OKXML;
using AerialCity;
using AerialCity.Database;
using AerialCity.Core.Primitives;
using AerialCity.GraphStore.Model;

namespace OKXML.Tests;

[TestClass]
public class OKXMLTests
{
    private string _testDir = "";

    [TestInitialize]
    public void Init()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OKXML_Test_" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, true);
            }
            catch
            {
                // Ignore
            }
        }
    }

    [TestMethod]
    public void TestVersionBumping()
    {
        var method = typeof(OKWikiManager).GetMethod("BumpVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "找不到 BumpVersion 方法");

        Func<string, string> bump = v => (string)method.Invoke(null, new object[] { v })!;

        Assert.AreEqual("1.1", bump("1.0"));
        Assert.AreEqual("1.10", bump("1.9"));
        Assert.AreEqual("1.11", bump("1.10"));
        Assert.AreEqual("2.0.1", bump("2.0.0"));
        Assert.AreEqual("1.1", bump("1"));
        Assert.AreEqual("abc.1", bump("abc"));
    }

    [TestMethod]
    public void TestLinkExtractionAndStripping()
    {
        string content = "Here is a link ok.MyWiki.wiki/doc-name_1.0. And another [Doc 2](ok.MyWiki.wiki/doc2_1.9.3) in parenthesis. And a Chinese one ok.CatWiki.wiki/猫的介绍_1.0.";

        var links = OKWikiManager.ExtractOkLinks(content);
        Assert.AreEqual(3, links.Count);
        Assert.IsTrue(links.Contains("ok.MyWiki.wiki/doc-name_1.0"));
        Assert.IsTrue(links.Contains("ok.MyWiki.wiki/doc2_1.9.3"));
        Assert.IsTrue(links.Contains("ok.CatWiki.wiki/猫的介绍_1.0"));

        var stripped = OKWikiManager.StripOkLinks(content);
        Assert.AreEqual("Here is a link . And another Doc 2 in parenthesis. And a Chinese one .", stripped);

        var (page1, ver1) = OKWikiManager.ParseOkLink("ok.MyWiki.wiki/doc-name_1.0");
        Assert.AreEqual("doc-name", page1);
        Assert.AreEqual("1.0", ver1);

        var (page2, ver2) = OKWikiManager.ParseOkLink("ok.MyWiki.wiki/doc2_1.9.3");
        Assert.AreEqual("doc2", page2);
        Assert.AreEqual("1.9.3", ver2);

        var (page3, ver3) = OKWikiManager.ParseOkLink("ok.CatWiki.wiki/猫的介绍_1.0");
        Assert.AreEqual("猫的介绍", page3);
        Assert.AreEqual("1.0", ver3);
    }

    [TestMethod]
    public async Task TestWikiMetadataWithOptionalFields()
    {
        var manager = new OKWikiManager(_testDir, "TestWikiWithMetadata", description: "This is a test wiki.", author: "Antigravity");
        await manager.InitializeAsync();

        string xmlPath = Path.Combine(_testDir, "TestWikiWithMetadata.xml");
        Assert.IsTrue(File.Exists(xmlPath), "Wiki级 XML 元数据文件应当被成功创建");

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(OKWikiMetadata));
        using var fs = new FileStream(xmlPath, FileMode.Open, FileAccess.Read);
        var meta = (OKWikiMetadata)serializer.Deserialize(fs)!;

        Assert.AreEqual("TestWikiWithMetadata", meta.WikiName);
        Assert.AreEqual("This is a test wiki.", meta.Description);
        Assert.AreEqual("Antigravity", meta.Author);
        Assert.IsTrue((DateTime.UtcNow - meta.CreatedAt).TotalSeconds < 10);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task TestBasicFlow()
    {
        var manager = new OKWikiManager(_testDir, "TestWiki");
        await manager.InitializeAsync();

        string doc1Content = "# Intro\nThis is doc1.\n\n## Section 1\nLink to [Doc2](ok.TestWiki.wiki/doc2_1.0)";
        string doc2Content = "# Intro\nThis is doc2.\n\n## Section A\nDoc2 detail.";

        manager.CreatePage("doc1", doc1Content, "1.0");
        manager.CreatePage("doc2", doc2Content, "1.0");

        Assert.IsTrue(File.Exists(Path.Combine(_testDir, "Documents", "doc1.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_testDir, "Documents", "doc1.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(_testDir, "Documents", "doc2.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_testDir, "Documents", "doc2.xml")));

        // Embed
        await manager.EmbedPageAsync("doc1");
        await manager.EmbedPageAsync("doc2");

        // Maintain Graph
        await manager.MaintainGraphAsync("doc1");
        await manager.MaintainGraphAsync("doc2");

        // Check if segments are in database
        var segmentIds = new List<AerialId>();
        await foreach (var id in manager.Database.ListSegmentIdsAsync())
        {
            segmentIds.Add(id);
        }
        Assert.IsTrue(segmentIds.Count > 0);

        // Update doc2
        string doc2UpdatedContent = "# Intro\nThis is doc2 updated.\n\n## Section A\nDoc2 detail updated.";
        await manager.UpdatePageAsync("doc2", doc2UpdatedContent);

        // Check version bumped
        var doc2Xml = Path.Combine(_testDir, "Documents", "doc2.xml");
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(OKDocumentMetadata));
        using (var fs = new FileStream(doc2Xml, FileMode.Open, FileAccess.Read))
        {
            var meta = (OKDocumentMetadata)serializer.Deserialize(fs)!;
            Assert.AreEqual("1.1", meta.Version);
        }

        // Check doc1 link updated
        string doc1UpdatedText = await File.ReadAllTextAsync(Path.Combine(_testDir, "Documents", "doc1.md"));
        Assert.IsTrue(doc1UpdatedText.Contains("ok.TestWiki.wiki/doc2_1.1"));
        Assert.IsFalse(doc1UpdatedText.Contains("ok.TestWiki.wiki/doc2_1.0"));

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task TestGraphMaintenance()
    {
        var manager = new OKWikiManager(_testDir, "TestWiki");
        await manager.InitializeAsync();

        string doc1Content = "# Intro\nThis is doc1.\n\n## Section 1\nLink to [Doc2](ok.TestWiki.wiki/doc2_1.0)";
        string doc2Content = "# Intro\nThis is doc2.\n\n## Section A\nDoc2 detail.";

        manager.CreatePage("doc1", doc1Content, "1.0");
        manager.CreatePage("doc2", doc2Content, "1.0");

        await manager.EmbedPageAsync("doc1");
        await manager.EmbedPageAsync("doc2");

        await manager.MaintainGraphAsync("doc1");
        await manager.MaintainGraphAsync("doc2");

        // 验证 doc1 到 doc2 之间有 DependsOn 边
        var doc2SegId = await FindSegmentIdAsync(manager.Database, "doc2", "1.0");
        Assert.IsNotNull(doc2SegId);

        var doc1SegIds = await FindAllSegmentIdsAsync(manager.Database, "doc1", "1.0");
        Assert.AreEqual(2, doc1SegIds.Count);

        List<GraphEdge> allDoc1Edges = new();
        foreach (var segId in doc1SegIds)
        {
            var edges = manager.Database.GetOutgoingEdges(segId, EdgeKind.DependsOn);
            allDoc1Edges.AddRange(edges);
        }

        Assert.AreEqual(1, allDoc1Edges.Count);
        Assert.AreEqual(doc2SegId.Value, allDoc1Edges[0].TargetId);

        // 现在更新 doc2
        string doc2UpdatedContent = "# Intro\nThis is doc2 updated.\n\n## Section A\nDoc2 detail updated.";
        await manager.UpdatePageAsync("doc2", doc2UpdatedContent);

        // 获取新版本的 doc2 首个 Segment
        var doc2NewSegId = await FindSegmentIdAsync(manager.Database, "doc2", "1.1");
        Assert.IsNotNull(doc2NewSegId);

        // 验证原 doc1 的各段落上，新添了指向 1.1 的 DependsOn 边
        List<GraphEdge> allDoc1EdgesAfterUpdate = new();
        foreach (var segId in doc1SegIds)
        {
            var edges = manager.Database.GetOutgoingEdges(segId, EdgeKind.DependsOn);
            allDoc1EdgesAfterUpdate.AddRange(edges);
        }
        Assert.IsTrue(allDoc1EdgesAfterUpdate.Any(e => e.TargetId == doc2NewSegId.Value));

        await manager.DisposeAsync();
    }

    private async Task<AerialId?> FindSegmentIdAsync(AerialDatabase db, string pageName, string version)
    {
        await foreach (var id in db.ListSegmentIdsAsync())
        {
            var seg = await db.ReadSegmentAsync(id);
            if (seg != null &&
                seg.Metadata.TryGetValue("PageName", out var pName) && pName?.ToString() == pageName &&
                seg.Metadata.TryGetValue("Version", out var ver) && ver?.ToString() == version &&
                seg.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "false")
            {
                return seg.Id;
            }
        }
        return null;
    }

    private async Task<List<AerialId>> FindAllSegmentIdsAsync(AerialDatabase db, string pageName, string version)
    {
        var list = new List<AerialId>();
        await foreach (var id in db.ListSegmentIdsAsync())
        {
            var seg = await db.ReadSegmentAsync(id);
            if (seg != null &&
                seg.Metadata.TryGetValue("PageName", out var pName) && pName?.ToString() == pageName &&
                seg.Metadata.TryGetValue("Version", out var ver) && ver?.ToString() == version &&
                seg.Metadata.TryGetValue("Deprecated", out var dep) && dep?.ToString() == "false")
            {
                list.Add(seg.Id);
            }
        }
        return list;
    }
}
