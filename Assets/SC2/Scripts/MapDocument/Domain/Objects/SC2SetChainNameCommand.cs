using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Objects' "Chains" slice — renames a chain by stable Id,
    /// same shape as SC2SetGraphNameCommand/SC2SetArmyNameCommand. Note
    /// this only renames the CHAIN itself (SC2ObjChain.Name) — the chain's
    /// membership (MarkerNames) is untouched and still linked by marker
    /// name, the documented gap SC2ObjectsValidator's OBJ002 check
    /// guards.</summary>
    public sealed class SC2SetChainNameCommand : ISC2DocumentCommand
    {
        private readonly int _chainId;
        private readonly string _newName;
        private string _previousName;

        public SC2SetChainNameCommand(int chainId, string newName)
        {
            _chainId = chainId;
            _newName = newName ?? "";
        }

        public string Name => $"Rename Chain #{_chainId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var chain = FindChain(document);
            _previousName = chain.Name;
            chain.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindChain(document).Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjChain FindChain(SC2MapDocument document)
        {
            foreach (var chain in document.Objects.Chains)
                if (chain.Id == _chainId)
                    return chain;
            throw new InvalidOperationException($"Chain #{_chainId} not found.");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Objects;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Objects },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
