namespace AerialCity.Core.Primitives;

/// <summary>
/// Classifies the type of content a <see cref="Segment"/> represents.
/// Each kind implies different segmentation strategies, embedding approaches,
/// and retrieval heuristics within the AerialCity pipeline.
/// </summary>
public enum SegmentKind
{
    /// <summary>
    /// A block of source code extracted via AST analysis.
    /// Typically represents a function, class, method, or logical code unit.
    /// Supports caller/callee tracing through the graph store.
    /// </summary>
    CodeBlock = 0,

    /// <summary>
    /// A passage of natural language text extracted via passage segmentation.
    /// Represents a semantically coherent unit of prose (paragraph, section, etc.).
    /// Supports entity mention tracing through the graph store.
    /// </summary>
    TextPassage = 1,

    /// <summary>
    /// A temporal segment of video content.
    /// Represents a clip defined by start/end timestamps with associated metadata.
    /// </summary>
    VideoClip = 2,

    /// <summary>
    /// A segment representing structured data (JSON, XML, tables, etc.).
    /// </summary>
    StructuredData = 3,

    /// <summary>
    /// An image or visual content segment.
    /// Requires multimodal embedding for retrieval.
    /// </summary>
    Image = 4,

    /// <summary>
    /// A user-defined or unclassified segment type.
    /// </summary>
    Custom = 255
}
