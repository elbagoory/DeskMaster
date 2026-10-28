namespace DeskMaster.Core.Scanning;

public enum ScanNodeKind
{
    Directory,
    File,

    /// <summary>Many small files of one folder folded into a single entry to save memory.</summary>
    SmallFiles,
}

/// <summary>One folder or file in a scan result tree.</summary>
public sealed class ScanNode
{
    private readonly List<ScanNode> _children = [];

    public ScanNode(string name, string fullPath, ScanNodeKind kind, ScanNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public ScanNodeKind Kind { get; }
    public ScanNode? Parent { get; }

    /// <summary>Total bytes (recursive for folders).</summary>
    public long Size { get; internal set; }

    /// <summary>Number of files (recursive for folders).</summary>
    public long FileCount { get; internal set; }

    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>True when part of this folder could not be read.</summary>
    public bool Incomplete { get; internal set; }

    public bool IsDirectory => Kind == ScanNodeKind.Directory;

    /// <summary>Children sorted by size, largest first.</summary>
    public IReadOnlyList<ScanNode> Children => _children;

    internal void SetChildren(List<ScanNode> children)
    {
        children.Sort((a, b) => b.Size.CompareTo(a.Size));
        _children.Clear();
        _children.AddRange(children);
    }

    /// <summary>Removes this node from the tree (after it was deleted) and shrinks every parent.</summary>
    public void Detach()
    {
        if (Parent is null)
            return;
        Parent._children.Remove(this);
        foreach (var ancestor in Ancestors())
        {
            ancestor.Size -= Size;
            ancestor.FileCount -= FileCount;
        }
    }

    public IEnumerable<ScanNode> Ancestors()
    {
        for (var n = Parent; n is not null; n = n.Parent)
            yield return n;
    }

    public override string ToString() => $"{FullPath} ({Size} bytes)";
}
