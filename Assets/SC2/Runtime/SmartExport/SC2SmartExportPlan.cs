#nullable disable
using System.Collections.Generic;

namespace SC2.MapEditor.SmartExport
{
    public sealed class SC2SmartExportPlan
    {
        private readonly List<SC2ExportFileState> _files = new List<SC2ExportFileState>();

        public SC2DirtyFlags DirtyFlags { get; private set; }
        public SC2DirtyFlags ExpandedDirtyFlags { get; private set; }
        public IReadOnlyList<SC2ExportFileState> Files { get { return _files; } }

        public SC2SmartExportPlan(SC2DirtyFlags dirtyFlags)
        {
            DirtyFlags = dirtyFlags;
            ExpandedDirtyFlags = SC2ExportDependencyGraph.ExpandStaleFlags(dirtyFlags);
        }

        public void AddFile(SC2ExportFileState file)
        {
            _files.Add(file);
        }

        public SC2ExportFileState Find(SC2ExportFileKind kind)
        {
            for (var i = 0; i < _files.Count; i++)
            {
                if (_files[i].Kind == kind)
                    return _files[i];
            }

            return null;
        }

        public bool HasBlockingErrors()
        {
            for (var i = 0; i < _files.Count; i++)
            {
                if (_files[i].HasErrors)
                    return true;
            }

            return false;
        }
    }
}

