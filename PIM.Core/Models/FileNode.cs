using System.Collections.Generic;
using PIM.Core.Models;

namespace PIM.Web.Models
{
    /// <summary>
    /// Represents a node in the preview file tree (folder or file).
    /// Used to visualize the future file structure before execution.
    /// </summary>
    public class FileNode
    {
        public string Name { get; set; } = "";
        public bool IsFolder { get; set; }

        /// <summary>
        /// Determines whether the node should be expanded by default in the UI.
        /// </summary>
        public bool IsExpanded { get; set; }

        public List<FileNode> Children { get; set; } = new();
        public bool IsDuplicate { get; set; }
        public bool KeepRecommended { get; set; }
        public string? Status { get; set; }
        public int FileCount { get; set; }
        public Movie? Movie { get; set; }
    }
}