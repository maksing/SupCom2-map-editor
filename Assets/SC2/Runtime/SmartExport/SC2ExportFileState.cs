#nullable disable
using System;
using System.Collections.Generic;

namespace SC2.MapEditor.SmartExport
{
    public sealed class SC2ExportFileState
    {
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _errors = new List<string>();

        public SC2ExportFileKind Kind { get; private set; }
        public string RelativePath { get; private set; }
        public byte[] OriginalBytes { get; private set; }
        public byte[] OutputBytes { get; private set; }
        public SC2DirtyFlags DirtyMask { get; private set; }
        public SC2ExportFileMode Mode { get; private set; }
        public bool IsStale { get; private set; }

        public IReadOnlyList<string> Warnings { get { return _warnings; } }
        public IReadOnlyList<string> Errors { get { return _errors; } }

        public bool HasOriginal { get { return OriginalBytes != null; } }
        public bool HasOutput { get { return OutputBytes != null; } }
        public bool HasErrors { get { return _errors.Count > 0; } }

        public SC2ExportFileState(SC2ExportFileKind kind, string relativePath, byte[] originalBytes, SC2DirtyFlags dirtyMask)
        {
            Kind = kind;
            RelativePath = NormalizeRelativePath(relativePath);
            OriginalBytes = originalBytes;
            DirtyMask = dirtyMask;
            Mode = originalBytes == null ? SC2ExportFileMode.Missing : SC2ExportFileMode.OriginalPassthrough;
        }

        public void SetOutput(byte[] outputBytes, SC2ExportFileMode mode)
        {
            if (mode != SC2ExportFileMode.Patched && mode != SC2ExportFileMode.Regenerated)
                throw new ArgumentException("Output bytes can only be assigned for patched or regenerated files.", "mode");

            OutputBytes = outputBytes;
            Mode = mode;
        }

        public void SetPassthrough()
        {
            if (HasOriginal)
            {
                OutputBytes = null;
                Mode = SC2ExportFileMode.OriginalPassthrough;
                return;
            }
            // 2026-08-29: no original bytes to fall back to. If real output
            // was already computed for this "not dirty" file (e.g. a
            // cheap-to-regenerate mirror copy — see SC2TerrainExporter's
            // uncompiled/ ScriptLua entry — that never carries its own
            // original blob independently of its sibling), KEEP it rather
            // than silently wiping to Missing: a passthrough step with
            // nothing to pass through from should never destroy already-
            // correct data. Only genuinely empty files (no original AND no
            // output ever computed) become Missing.
            if (HasOutput)
            {
                Mode = SC2ExportFileMode.Regenerated;
                return;
            }
            Mode = SC2ExportFileMode.Missing;
        }

        public void SetSkipped()
        {
            OutputBytes = null;
            Mode = SC2ExportFileMode.SkippedBecauseUnchanged;
        }

        public void MarkStale(string reason)
        {
            IsStale = true;
            if (!string.IsNullOrEmpty(reason))
                _warnings.Add(reason);
        }

        public void AddWarning(string message)
        {
            if (!string.IsNullOrEmpty(message))
                _warnings.Add(message);
        }

        public void AddError(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                _errors.Add(message);
                Mode = SC2ExportFileMode.BlockedByValidationError;
            }
        }

        public byte[] GetBytesForWrite()
        {
            if (Mode == SC2ExportFileMode.Patched || Mode == SC2ExportFileMode.Regenerated)
                return OutputBytes;

            if (Mode == SC2ExportFileMode.OriginalPassthrough)
                return OriginalBytes;

            return null;
        }

        public static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path.Replace('\\', '/').TrimStart('/');
        }
    }
}

