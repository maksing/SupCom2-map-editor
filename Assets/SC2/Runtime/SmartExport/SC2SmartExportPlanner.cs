#nullable disable
using System.Collections.Generic;

namespace SC2.MapEditor.SmartExport
{
    public static class SC2SmartExportPlanner
    {
        public static SC2SmartExportPlan BuildPlan(IEnumerable<SC2ExportFileState> files, SC2DirtyFlags dirtyFlags)
        {
            var plan = new SC2SmartExportPlan(dirtyFlags);

            foreach (var file in files)
            {
                var needsWrite = SC2ExportDependencyGraph.IsDirtyOrStale(plan.ExpandedDirtyFlags, file.DirtyMask);
                if (!needsWrite)
                {
                    file.SetPassthrough();
                    plan.AddFile(file);
                    continue;
                }

                var staleReason = SC2ExportDependencyGraph.GetStaleReason(dirtyFlags, file.DirtyMask);
                if (!string.IsNullOrEmpty(staleReason))
                    file.MarkStale(staleReason);

                if (file.HasOutput)
                {
                    plan.AddFile(file);
                    continue;
                }

                if (file.HasOriginal && dirtyFlags == SC2DirtyFlags.None)
                {
                    file.SetPassthrough();
                }
                else
                {
                    file.AddError("File is dirty or stale but no regenerated/patched bytes were provided.");
                }

                plan.AddFile(file);
            }

            return plan;
        }

        public static SC2ExportFileState CreateOriginal(
            SC2ExportFileKind kind,
            string relativePath,
            byte[] originalBytes)
        {
            var masks = SC2ExportDependencyGraph.CreateDefaultFileMasks();
            SC2DirtyFlags mask;
            if (!masks.TryGetValue(kind, out mask))
                mask = SC2DirtyFlags.None;

            return new SC2ExportFileState(kind, relativePath, originalBytes, mask);
        }
    }
}

