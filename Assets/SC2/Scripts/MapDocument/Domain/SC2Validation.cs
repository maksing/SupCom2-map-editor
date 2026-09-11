using System.Collections.Generic;
using System.Linq;

namespace SC2.MapEditor.Document
{
    public enum SC2ValidationSeverity { Info, Warning, Error }

    /// <summary>One diagnostic
    /// entry. Domain/ObjectId are nullable/empty for container- and
    /// scenario-level messages that don't refer to a specific
    /// in-map object; per-domain validation populates them.</summary>
    public sealed class SC2ValidationMessage
    {
        public SC2ValidationSeverity Severity;
        public string Code;
        public string Domain;
        public string ObjectId;
        public string Message;
        public string SuggestedAction;
    }

    /// <summary>Aggregate of SC2ValidationMessage produced by one operation
    /// (a Probe, an Import, a future domain validation pass).</summary>
    public sealed class SC2ValidationReport
    {
        public readonly List<SC2ValidationMessage> Messages = new();

        public bool HasErrors => Messages.Any(m => m.Severity == SC2ValidationSeverity.Error);
        public bool HasWarnings => Messages.Any(m => m.Severity == SC2ValidationSeverity.Warning);

        public void Add(SC2ValidationSeverity severity, string code, string domain, string objectId, string message, string suggestedAction = null)
        {
            Messages.Add(new SC2ValidationMessage
            {
                Severity = severity,
                Code = code,
                Domain = domain,
                ObjectId = objectId,
                Message = message,
                SuggestedAction = suggestedAction,
            });
        }
    }
}
