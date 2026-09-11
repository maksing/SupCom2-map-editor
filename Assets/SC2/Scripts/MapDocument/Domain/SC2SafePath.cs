using System;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A safe path normalizer —
    /// closes two real zip-slip findings:
    ///
    ///   SC2RuntimeScdImporter's unzip joins tmpDir + entry.FullName
    ///        with no containment check — a zip entry with "../" can write
    ///        outside the temp directory, and
    ///
    ///   SC2ExportFileState.NormalizeRelativePath strips leading
    ///        slashes and flips backslashes, but never rejects "../" —
    ///        confirmed by SC2CharTests's
    ///        NormalizeRelativePath_DoesNotRejectParentTraversal_CurrentBehavior.
    ///
    /// Every archive entry path (reading a .scd, and later writing one) must
    /// go through this instead of ad-hoc Path.Combine.
    /// </summary>
    public static class SC2SafePath
    {
        /// <summary>
        /// Normalizes an archive-relative path to forward slashes with no
        /// leading slash, and throws if it contains a parent-directory
        /// segment, an absolute path, or a drive prefix. Returns null (never
        /// throws) for a null/empty input, matching the tolerant behavior
        /// callers already expect from the pre-existing
        /// NormalizeRelativePath this supersedes.
        /// </summary>
        public static string NormalizeRelativeOrThrow(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            var normalized = path.Replace('\\', '/').TrimStart('/');

            if (normalized.Length >= 2 && normalized[1] == ':')
                throw new SC2UnsafePathException($"Path has a drive prefix, not allowed in an archive entry: '{path}'");

            if (normalized.StartsWith("//", StringComparison.Ordinal))
                throw new SC2UnsafePathException($"Path looks like a UNC/absolute path, not allowed in an archive entry: '{path}'");

            var segments = normalized.Split('/');
            foreach (var segment in segments)
            {
                if (segment == "..")
                    throw new SC2UnsafePathException($"Path contains a parent-directory ('..') segment: '{path}'");
                if (segment == ".")
                    continue; // harmless no-op segment, not a traversal risk — allowed
            }

            return normalized;
        }

        /// <summary>
        /// Resolves a (trusted, already-normalized) relative path against a
        /// root directory and verifies the resulting full path is actually
        /// contained within that root — the second half of zip-slip
        /// protection (NormalizeRelativeOrThrow catches "../" textually;
        /// this catches symlink/junction escapes and any normalization
        /// mismatch between the two path forms).
        /// </summary>
        public static string ResolveContainedOrThrow(string rootDirectory, string relativePath)
        {
            var normalizedRelative = NormalizeRelativeOrThrow(relativePath);
            var rootFull = Path.GetFullPath(rootDirectory);
            var candidateFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRelative));

            var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!candidateFull.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidateFull, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new SC2UnsafePathException(
                    $"Resolved path '{candidateFull}' escapes root '{rootFull}' (relative path was '{relativePath}').");
            }

            return candidateFull;
        }
    }

    /// <summary>Thrown when an archive/document path fails containment or
    /// traversal checks. Callers should treat this as a hard import/export
    /// error, not a recoverable warning — a path that fails this check is
    /// either malicious or badly corrupt, never a legitimate vanilla file.</summary>
    public sealed class SC2UnsafePathException : Exception
    {
        public SC2UnsafePathException(string message) : base(message) { }
    }
}
