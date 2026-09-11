#nullable disable
using System.Text;

namespace SC2.MapEditor.SmartExport
{
    public static class SC2SmartExportReport
    {
        public static string ToMarkdown(SC2SmartExportPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SC2 Smart Export Report");
            sb.AppendLine();
            sb.AppendLine("| File | Kind | Mode | Stale | Warnings | Errors |");
            sb.AppendLine("|---|---|---|---|---|---|");

            for (var i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                sb.Append("| ");
                sb.Append(Escape(file.RelativePath));
                sb.Append(" | ");
                sb.Append(file.Kind);
                sb.Append(" | ");
                sb.Append(file.Mode);
                sb.Append(" | ");
                sb.Append(file.IsStale ? "yes" : "no");
                sb.Append(" | ");
                sb.Append(Escape(Join(file.Warnings)));
                sb.Append(" | ");
                sb.Append(Escape(Join(file.Errors)));
                sb.AppendLine(" |");
            }

            return sb.ToString();
        }

        private static string Join(System.Collections.Generic.IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");
                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}

