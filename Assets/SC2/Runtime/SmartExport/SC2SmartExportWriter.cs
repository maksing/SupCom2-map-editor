#nullable disable
using System;
using System.IO;

namespace SC2.MapEditor.SmartExport
{
    public static class SC2SmartExportWriter
    {
        public static void WriteToFolder(SC2SmartExportPlan plan, string outputFolder)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");

            if (string.IsNullOrEmpty(outputFolder))
                throw new ArgumentException("Output folder is required.", "outputFolder");

            Directory.CreateDirectory(outputFolder);

            for (var i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                var bytes = file.GetBytesForWrite();
                if (bytes == null)
                    continue;

                var safeRelativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var outputPath = Path.Combine(outputFolder, safeRelativePath);
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(outputPath, bytes);
            }

            var reportPath = Path.Combine(outputFolder, "sc2_export_report.md");
            File.WriteAllText(reportPath, SC2SmartExportReport.ToMarkdown(plan));
        }
    }
}

