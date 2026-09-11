using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Extends Environment's command coverage beyond
    /// SC2SetEnvironmentTexturePathCommand's 5 texture-path strings to the
    /// remaining ~45 numeric/color fields (Sun/Shadow/Water/WaterFx/Fog/
    /// Glow/Camera/Map/Unverified tabs on SC2EnvironmentShell) — all of
    /// which live inside the single flat 508-byte WorkingHeader
    /// (BdfIO.Formats.Info.InfoModel wraps it with computed float/int
    /// accessors, no separate per-field storage on SC2TerrainData either).
    ///
    /// Deliberately ONE whole-header-swap command rather than ~45 separate
    /// per-field commands: that's not a shortcut, it's what the live editing
    /// model already does. SC2EnvironmentShell's own undo stack
    /// (MaybeSnapshot/PushSnapshot) diffs and snapshots the ENTIRE header
    /// every frame, never a single field in isolation — a per-field command
    /// design would be fighting the shape of the real edit surface instead
    /// of matching it.
    /// </summary>
    public sealed class SC2SetEnvironmentHeaderCommand : ISC2DocumentCommand
    {
        public const int HeaderSize = SC2EnvironmentDocument.HeaderSize;

        private readonly byte[] _newHeader;
        private byte[] _previousHeader;

        public SC2SetEnvironmentHeaderCommand(byte[] newHeader)
        {
            if (newHeader == null) throw new ArgumentNullException(nameof(newHeader));
            if (newHeader.Length != HeaderSize)
                throw new ArgumentException($"Expected a {HeaderSize}-byte header, got {newHeader.Length}.", nameof(newHeader));
            _newHeader = (byte[])newHeader.Clone();
        }

        public string Name => "Set Environment.Header";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousHeader = (byte[])document.Environment.WorkingHeader.Clone();
            document.Environment.WorkingHeader = (byte[])_newHeader.Clone();
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Environment.WorkingHeader = (byte[])_previousHeader.Clone();
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Info;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Environment },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
