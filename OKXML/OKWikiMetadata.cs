using System;
using System.Xml.Serialization;

namespace OKXML;

/// <summary>
/// 表示 OKXML 知识库的元数据结构。
/// </summary>
[XmlRoot("OKWikiMetadata")]
public class OKWikiMetadata
{
    /// <summary>
    /// 知识 Wiki 的名称。
    /// </summary>
    public string WikiName { get; set; } = string.Empty;

    /// <summary>
    /// 知识库创建时间。
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 知识库的简介。
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 知识库的作者。
    /// </summary>
    public string? Author { get; set; }
}
